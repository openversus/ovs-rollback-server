#!/usr/bin/env bash
# Synthetic players, each behind its own P2P node, on the local bench.
#
#   p2p-bench.sh up        publish the node and the rendezvous, start the rendezvous on this machine (UDP 41235),
#                          start synthetic players $PLAYERS (default "1 2": they queue 1v1 and match each other), and
#                          start a node in each player's network namespace; the player connects to 127.0.0.1:57000,
#                          its node, and reports that port at /api/identify as the client does.
#                          Per-player settings: SYNTHETIC_ARGS_N (exported), e.g. SYNTHETIC_ARGS_3="--Queue:Mode casual".
#   p2p-bench.sh logs N    follow player N's node
#   p2p-bench.sh rdv       follow the rendezvous
#   p2p-bench.sh down      stop the nodes, the players and the rendezvous
#   p2p-bench.sh me        for a game on this machine against synthetic player 1: stops the bench's rollback server
#                          (host UDP 57000), starts the rendezvous, a node on this machine at 57000 (the game connects
#                          to 127.0.0.1:57000 as the bench's notification says), and player 1 behind its own node.
#   p2p-bench.sh me-down   undoes "me": stops everything and starts the bench's rollback server again.
#
# Fallback test: NO_RDV=1 RELAY=<gateway>:57000 p2p-bench.sh up points the nodes at a dead rendezvous port; after
# the punch timeout both forward to the bench's rollback server on the host, as the relay. NODE_ENV (every node) and
# NODE_ENV_N (node N only, exported) add docker -e options, e.g. NODE_ENV_4="-e Node__Rendezvous=<gateway>:9" leaves only
# node 4 without a rendezvous.
#
# Needs the bench up (ovs-http-server/dotnet/local/bench.sh status) and ~/git/ovs-synthetic-client. The bench's own
# rollback server on the host's UDP 57000 is not used: each node is the rollback server for its player, and the host
# node runs the engine for both. Match config comes from the bench's /ovs_register (checked by match key).
set -euo pipefail
cd "$(dirname "$0")/../.."

SYNTHETIC=${SYNTHETIC:-$HOME/git/ovs-synthetic-client}
NETWORK=ovs-synthetic
IMAGE=ovs-rollback-node
RDV_PORT=${RDV_PORT:-41235}
GAME_PORT=${GAME_PORT:-57000}
LOGS=${LOGS:-/tmp/p2p-bench}
mkdir -p "$LOGS"

# The bench's nodes are unlocked (every per-node override above keeps working) and trust the bench's own key pair,
# local/pki/bench (made here on first use if missing); the bench servers sign with its private half (ovs-http-server's
# bench.sh passes P2P_NODE_SIGNING_KEY_FILE to the TS server and C# match flow).
BENCH_KEYS=${BENCH_KEYS:-local/pki/bench}
publish() {
  [ -f "$BENCH_KEYS/node-config-public-key.txt" ] || OVSRollbackNode/tools/new-signing-key.sh "$BENCH_KEYS" >/dev/null
  dotnet publish OVSRollbackNode/OVSRollbackNode.csproj -c Release -r linux-x64 --self-contained false -p:PublishReadyToRun=false \
    -p:NodeUnlocked=true "-p:NodeConfigPublicKey=$(tr -d '[:space:]' < "$BENCH_KEYS/node-config-public-key.txt")" -o out/node --nologo -v q
  dotnet publish OVSRendezvous/OVSRendezvous.csproj -c Release -o out/rendezvous --nologo -v q
  docker build -q -t "$IMAGE" -f OVSRollbackNode/Dockerfile . >/dev/null
}

gateway() {
  docker network inspect -f '{{range .IPAM.Config}}{{.Gateway}}{{end}}' "$NETWORK"
}

case "${1:-}" in
  up|me)
    publish
    # The rendezvous, on this machine, reachable from the containers at the network's gateway.
    if [ "${NO_RDV:-}" = 1 ]; then
      echo "NO_RDV: no rendezvous; nodes point at a dead port and must fall back to ${RELAY:-nothing}"
    elif [ -f "$LOGS/rendezvous.pid" ] && kill -0 "$(cat "$LOGS/rendezvous.pid")" 2>/dev/null; then
      echo "rendezvous already running (pid $(cat "$LOGS/rendezvous.pid"))"
    else
      nohup dotnet out/rendezvous/OVS.Rollback.Rendezvous.dll "$RDV_PORT" >"$LOGS/rendezvous.log" 2>&1 &
      echo $! >"$LOGS/rendezvous.pid"
      echo "rendezvous: pid $! on UDP $RDV_PORT"
    fi
    if [ "$1" = me ]; then
      # This machine's node takes the bench rollback server's port; the game is told 127.0.0.1:57000 already.
      "$HOME/git/ovs-http-server/dotnet/local/bench.sh" down rollback
      Server__BaseUrl=http://127.0.0.1:18000 OVS_SERVER=http://127.0.0.1:18000 \
        Node__Rendezvous="127.0.0.1:$RDV_PORT" Node__RelayFallback="${RELAY:-}" \
        nohup dotnet out/node/OVS.Rollback.Node.dll "$GAME_PORT" >"$LOGS/node-me.log" 2>&1 &
      echo $! >"$LOGS/node-me.pid"
      echo "node (this machine): pid $! on UDP $GAME_PORT, log $LOGS/node-me.log"
      PLAYERS=1
    else
      PLAYERS=${PLAYERS:-1 2}
    fi
    # The players: each connects to its own loopback instead of the address the notification names. A host node
    # answers its game only once every peer path is open, and a real game sends its first NewConnection some 15 s
    # after the perks lock (map loading); the synthetic connects at once, so it waits like the real game does (45 s).
    # shellcheck disable=SC2086
    SYNTHETIC_ARGS="${SYNTHETIC_ARGS:-} --Rollback:Host 127.0.0.1 --Rollback:ConnectTimeoutSeconds 45 --Node:Port $GAME_PORT" "$SYNTHETIC/run.sh" up $PLAYERS
    GW=$(gateway)
    RDV="$GW:$RDV_PORT"; [ "${NO_RDV:-}" = 1 ] && RDV="$GW:9"
    for n in $PLAYERS; do
      own="NODE_ENV_$n"
      docker rm -f "$IMAGE-$n" >/dev/null 2>&1 || true
      docker run -d --name "$IMAGE-$n" --network "container:ovs-synthetic-$n" \
        -e Server__BaseUrl="http://$GW:18000" -e OVS_SERVER="http://$GW:18000" \
        -e Node__Rendezvous="$RDV" -e Node__RelayFallback="${RELAY:-}" \
        ${NODE_ENV:-} ${!own:-} "$IMAGE" "$GAME_PORT" >/dev/null
      echo "node $n: in player $n's namespace, game port $GAME_PORT"
    done
    ;;
  me-down)
    if [ -f "$LOGS/node-me.pid" ]; then kill "$(cat "$LOGS/node-me.pid")" 2>/dev/null || true; rm -f "$LOGS/node-me.pid"; fi
    "$0" down
    "$HOME/git/ovs-http-server/dotnet/local/bench.sh" up rollback
    ;;
  logs)
    docker logs -f "$IMAGE-${2:?player number}"
    ;;
  rdv)
    tail -f "$LOGS/rendezvous.log"
    ;;
  down)
    ids=$(docker ps -aq --filter "name=^$IMAGE-")
    # shellcheck disable=SC2086
    [ -n "$ids" ] && docker rm -f $ids >/dev/null
    "$SYNTHETIC/run.sh" down
    if [ -f "$LOGS/rendezvous.pid" ]; then kill "$(cat "$LOGS/rendezvous.pid")" 2>/dev/null || true; rm -f "$LOGS/rendezvous.pid"; fi
    echo "stopped"
    ;;
  *)
    sed -n '2,20p' "$0"
    exit 1
    ;;
esac

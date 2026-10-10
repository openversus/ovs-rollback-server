#!/usr/bin/env bash
# Synthetic players, each behind its own P2P node, on the local bench.
#
#   p2p-bench.sh up        publish the node, start synthetic players $PLAYERS (default "1 2": they queue 1v1 and match
#                          each other; PLAYERS=1 for a game against the OpenVersus client, which brings its own node),
#                          and start a node in each player's network namespace; the player connects to 127.0.0.1:57000,
#                          its node, and reports that port at /api/identify as the client does.
#                          Per-player settings: SYNTHETIC_ARGS_N (exported), e.g. SYNTHETIC_ARGS_3="--Queue:Mode casual".
#   p2p-bench.sh logs N    follow player N's node
#   p2p-bench.sh rdv       follow the rendezvous
#   p2p-bench.sh down      stop the nodes and the players (and a rendezvous this script started)
#
# Fallback test: NO_RDV=1 RELAY=<gateway>:57000 p2p-bench.sh up points the nodes at a dead rendezvous port; after
# the punch timeout both forward to the bench's rollback server on the host, as the relay. NODE_ENV (every node) and
# NODE_ENV_N (node N only, exported) add docker -e options, e.g. NODE_ENV_4="-e Node__Rendezvous=<gateway>:9" leaves only
# node 4 without a rendezvous.
#
# Needs the bench's compose stack up (ovs-http-server dotnet/local/compose/bench-compose.sh ps) and
# ~/git/ovs-synthetic-client. The players join the stack's network (NETWORK, default ovs; run.sh) and the nodes share
# their namespaces: they reach the match flow through the router (NODE_SERVER, default http://router:8000), and the
# rendezvous and the relay, which run on the host's network, at the network's gateway. The stack's rendezvous
# (openversus-rendezvous-1) is used when it runs; otherwise this script starts one on the host (UDP 41235). A game on
# this machine is seen by the rendezvous at the address its node used to reach it: for a game against these players,
# the client's P2PRegistry must be an address of this machine that a container can reach (not 127.0.0.1), or the
# rendezvous hands the players' nodes a loopback address. The bench's own rollback server on the host's UDP 57000 is
# not used: each node is the rollback server for its player, and the host node runs the engine for both. Match config
# comes from the bench's /ovs_register (checked by match key).
# The bench's nodes are unlocked (every per-node override above keeps working) and trust the bench's own key pair,
# local/pki/bench (made here on first use if missing); the bench's match flow signs with its private half (the stack
# mounts this directory at /keys: OVS_KEYS_DIR).
set -euo pipefail
cd "$(dirname "$0")/../.."

SYNTHETIC=${SYNTHETIC:-$HOME/git/ovs-synthetic-client}
export NETWORK=${NETWORK:-ovs}
NODE_SERVER=${NODE_SERVER:-http://router:8000}
STACK_RDV=${STACK_RDV:-openversus-rendezvous-1}
IMAGE=ovs-rollback-node
RDV_PORT=${RDV_PORT:-41235}
GAME_PORT=${GAME_PORT:-57000}
LOGS=${LOGS:-/tmp/p2p-bench}
mkdir -p "$LOGS"

BENCH_KEYS=${BENCH_KEYS:-local/pki/bench}
publish() {
  [ -f "$BENCH_KEYS/node-config-public-key.txt" ] || OVSRollbackNode/tools/new-signing-key.sh "$BENCH_KEYS" >/dev/null
  dotnet publish OVSRollbackNode/OVSRollbackNode.csproj -c Release -r linux-x64 --self-contained false -p:PublishReadyToRun=false \
    -p:NodeUnlocked=true "-p:NodeTrust=$NODE_SERVER $(tr -d '[:space:]' < "$BENCH_KEYS/node-config-public-key.txt")" -o out/node --nologo -v q
  stack_rdv || dotnet publish OVSRendezvous/OVSRendezvous.csproj -c Release -o out/rendezvous --nologo -v q
  docker build -q -t "$IMAGE" -f OVSRollbackNode/Dockerfile . >/dev/null
}

gateway() {
  docker network inspect -f '{{range .IPAM.Config}}{{.Gateway}}{{end}}' "$NETWORK"
}

# Whether the stack's rendezvous is running.
stack_rdv() {
  [ "$(docker inspect -f '{{.State.Running}}' "$STACK_RDV" 2>/dev/null)" = true ]
}

case "${1:-}" in
  up)
    if ! docker network inspect "$NETWORK" >/dev/null 2>&1; then
      echo "no network $NETWORK: is the bench stack up?" >&2
      exit 1
    fi
    publish
    # The rendezvous, on this machine, reachable from the containers at the network's gateway.
    if [ "${NO_RDV:-}" = 1 ]; then
      echo "NO_RDV: no rendezvous; nodes point at a dead port and must fall back to ${RELAY:-nothing}"
    elif stack_rdv; then
      echo "rendezvous: the stack's ($STACK_RDV)"
    elif [ -f "$LOGS/rendezvous.pid" ] && kill -0 "$(cat "$LOGS/rendezvous.pid")" 2>/dev/null; then
      echo "rendezvous already running (pid $(cat "$LOGS/rendezvous.pid"))"
    else
      nohup dotnet out/rendezvous/OVS.Rollback.Rendezvous.dll "$RDV_PORT" >"$LOGS/rendezvous.log" 2>&1 &
      echo $! >"$LOGS/rendezvous.pid"
      echo "rendezvous: pid $! on UDP $RDV_PORT"
    fi
    PLAYERS=${PLAYERS:-1 2}
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
        -e Server__BaseUrl="$NODE_SERVER" -e OVS_SERVER="$NODE_SERVER" \
        -e Node__Rendezvous="$RDV" -e Node__RelayFallback="${RELAY:-}" \
        ${NODE_ENV:-} ${!own:-} "$IMAGE" "$GAME_PORT" >/dev/null
      echo "node $n: in player $n's namespace, game port $GAME_PORT"
    done
    ;;
  logs)
    docker logs -f "$IMAGE-${2:?player number}"
    ;;
  rdv)
    if stack_rdv; then docker logs -f "$STACK_RDV"; else tail -f "$LOGS/rendezvous.log"; fi
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
    sed -n '2,30p' "$0"
    exit 1
    ;;
esac

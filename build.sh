#!/usr/bin/env bash
# Builds the OpenVersus rollback server and everything that ships beside it.
#
#   ./build.sh                        everything: the tests, the P2P node for win-x64 and linux-x64, the rendezvous
#                                     service for linux-x64 (plus a tarball to carry to the server), and the rollback
#                                     server's Docker image
#   ./build.sh TAG [CONTEXT]          the same, with the image tagged ovs-rollback-server-csharp:TAG and built from
#                                     CONTEXT (this script's original form; TAG defaults to ready-to-run, CONTEXT to .)
#   ./build.sh test                   the unit tests only
#   ./build.sh node [RID ...]         the node only (default: win-x64 linux-x64)      -> out/node-<RID>/
#   ./build.sh rendezvous             the rendezvous only                             -> out/rendezvous-linux-x64/, .tar.gz
#   ./build.sh publish [RID ...]      node and rendezvous, no tests, no image
#   ./build.sh image [TAG] [CONTEXT]  the Docker image only
#
# Every build in a run is stamped with the same version (git commit date, branch, short hash) and compile time, as the
# image always was; the two stamped source files are put back as they were when the script ends, edits included.
# EXPERIMENTAL_BUILD=<label> makes the server log "EXPERIMENTAL BUILD: <label>" at startup.
#
# Needs the .NET 10 SDK; the image also needs Docker. The node is self-contained ReadyToRun, one file per platform, so
# players install no runtime; the rendezvous is the same for the server it runs on.
#
# The node is locked (NodeLockdown): it talks only to the servers of the environments in NODE_PKI (default "prod
# testing", the first being the default), each read from pki/<env>/: server-url.txt and node-config-public-key.txt (the
# public half of the pair OVSRollbackNode/tools/new-signing-key.sh makes). The mod's ServerUrl picks among them, and the
# node takes configs only with that server's signature.
set -euo pipefail
cd "$(dirname "$0")"

say() { printf '\033[1m== %s\033[0m\n' "$*"; }
fail() { echo "build.sh: $*" >&2; exit 1; }

command=all
TAGNAME=""
CONTEXT=""
case "${1:-}" in
    test|node|rendezvous|publish|image|all) command=$1; shift ;;
    -h|--help|help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) ;;   # the original form: build.sh [TAG] [CONTEXT]
esac
case "$command" in
    image|all) TAGNAME=${1:-ready-to-run}; CONTEXT=${2:-.} ;;
esac

if [[ -n "${EXPERIMENTAL_BUILD:-}" && ! "$EXPERIMENTAL_BUILD" =~ ^[A-Za-z0-9._-]+$ ]]; then
    fail "EXPERIMENTAL_BUILD may only contain letters, digits, '.', '_' and '-'"
fi
command -v dotnet >/dev/null || fail "dotnet is not on PATH; install the .NET 10 SDK from https://dotnet.microsoft.com/download"

# ── Version stamp: the placeholders in Statics.cs and CompileTime.cs become this run's values for every build, and the
# files go back to exactly what they were (not to HEAD: an edit in progress survives) when the script ends. ──
STATICS=OVSRollbackServer/Common/Statics.cs
COMPILETIME_CS=OVSRollbackServer/Common/CompileTime.cs
stamp_backup=$(mktemp -d)
cp "$STATICS" "$COMPILETIME_CS" "$stamp_backup/"
trap 'cp "$stamp_backup/Statics.cs" "$STATICS"; cp "$stamp_backup/CompileTime.cs" "$COMPILETIME_CS"; rm -rf "$stamp_backup"' EXIT
COMPILETIME="$(date +'%Y-%m-%d %H:%M:%S')"
GITBASEDVERSION="$(git log -1 --format="%at" | xargs -I{} date -d @{} +%Y.%m.%d.%H%M%S)-$(git rev-parse --abbrev-ref HEAD)-$(git rev-parse --short HEAD)"
sed -Ei "s|1\.0\.0-default-version|$GITBASEDVERSION|ig" "$STATICS"
sed -Ei "s|DefaultCompileDateTime|$COMPILETIME|ig" "$COMPILETIME_CS"
if [[ -n "${EXPERIMENTAL_BUILD:-}" ]]; then
    # Anchored on the declaration: the IsExperimentalBuild comparison holds the same placeholder.
    sed -Ei "s|(ExperimentalBuild \{ get; \} = )\"DefaultExperimentalBuild\";|\1\"$EXPERIMENTAL_BUILD\";|" "$STATICS"
fi
say "version $GITBASEDVERSION, compiled $COMPILETIME${EXPERIMENTAL_BUILD:+, EXPERIMENTAL BUILD $EXPERIMENTAL_BUILD}"

do_test() {
    say "running the tests"
    dotnet test OVSRollbackP2P.Tests/OVSRollbackP2P.Tests.csproj -c Release --nologo -v q
}

# The P2P node players run beside the game: self-contained, single file, ReadyToRun, no runtime to install.
# Linux is a first-class target (Steam Deck and desktop Linux players: the mod starts the ELF from inside Proton).
do_node() {
    local rids=${*:-"win-x64 linux-x64"}
    local trust="" env file
    for env in ${NODE_PKI:-prod testing}; do
        for file in pki/$env/server-url.txt pki/$env/node-config-public-key.txt; do
            [ -s "$file" ] || fail "$file is missing or empty: each environment the node trusts needs its server URL and public key (OVSRollbackNode/tools/new-signing-key.sh DIR makes a key pair; the private half goes to that server as P2P_NODE_SIGNING_KEY_FILE)"
        done
        trust+="$(tr -d '[:space:]' < "pki/$env/server-url.txt") $(tr -d '[:space:]' < "pki/$env/node-config-public-key.txt") "
    done
    say "the node trusts: $(for env in ${NODE_PKI:-prod testing}; do printf '%s (%s) ' "$env" "$(tr -d '[:space:]' < "pki/$env/server-url.txt")"; done)"
    for rid in $rids; do
        say "publishing the node for $rid"
        dotnet publish OVSRollbackNode/OVSRollbackNode.csproj -c Release -r "$rid" --self-contained true \
            -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true \
            "-p:NodeTrust=$trust" -o "out/node-$rid" --nologo -v q
        local exe
        exe=$(ls "out/node-$rid" | grep -E '^OVS\.Rollback\.Node(\.exe)?$') || fail "out/node-$rid has no node executable"
        say "node $rid: out/node-$rid/$exe ($(du -sh "out/node-$rid" | cut -f1))"
    done
}

# The rendezvous pairs the nodes of a match; it runs on the server (the relay VM), so linux-x64 only, and the tarball
# is what gets carried there: unpack it and run ./OVS.Rollback.Rendezvous [port] (41235 by default).
do_rendezvous() {
    say "publishing the rendezvous for linux-x64"
    dotnet publish OVSRendezvous/OVSRendezvous.csproj -c Release -r linux-x64 --self-contained true \
        -p:PublishSingleFile=true -o out/rendezvous-linux-x64 --nologo -v q
    [ -x out/rendezvous-linux-x64/OVS.Rollback.Rendezvous ] || fail "out/rendezvous-linux-x64 has no rendezvous executable"
    tar -C out -czf out/rendezvous-linux-x64.tar.gz rendezvous-linux-x64
    say "rendezvous linux-x64: out/rendezvous-linux-x64/OVS.Rollback.Rendezvous ($(du -sh out/rendezvous-linux-x64 | cut -f1)), packed as out/rendezvous-linux-x64.tar.gz ($(du -h out/rendezvous-linux-x64.tar.gz | cut -f1))"
}

do_image() {
    command -v docker >/dev/null || fail "docker is not on PATH; the image needs it (build.sh publish builds everything else)"
    say "building the Docker image ovs-rollback-server-csharp:$TAGNAME from $CONTEXT"
    docker build -t "ovs-rollback-server-csharp:$TAGNAME" "$CONTEXT"
}

case "$command" in
    test) do_test ;;
    node) do_node "$@" ;;
    rendezvous) do_rendezvous ;;
    publish) do_node "$@"; do_rendezvous ;;
    image) do_image ;;
    all) do_test; do_node; do_rendezvous; do_image ;;
esac
say "done"

#!/usr/bin/env bash

cd "$(dirname "$0")"

TAGNAME=$1
CONTEXT=$2
# Optional: EXPERIMENTAL_BUILD=<label> ./build.sh ... makes the server log "EXPERIMENTAL BUILD: <label>" at startup.
if [[ -n "${EXPERIMENTAL_BUILD:-}" && ! "$EXPERIMENTAL_BUILD" =~ ^[A-Za-z0-9._-]+$ ]]; then
    echo "EXPERIMENTAL_BUILD may only contain letters, digits, '.', '_' and '-'" >&2
    exit 1
fi
COMPILETIME="$(date +'%Y-%m-%d %H:%M:%S')"
GITBASEDVERSION="$(git log -1 --format="%at" | xargs -I{} date -d @{} +%Y.%m.%d.%H%M%S)-$(git rev-parse --abbrev-ref HEAD)-$(git rev-parse --short HEAD)"
trap 'git restore OVSRollbackServer/Common/CompileTime.cs OVSRollbackServer/Common/Statics.cs' EXIT
sed -Ei "s|1\.0\.0-default-version|$GITBASEDVERSION|ig" OVSRollbackServer/Common/Statics.cs
sed -Ei "s|DefaultCompileDateTime|$COMPILETIME|ig" OVSRollbackServer/Common/CompileTime.cs
if [[ -n "${EXPERIMENTAL_BUILD:-}" ]]; then
    # Anchored on the declaration: the IsExperimentalBuild comparison holds the same placeholder.
    sed -Ei "s|(ExperimentalBuild \{ get; \} = )\"DefaultExperimentalBuild\";|\1\"$EXPERIMENTAL_BUILD\";|" OVSRollbackServer/Common/Statics.cs
fi

docker build -t "ovs-rollback-server-csharp:${TAGNAME:-ready-to-run}" "${CONTEXT:-.}"


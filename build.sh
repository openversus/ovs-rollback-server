#!/usr/bin/env bash

cd "$(dirname "$0")"

TAGNAME=$1
CONTEXT=$2
COMPILETIME="$(date +'%Y-%m-%d %H:%M:%S')"
GITBASEDVERSION="$(git log -1 --format="%at" | xargs -I{} date -d @{} +%Y.%m.%d.%H%M%S)-$(git rev-parse --abbrev-ref HEAD)-$(git rev-parse --short HEAD)"
trap 'git restore OVSRollbackServer/Common/CompileTime.cs OVSRollbackServer/Common/Statics.cs' EXIT
sed -Ei "s|1\.0\.0-default-version|$GITBASEDVERSION|ig" OVSRollbackServer/Common/Statics.cs
sed -Ei "s|DefaultCompileDateTime|$COMPILETIME|ig" OVSRollbackServer/Common/CompileTime.cs

docker build -t "ovs-rollback-server-csharp:${TAGNAME:-ready-to-run}" "${CONTEXT:-.}"


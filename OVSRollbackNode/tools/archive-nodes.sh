#!/usr/bin/env bash
# Packs the published nodes (build.sh node: out/node-<RID>/ and out/OVS.Rollback.Node.sh) into the archives a client
# release carries, one per platform, each extracting into plugins/OpenVersus/ (node/<RID>/... and, for the Unix ones,
# node/OVS.Rollback.Node.sh, the launcher that picks a Mac's node):
#
#   archive-nodes.sh VERSION OUTDIR [RID ...]     OUTDIR/OVS_Node_<VERSION>_win-x64.zip, OVS_Node_<VERSION>_linux-x64.tar.gz,
#                                                 OVS_Node_<VERSION>_osx-x64.tar.gz, OVS_Node_<VERSION>_osx-arm64.tar.gz,
#                                                 and SHA256SUMS-node beside them
#
# The names are the server's contract (ClientReleaseManifest: OVS_Node_<version>_<rid>.<zip|tar.gz>); the version must be
# the release's. Debug symbols are left out. Tarballs keep the executable bits; the zip is for Windows, where none matter.
set -euo pipefail
cd "$(dirname "$0")/../.."
version=${1:?version}; out=${2:?output directory}; shift 2
rids=${*:-"win-x64 linux-x64 osx-x64 osx-arm64"}
mkdir -p "$out"
out=$(cd "$out" && pwd)   # absolute: the zip is written from inside the staging folder
stage=$(mktemp -d); trap 'rm -rf "$stage"' EXIT
for rid in $rids; do
    src="out/node-$rid"
    exe=$(ls "$src" | grep -E '^OVS\.Rollback\.Node(\.exe)?$') || { echo "$src has no node executable (build.sh node $rid first)" >&2; exit 1; }
    rm -rf "$stage/node"; mkdir -p "$stage/node/$rid"
    (cd "$src" && find . -type f ! -name '*.pdb' -exec cp --parents {} "$stage/node/$rid/" \;)
    case "$rid" in
        win-x64)
            name="OVS_Node_${version}_$rid.zip"
            rm -f "$out/$name"
            (cd "$stage" && zip -q -r -X "$out/$name" node)
            ;;
        *)
            [ -f out/OVS.Rollback.Node.sh ] || { echo "out/OVS.Rollback.Node.sh is missing (build.sh node writes it)" >&2; exit 1; }
            install -m 755 out/OVS.Rollback.Node.sh "$stage/node/OVS.Rollback.Node.sh"
            chmod 755 "$stage/node/$rid/$exe"
            name="OVS_Node_${version}_$rid.tar.gz"
            tar -C "$stage" --owner=0 --group=0 --numeric-owner -czf "$out/$name" node
            ;;
    esac
    echo "$name ($(du -h "$out/$name" | cut -f1))"
done
(cd "$out" && sha256sum -b OVS_Node_"$version"_* > SHA256SUMS-node && cat SHA256SUMS-node)

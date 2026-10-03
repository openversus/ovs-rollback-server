#!/usr/bin/env bash
# Publishes the node for players: one self-contained executable per platform, ReadyToRun, no runtime to install.
#
#   OVSRollbackNode/publish.sh            -> out/node-win-x64/OVS.Rollback.Node.exe, out/node-linux-x64/OVS.Rollback.Node
#   OVSRollbackNode/publish.sh linux-x64  -> one of them
#
# Linux is a first-class target (Steam Deck and desktop Linux players): the mod can start the ELF from inside
# Proton with Wine's `start /unix`. The rendezvous is published for linux-x64 only (it runs on the servers).
# NativeAOT is a follow-up: the engine's native-aot branch has the needed changes and was validated but never shipped.
set -euo pipefail
cd "$(dirname "$0")/.."
RIDS=${1:-"win-x64 linux-x64"}
for rid in $RIDS; do
  dotnet publish OVSRollbackNode/OVSRollbackNode.csproj -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "out/node-$rid" --nologo -v q
  echo "node $rid: $(ls out/node-$rid | grep -E '^OVS\.Rollback\.Node(\.exe)?$') ($(du -sh out/node-$rid | cut -f1))"
done
dotnet publish OVSRendezvous/OVSRendezvous.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -o out/rendezvous-linux-x64 --nologo -v q
echo "rendezvous linux-x64: $(du -sh out/rendezvous-linux-x64 | cut -f1)"

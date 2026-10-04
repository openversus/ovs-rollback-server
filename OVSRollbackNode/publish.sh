#!/usr/bin/env bash
# The node and rendezvous builds live in ../build.sh now; this stays for the hands that know it.
#
#   OVSRollbackNode/publish.sh            -> build.sh publish            (node for win-x64 and linux-x64, then the rendezvous)
#   OVSRollbackNode/publish.sh linux-x64  -> build.sh publish linux-x64  (one platform)
set -euo pipefail
exec "$(dirname "$0")/../build.sh" publish "$@"

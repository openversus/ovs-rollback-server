#!/bin/sh
# Starts the rollback node built for this machine, from the node folder this script sits in:
#   macOS, Apple silicon  -> osx-arm64/OVS.Rollback.Node
#   macOS, Intel          -> osx-x64/OVS.Rollback.Node
#   anything else         -> linux-x64/OVS.Rollback.Node
# The mod runs this through Wine's start /unix on macOS, where the Mac's architecture is not visible from inside Wine.
# Under Rosetta a translated shell reports x86_64 from uname -m, so Apple silicon is read from sysctl instead:
# hw.optional.arm64 (1 on Apple silicon, absent on Intel) or sysctl.proc_translated (1 when this shell itself runs
# translated, which only happens on Apple silicon; Apple's documented Rosetta check). uname -s is POSIX. Anything
# unexpected, including no answer at all, runs the Linux x64 node: Linux (Steam Deck and desktop) is by far the larger
# install base, and on Linux the mod starts that node directly anyway.
dir=$(cd "$(dirname "$0")" && pwd)
case "$(uname -s 2>/dev/null)" in
    Darwin)
        if [ "$(sysctl -n hw.optional.arm64 2>/dev/null)" = 1 ] || [ "$(sysctl -n sysctl.proc_translated 2>/dev/null)" = 1 ]; then
            node="$dir/osx-arm64/OVS.Rollback.Node"
        else
            node="$dir/osx-x64/OVS.Rollback.Node"
        fi
        # The updater installs one Mac build; a hand install may hold the other: run whichever is there.
        if [ ! -f "$node" ]; then
            for other in "$dir/osx-arm64/OVS.Rollback.Node" "$dir/osx-x64/OVS.Rollback.Node"; do
                [ -f "$other" ] && node="$other" && break
            done
        fi
        ;;
    *)
        node="$dir/linux-x64/OVS.Rollback.Node"
        ;;
esac
cd "$(dirname "$node")" && exec "$node" "$@"

#!/usr/bin/env bash
# Load a TACT save and run a scenario's assertions; results land in
# test/SaveData/test-results-<scenario>.json in the compat repo profile.
# Usage: ./test/run-tact-assert.sh tact1 TACT-1-reload-abort
set -euo pipefail
SCENARIO="${1:?scenario (tact1|tact2|tact3)}"
SAVE="${2:?save name}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
COMPAT="$HOME/Projects/CombatExtended-SimpleSidearms Compatibility Patch"
RIMWORLD="$HOME/.local/share/Steam/steamapps/common/RimWorld/RimWorldLinux"
# GS_WRAP: launch inside gamescope's nested compositor — immune to the desktop's
# display state (owner gaming via Proton, mode-list churn, XF86VidMode crashes).
GS=(gamescope -W 1600 -H 900 --)
SAVEDATA="$COMPAT/test/SaveData"
RESULT="$SAVEDATA/test-results-$SCENARIO.json"
if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
    dotnet build "$REPO/Source/CESSCompatTactics/CESSCompatTactics.csproj" -c Release
    dotnet build "$REPO/test/StagingMod/Source/TacticsTestStaging.csproj" -c Release
fi
rm -f "$RESULT"
timeout --signal=TERM 20m "${GS[@]}" "$RIMWORLD" -savedatafolder="$SAVEDATA" \
    "-celoadsave=$SAVE" "-ceassert=$SCENARIO" || true
if [[ -f "$RESULT" ]]; then
    echo "== results: $RESULT =="
    cat "$RESULT"
else
    echo "== NO RESULTS FILE ==" >&2
    exit 1
fi

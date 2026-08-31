#!/usr/bin/env bash
# Every phase in its own process against a freshly loaded save — proves each
# phase stands alone. Slow by construction; a pre-release sweep.
# Usage: ./test/run-tact-isolated.sh tact1 TACT-1-reload-abort
set -euo pipefail

SCENARIO="${1:?scenario (tact1..5)}"
SAVE="${2:?save name}"

REPO="$(cd "$(dirname "$0")/.." && pwd)"
COMPAT="$HOME/Projects/CombatExtended-SimpleSidearms Compatibility Patch"
RIMWORLD="$HOME/.local/share/Steam/steamapps/common/RimWorld/RimWorldLinux"
GS=(gamescope -W 1600 -H 900 --)
SAVEDATA="$COMPAT/test/SaveData"

if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
    dotnet build "$REPO/Source/CESSCompatTactics/CESSCompatTactics.csproj" -c Release
    dotnet build "$REPO/test/StagingMod/Source/TacticsTestStaging.csproj" -c Release
fi

rm -f "$SAVEDATA/test-results-$SCENARIO-iso-"*.json

run_one() {
    timeout --signal=TERM 20m "${GS[@]}" "$RIMWORLD" -savedatafolder="$SAVEDATA" \
        "-celoadsave=$SAVE" "-ceassert=$SCENARIO:$1" >/dev/null 2>&1 || true
}

echo "== isolated sweep: $SCENARIO =="
run_one 0
FIRST="$SAVEDATA/test-results-$SCENARIO-iso-00.json"
if [[ ! -f "$FIRST" ]]; then
    echo "== phase 0 produced no results; check Player.log ==" >&2
    exit 1
fi
COUNT=$(python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['phaseCount'])" "$FIRST")
echo "   $COUNT phases"

for ((i = 1; i < COUNT; i++)); do
    printf '   phase %d/%d\n' "$i" "$((COUNT - 1))"
    run_one "$i"
done

exec "$(dirname "$0")/verdict.py" --merge "$SAVEDATA/test-results-$SCENARIO-iso-"*.json

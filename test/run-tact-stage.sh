#!/usr/bin/env bash
# Build the Tactics mod + its staging mod and regenerate the TACT-* staged saves
# in the shared test profile (compat patch repo). Quit after the in-game letter.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
COMPAT="$HOME/Projects/CombatExtended-SimpleSidearms Compatibility Patch"
RIMWORLD="$HOME/.local/share/Steam/steamapps/common/RimWorld/RimWorldLinux"
# GS_WRAP: launch inside gamescope's nested compositor — immune to the desktop's
# display state (owner gaming via Proton, mode-list churn, XF86VidMode crashes).
GS=(gamescope -W 1600 -H 900 --)
SAVEDATA="$COMPAT/test/SaveData"
if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
    dotnet build "$REPO/Source/CESSCompatTactics/CESSCompatTactics.csproj" -c Release
    dotnet build "$REPO/test/StagingMod/Source/TacticsTestStaging.csproj" -c Release
fi
rm -f "$SAVEDATA/Saves"/TACT-*.rws
exec "${GS[@]}" "$RIMWORLD" -savedatafolder="$SAVEDATA" -quicktest -cetactstage -screen-fullscreen 0 -screen-width 1600 -screen-height 900

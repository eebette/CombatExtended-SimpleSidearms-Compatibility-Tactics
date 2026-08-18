using System.Linq;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using SimpleSidearms.rimworld;
using Verse;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;

namespace CESSCompatTactics.Features
{
    /// <summary>
    /// Feature 3: forced-weapon dry fall-through. SS's forced-weapon branches run
    /// before best-weapon logic with zero ammo checks (impossible state in vanilla).
    /// While a forced weapon is TRULY dry — empty magazine and nothing to reload
    /// from inventory — temporarily hide the forced setting from SS's selection so
    /// it falls through to normal preference logic.
    ///
    /// GUARD (bypass, never clear): the ForcedWeapon flags are SS-owned player
    /// state. They are nulled only for the duration of the wrapped call and always
    /// restored in finally — the moment ammo exists again the forced weapon
    /// resumes on its own.
    /// </summary>
    [HarmonyPatch(typeof(WeaponAssingment), nameof(WeaponAssingment.equipBestWeaponFromInventoryByPreference))]
    public static class ForcedDryFallthrough_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn pawn, out (CompSidearmMemory memory, ThingDefStuffDefPair? forced, ThingDefStuffDefPair? forcedDrafted)? __state)
        {
            __state = null;
            if (!TacticsMod.Settings.forcedDryFallthrough || pawn == null)
            {
                return;
            }
            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn, fillExistingIfCreating: false);
            if (memory == null)
            {
                return;
            }
            bool hideForced = memory.ForcedWeapon != null && IsTrulyDry(pawn, memory.ForcedWeapon.Value);
            bool hideDrafted = memory.ForcedWeaponWhileDrafted != null && IsTrulyDry(pawn, memory.ForcedWeaponWhileDrafted.Value);
            if (!hideForced && !hideDrafted)
            {
                return;
            }
            __state = (memory,
                       hideForced ? memory.ForcedWeapon : null,
                       hideDrafted ? memory.ForcedWeaponWhileDrafted : null);
            if (hideForced)
            {
                memory.ForcedWeapon = null;
            }
            if (hideDrafted)
            {
                memory.ForcedWeaponWhileDrafted = null;
            }
        }

        [HarmonyPostfix]
        public static void Postfix((CompSidearmMemory memory, ThingDefStuffDefPair? forced, ThingDefStuffDefPair? forcedDrafted)? __state)
        {
            if (__state == null)
            {
                return;
            }
            var (memory, forced, forcedDrafted) = __state.Value;
            if (forced != null)
            {
                memory.ForcedWeapon = forced;
            }
            if (forcedDrafted != null)
            {
                memory.ForcedWeaponWhileDrafted = forcedDrafted;
            }
        }

        /// <summary>Empty magazine AND no compatible ammo anywhere on the pawn.</summary>
        private static bool IsTrulyDry(Pawn pawn, ThingDefStuffDefPair pair)
        {
            ThingWithComps carried = pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                .FirstOrDefault(w => w.toThingDefStuffDefPair() == pair);
            if (carried == null)
            {
                return false; // not carried — SS's own logic handles that case
            }
            CompAmmoUser user = carried.TryGetComp<CompAmmoUser>();
            if (user == null || !user.UseAmmo)
            {
                return false; // no CE ammo concept — can never be dry
            }
            bool magEmpty = !user.HasMagazine || user.CurMagCount <= 0;
            return magEmpty && !user.HasAmmo;
        }
    }
}

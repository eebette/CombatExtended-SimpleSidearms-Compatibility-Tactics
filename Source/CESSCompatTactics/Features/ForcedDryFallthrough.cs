using System.Linq;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;

namespace CESSCompatTactics.Features
{
    /// <summary>
    /// Patches SS to allow switches off of a forced gun the pawn has no ammo for.
    /// </summary>
    [HarmonyPatch(typeof(WeaponAssingment), nameof(WeaponAssingment.equipBestWeaponFromInventoryByPreference),
                  new[] { typeof(Pawn), typeof(DroppingModeEnum), typeof(PrimaryWeaponMode?), typeof(Pawn) })]
    public static class ForcedDryFallthrough_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(WeaponAssingment), "equipBestWeaponFromInventoryByPreference",
            new[] { typeof(Pawn), typeof(DroppingModeEnum), typeof(PrimaryWeaponMode?), typeof(Pawn) },
            "a forced weapon that runs completely dry will be held no matter what.");

        [HarmonyPrefix]
        public static void Prefix(Pawn pawn, out (CompSidearmMemory memory, ThingDefStuffDefPair? forced, ThingDefStuffDefPair? forcedDrafted)? __state)
        {
            __state = null;
            try
            {
                PrefixInner(pawn, ref __state);
            }
            catch (System.Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Forced-dry check failed; the forced weapon is "
                              + "honored literally. " + e, 0x54414301);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void PrefixInner(Pawn pawn, ref (CompSidearmMemory memory, ThingDefStuffDefPair? forced, ThingDefStuffDefPair? forcedDrafted)? __state)
        {
            HideDryForcedFlags(pawn, ref __state);
        }

        [HarmonyFinalizer]
        public static void Finalizer((CompSidearmMemory memory, ThingDefStuffDefPair? forced, ThingDefStuffDefPair? forcedDrafted)? __state)
        {
            RestoreForcedFlags(__state);
        }

        /// <summary>Empty magazine AND no compatible ammo anywhere on the pawn.</summary>
        // (see also ForcedWeaponLesson_Patch below)
        internal static bool IsTrulyDry(Pawn pawn, ThingDefStuffDefPair pair)
        {
            // The forced flag is PAIR-level; dryness must be too.
            var instances = pawn.GetCarriedWeapons(includeEquipped: true, includeTools: true)
                .Where(w => w.toThingDefStuffDefPair() == pair)
                .ToList();
            if (instances.Count == 0)
            {
                return false; // not carried - SS's own logic handles that case
            }
            bool anyCeGun = false;
            bool anyLoaded = false;
            bool anyAmmo = false;
            bool anyRefill = false;
            foreach (ThingWithComps instance in instances)
            {
                CompAmmoUser user = instance.TryGetComp<CompAmmoUser>();
                if (user == null || !user.UseAmmo)
                {
                    return false; // a no-ammo-concept copy exists - can never be dry
                }
                anyCeGun = true;
                if (user.HasMagazine && user.CurMagCount > 0)
                {
                    anyLoaded = true;
                }
                if (user.HasAmmo)
                {
                    anyAmmo = true;
                }
                if (pawn.CurJobDef == CE_JobDefOf.ReloadWeapon
                    && pawn.CurJob?.targetB.Thing == instance)
                {
                    anyRefill = true;
                }
            }
            if (!anyCeGun || anyLoaded)
            {
                return false; // a loaded copy exists - SS's forced branch can equip it
            }
            return !anyAmmo || anyRefill;
        }

        /// <summary>Shared hide step for both entry points.</summary>
        internal static void HideDryForcedFlags(Pawn pawn,
            ref (CompSidearmMemory memory, ThingDefStuffDefPair? forced, ThingDefStuffDefPair? forcedDrafted)? __state)
        {
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

        internal static void RestoreForcedFlags(
            (CompSidearmMemory memory, ThingDefStuffDefPair? forced, ThingDefStuffDefPair? forcedDrafted)? __state)
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
    }

    /// <summary>
    /// Patches SS's melee-attacked reflex (tryCQCWeaponSwapToMelee) to hide a dry forced
    /// weapon's flags so it stops blocking the knife draw.
    /// </summary>
    [HarmonyPatch(typeof(WeaponAssingment), nameof(WeaponAssingment.tryCQCWeaponSwapToMelee),
                  new[] { typeof(Pawn), typeof(Pawn), typeof(DroppingModeEnum) })]
    public static class ForcedDryCqc_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(WeaponAssingment), "tryCQCWeaponSwapToMelee",
            new[] { typeof(Pawn), typeof(Pawn), typeof(DroppingModeEnum) },
            "forced-dry fall-through will not cover the melee-attacked reflex.");

        [HarmonyPrefix]
        public static void Prefix(Pawn pawn, out (CompSidearmMemory memory, ThingDefStuffDefPair? forced, ThingDefStuffDefPair? forcedDrafted)? __state)
        {
            __state = null;
            try
            {
                ForcedDryFallthrough_Patch.HideDryForcedFlags(pawn, ref __state);
            }
            catch (System.Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Forced-dry CQC check failed; the forced weapon is "
                              + "honored literally. " + e, 0x5441430C);
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer((CompSidearmMemory memory, ThingDefStuffDefPair? forced, ThingDefStuffDefPair? forcedDrafted)? __state)
        {
            ForcedDryFallthrough_Patch.RestoreForcedFlags(__state);
        }
    }

    /// <summary>
    /// Learning Helper note informing the player of the default fall-through behavior the
    /// first time a weapon is forced.
    /// </summary>
    [HarmonyPatch(typeof(CompSidearmMemory), nameof(CompSidearmMemory.SetWeaponAsForced),
                  new[] { typeof(ThingDefStuffDefPair), typeof(bool) })]
    public static class ForcedWeaponLesson_Patch
    {
        private static ConceptDef concept;

        public static bool Prepare() => PatchGuard.Require(typeof(CompSidearmMemory), "SetWeaponAsForced",
            new[] { typeof(ThingDefStuffDefPair), typeof(bool) },
            "the one-time note explaining the forced-dry toggle will not appear.");

        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                concept = concept ?? DefDatabase<ConceptDef>.GetNamedSilentFail("CESSTactics_ForcedDryChoice");
                if (concept != null)
                {
                    LessonAutoActivator.TeachOpportunity(concept, OpportunityType.GoodToKnow);
                }
            }
            catch (System.Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Forced-dry lesson note failed. " + e, 0x54414302);
            }
        }
    }
}

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

        // A FINALIZER, not a postfix: postfixes are skipped when the original (or a
        // later prefix) throws, and the state being restored here is the PLAYER'S
        // forced-weapon setting — hidden for the duration of one call under the
        // "bypass, never clear" guard. A throw leaving it nulled would be this
        // feature destroying the exact intent it exists to respect.
        [HarmonyFinalizer]
        public static void Finalizer((CompSidearmMemory memory, ThingDefStuffDefPair? forced, ThingDefStuffDefPair? forcedDrafted)? __state)
        {
            RestoreForcedFlags(__state);
        }

        /// <summary>Empty magazine AND no compatible ammo anywhere on the pawn.
        /// One refinement (T3-11): while a reload job for this very gun is in
        /// flight, backpack ammo does NOT count as "not dry" — the magazine is
        /// still at zero, and letting the forced branch re-equip it mid-refill
        /// killed the refill and put an empty gun in the pawn's hands. The forced
        /// weapon resumes the moment the refill lands.</summary>
        // (see also ForcedWeaponLesson_Patch below)
        internal static bool IsTrulyDry(Pawn pawn, ThingDefStuffDefPair pair)
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
            bool refillInFlight = pawn.CurJobDef == CE_JobDefOf.ReloadWeapon
                                  && pawn.CurJob?.targetB.Thing == carried;
            return magEmpty && (!user.HasAmmo || refillInFlight);
        }

        /// <summary>Shared hide step for both entry points: stash and null the
        /// truly-dry forced flags; the finalizer restores from __state.</summary>
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
    /// T3-6: the melee-attacked reflex (doCQC → tryCQCWeaponSwapToMelee) checks
    /// "is the current weapon forced?" INSIDE SS, one call above everything the
    /// class above hides — so the fall-through never covered the one moment the
    /// pawn is being stabbed. Same hide-and-always-restore discipline on that
    /// entry point gives the toggle full coverage: a truly-dry forced gun stops
    /// blocking the knife draw, and the flags come back untouched either way.
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
    /// Learning Helper note at the moment the ambiguity starts existing for the
    /// player: the first time they FORCE a weapon, the vanilla lesson system
    /// explains the two readings ("hold no matter what" vs "prefer while usable")
    /// and where the toggle lives. Vanilla's own teaching surface — no popups.
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

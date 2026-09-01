using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Utilities;
using Verse;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;

namespace CESSCompatTactics.Features
{
    /// <summary>
    /// Feature 6: armor-aware melee choice — reworked twice over (T3-2).
    ///
    /// The first version postfixed findBestMeleeWeapon and read its target
    /// parameter. That parameter is DEAD WIRING in this SS build: the CQC/ordered
    /// paths hand their target to equipBestWeaponFromInventoryByPreference, which
    /// never forwards it to the findBestMeleeWeapon call — the only such call in
    /// the game. So the feature never fired outside the harness. The version also
    /// re-enumerated candidates without SS's usability/mod filters and fed the
    /// scoring a zero averageSpeed that cancelled the player's speed-bias setting.
    ///
    /// This rework is the F04 pattern, melee edition:
    ///  - A prefix on equipBestWeaponFromInventoryByPreference — the method the
    ///    target actually reaches — opens a call-lifetime SCOPE carrying it
    ///    (finalizer closes; nesting saved/restored via __state).
    ///  - A postfix on getMeleeDPSBiased — the scoring call inside SS's own
    ///    selection loop — adjusts the outgoing score in place while the scope is
    ///    open: divide out SS's generic (1 + penetration) armor bonus (the same
    ///    P12/P13-backed MeleePenetration SS multiplied in, so the division is
    ///    exact) and multiply the fraction of damage that survives THIS target's
    ///    armor (TargetScoring.MeleeTargetFactor). Against flesh that leaves pure
    ///    CE damage-per-second — the fast blade wins; against armor the fraction
    ///    takes over. SS keeps its filters, its real averageSpeed, its speed
    ///    bias, and its comparison. Every (weapon, raw, adjusted) pair is
    ///    RECORDED (per weapon — SS scores each candidate twice via Max+MaxBy).
    ///  - A postfix on findBestMeleeWeapon applies the all-hopeless defer from
    ///    the records: when every candidate's adjusted score is zero (centipede
    ///    plate vs a colonist's pocket), re-ranking zeros is noise — the raw
    ///    ranking, which IS SS's own target-blind pick, stands.
    /// </summary>
    internal static class MeleeSelectionScope
    {
        internal static Verse.Pawn Target;
        internal static Dictionary<ThingWithComps, (float raw, float adjusted)> Records;

        internal static bool Active => Records != null;
    }

    [HarmonyPatch(typeof(WeaponAssingment), nameof(WeaponAssingment.equipBestWeaponFromInventoryByPreference),
                  new[] { typeof(Pawn), typeof(DroppingModeEnum), typeof(PrimaryWeaponMode?), typeof(Pawn) })]
    public static class MeleeSelection_ScopePatch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(WeaponAssingment), "equipBestWeaponFromInventoryByPreference",
            new[] { typeof(Pawn), typeof(DroppingModeEnum), typeof(PrimaryWeaponMode?), typeof(Pawn) },
            "armor-aware melee choice is inactive.");

        [HarmonyPrefix]
        public static void Prefix(Pawn target,
            out (Pawn prevTarget, Dictionary<ThingWithComps, (float, float)> prevRecords)? __state)
        {
            __state = null;
            try
            {
                if (!TacticsMod.Settings.armorAwareMelee || target == null)
                {
                    return; // no scope: the scoring postfix stays inert
                }
                __state = (MeleeSelectionScope.Target, MeleeSelectionScope.Records);
                MeleeSelectionScope.Target = target;
                MeleeSelectionScope.Records = new Dictionary<ThingWithComps, (float, float)>();
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Melee-selection scope failed to open; Simple "
                              + "Sidearms' own pick stands. " + e, 0x5441430A);
            }
        }

        /// <summary>Scope must not leak past the call even when SS throws.</summary>
        [HarmonyFinalizer]
        public static void Finalizer((Pawn prevTarget, Dictionary<ThingWithComps, (float, float)> prevRecords)? __state)
        {
            if (__state.HasValue)
            {
                MeleeSelectionScope.Target = __state.Value.prevTarget;
                MeleeSelectionScope.Records = __state.Value.prevRecords;
            }
        }
    }

    [HarmonyPatch(typeof(StatCalculator), nameof(StatCalculator.getMeleeDPSBiased),
                  new[] { typeof(ThingWithComps), typeof(Pawn), typeof(float), typeof(float) })]
    public static class StatCalculator_getMeleeDPSBiased_ScopePatch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(StatCalculator), "getMeleeDPSBiased",
            new[] { typeof(ThingWithComps), typeof(Pawn), typeof(float), typeof(float) },
            "armor-aware melee choice cannot adjust scores in place.");

        [HarmonyPostfix]
        public static void Postfix(ThingWithComps weapon, Pawn pawn, ref float __result)
        {
            try
            {
                PostfixInner(weapon, pawn, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Armor-aware melee score adjustment failed; the "
                              + "unadjusted score stands. " + e, 0x5441430B);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(ThingWithComps weapon, Pawn pawn, ref float __result)
        {
            if (!MeleeSelectionScope.Active)
            {
                return; // gizmos, tooltips, out-of-scope ranking: untouched
            }
            float raw = __result;
            float adjusted = raw;
            if (raw > 0f)
            {
                // SS's score factors as (dmg/biasedSpeed) × (1 + pen): the (1+pen)
                // term is a GENERIC armor bonus paid against every target. Divide it
                // out — MeleePenetration is the very input SS multiplied in, so this
                // is exact — and substitute the actual through-armor fraction.
                adjusted = raw / (1f + StatCalculator.MeleePenetration(weapon, pawn))
                           * TargetScoring.MeleeTargetFactor(weapon, MeleeSelectionScope.Target);
            }
            MeleeSelectionScope.Records[weapon] = (raw, adjusted);
            __result = adjusted;
        }
    }

    [HarmonyPatch(typeof(GettersFilters), nameof(GettersFilters.findBestMeleeWeapon),
                  new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool), typeof(Pawn) },
                  new[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal })]
    public static class ArmorAwareMelee_DeferPatch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(GettersFilters), "findBestMeleeWeapon",
            new[] { typeof(Pawn), typeof(ThingWithComps).MakeByRefType(), typeof(bool), typeof(bool), typeof(Pawn) },
            "the armor-aware melee all-hopeless fallback is inactive.");

        [HarmonyPostfix]
        public static void Postfix(ref ThingWithComps result, ref bool __result)
        {
            try
            {
                PostfixInner(ref result, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Melee all-hopeless fallback failed; the adjusted "
                              + "pick stands. " + e, 0x54414304);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(ref ThingWithComps result, ref bool __result)
        {
            if (!MeleeSelectionScope.Active || result == null)
            {
                return;
            }
            var records = MeleeSelectionScope.Records;
            if (records.Count == 0)
            {
                return;
            }
            // All-hopeless defer: nothing the pawn carries does anything to this
            // target — the recorded raw ranking IS SS's target-blind pick; restore it.
            if (records.Values.All(r => r.adjusted <= 0f) && records.Values.Any(r => r.raw > 0f))
            {
                result = records.MaxBy(kv => kv.Value.raw).Key;
                __result = true;
            }
        }
    }
}

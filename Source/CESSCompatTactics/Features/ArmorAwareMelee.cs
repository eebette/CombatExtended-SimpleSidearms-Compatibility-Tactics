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
    /// Armor-aware melee choice.
    /// </summary>
    internal static class MeleeSelectionScope
    {
        internal static Verse.Pawn Target;
        internal static Dictionary<ThingWithComps, (float raw, float adjusted, bool modeled)> Records;

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
            out (Pawn prevTarget, Dictionary<ThingWithComps, (float, float, bool)> prevRecords)? __state)
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
                MeleeSelectionScope.Records = new Dictionary<ThingWithComps, (float, float, bool)>();
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Melee-selection scope failed to open; Simple "
                              + "Sidearms' own pick stands. " + e, 0x5441430A);
            }
        }

        /// <summary>Scope must not leak past the call even when SS throws.</summary>
        [HarmonyFinalizer]
        public static void Finalizer((Pawn prevTarget, Dictionary<ThingWithComps, (float, float, bool)> prevRecords)? __state)
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
            bool modeled = false;
            if (raw > 0f
                && TargetScoring.TryMeleeTargetFactor(weapon, MeleeSelectionScope.Target, out float factor))
            {
                // SS exposes no pre-armor score so back into it from raw
                adjusted = raw / (1f + StatCalculator.MeleePenetration(weapon, pawn)) * factor;
                modeled = true;
            }
            MeleeSelectionScope.Records[weapon] = (raw, adjusted, modeled);
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
            // Nothing MODELED does anything to this target so restore SS's target-blind pick.
            if (records.Values.Any(r => r.modeled)
                && records.Values.Where(r => r.modeled).All(r => r.adjusted <= 0f)
                && records.Values.Any(r => r.raw > 0f))
            {
                result = records.MaxBy(kv => kv.Value.raw).Key;
                __result = true;
            }
        }
    }
}

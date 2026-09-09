using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms.Utilities;
using Verse;

namespace CESSCompatTactics.Features
{
    /// <summary>
    /// Call-lifetime scope for one findBestRangedWeapon pass. The ammo-aware hooks run inside
    /// SS's own selection, so they need a shared place to carry the target in and collect each
    /// candidate's score while it runs.
    /// </summary>
    internal static class RangedSelectionScope
    {
        internal static Pawn Target;
        // modeled=false: the multiplier could not judge this weapon (no CE
        // projectile).
        internal static List<(ThingWithComps weapon, float raw, float adjusted, bool modeled)> Records;

        internal static bool Active => Records != null;
    }

    [HarmonyPatch(typeof(GettersFilters), nameof(GettersFilters.findBestRangedWeapon),
                  new[] { typeof(Pawn), typeof(LocalTargetInfo?), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
    public static class RangedSelection_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(GettersFilters), "findBestRangedWeapon",
            new[] { typeof(Pawn), typeof(LocalTargetInfo?), typeof(bool), typeof(bool), typeof(bool), typeof(bool) },
            "target-aware ammo scoring and the ammo-depth tiebreak are inactive.");

        [HarmonyPrefix]
        public static void Prefix(LocalTargetInfo? target,
            out (Pawn prevTarget, List<(ThingWithComps weapon, float raw, float adjusted, bool modeled)> prevRecords)? __state)
        {
            __state = null;
            try
            {
                TacticsSettings settings = TacticsMod.Settings;
                Pawn targetPawn = target.HasValue ? target.Value.Thing as Pawn : null;
                bool targetAware = settings.targetAwareAmmoScoring && targetPawn != null;
                if (!targetAware && !settings.ammoDepthTiebreak)
                {
                    return; // no scope: the scoring postfixes stay inert
                }
                __state = (RangedSelectionScope.Target, RangedSelectionScope.Records);
                RangedSelectionScope.Target = targetAware ? targetPawn : null;
                RangedSelectionScope.Records = new List<(ThingWithComps, float, float, bool)>();
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Ranged-selection scope failed to open; Simple "
                              + "Sidearms' own pick stands. " + e, 0x54414307);
            }
        }

        /// <summary>
        /// Patches findBestRangedWeapon's result to break near-ties by ammo depth and defer to
        /// SS's raw pick when nothing scores against the target - both from the recorded scores.
        /// </summary>
        [HarmonyBefore(CESimpleSidearmsCompat.Bootstrap.HarmonyId)]
        [HarmonyPostfix]
        public static void Postfix(ref (ThingWithComps weapon, float dps, float averageSpeed) __result)
        {
            try
            {
                PostfixInner(ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Ranged re-rank failed; Simple Sidearms' own "
                              + "pick stands. " + e, 0x54414303);
            }
        }

        /// <summary>Scope must not leak past the call even when SS throws.</summary>
        [HarmonyFinalizer]
        public static void Finalizer(
            (Pawn prevTarget, List<(ThingWithComps weapon, float raw, float adjusted, bool modeled)> prevRecords)? __state)
        {
            if (__state.HasValue)
            {
                RangedSelectionScope.Target = __state.Value.prevTarget;
                RangedSelectionScope.Records = __state.Value.prevRecords;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(ref (ThingWithComps weapon, float dps, float averageSpeed) __result)
        {
            if (!RangedSelectionScope.Active || __result.weapon == null)
            {
                return; // scope never opened, or SS found nothing usable at all
            }
            var records = RangedSelectionScope.Records;
            if (records.Count == 0)
            {
                return;
            }
            TacticsSettings settings = TacticsMod.Settings;

            // Every MODELED score zeroed against this target, so restore the raw ranking.
            bool deferred = false;
            if (RangedSelectionScope.Target != null
                && records.Any(r => r.modeled)
                && records.Where(r => r.modeled).All(r => r.adjusted <= 0f)
                && records.Any(r => r.raw > 0f))
            {
                // Only guns that can actually fire.
                var usable = records.Where(r => HasRounds(r.weapon) && r.raw > 0f).ToList();
                if (usable.Count > 0)
                {
                    var bestRaw = usable.MaxBy(r => r.raw);
                    // The ADJUSTED score (zero).
                    __result = (bestRaw.weapon, bestRaw.adjusted, __result.averageSpeed);
                    deferred = true;
                }
            }

            if (!settings.ammoDepthTiebreak)
            {
                return;
            }
            // Tie window over the scores this selection actually ranked by.
            Func<(ThingWithComps weapon, float raw, float adjusted, bool modeled), float> score =
                r => deferred ? r.raw : r.adjusted;
            ThingWithComps picked = __result.weapon;
            var current = records.FirstOrDefault(r => r.weapon == picked);
            if (current.weapon == null)
            {
                return;
            }
            float floor = score(current) * (1f - settings.tiebreakEpsilonPct / 100f);
            var best = current;
            long bestDepth = AmmoDepth(best.weapon);
            foreach (var r in records)
            {
                if (r.weapon == best.weapon || score(r) < floor || score(r) <= 0f)
                {
                    continue;
                }
                long depth = AmmoDepth(r.weapon);
                if (depth > bestDepth)
                {
                    best = r;
                    bestDepth = depth;
                }
            }
            if (best.weapon != __result.weapon)
            {
                __result = (best.weapon, deferred ? best.adjusted : score(best), __result.averageSpeed);
            }
        }

        /// <summary>Same has-rounds rule the rest of the suite uses.</summary>
        private static bool HasRounds(ThingWithComps weapon)
        {
            CompAmmoUser user = weapon.TryGetComp<CompAmmoUser>();
            return user == null || !user.UseAmmo || user.HasAmmoOrMagazine;
        }

        /// <summary>Rounds on hand: magazine + carried spares.</summary>
        private static long AmmoDepth(ThingWithComps weapon)
        {
            CompAmmoUser user = weapon.TryGetComp<CompAmmoUser>();
            if (user == null || !user.UseAmmo)
            {
                return long.MaxValue;
            }
            Pawn holder = (weapon.ParentHolder as Pawn_InventoryTracker)?.pawn
                          ?? (weapon.ParentHolder as Pawn_EquipmentTracker)?.pawn;
            CompInventory inventory = holder?.TryGetComp<CompInventory>();
            long spare = 0;
            var ammoTypes = user.CurAmmoSet?.ammoTypes;
            if (inventory != null && ammoTypes != null)
            {
                spare = ammoTypes.Sum(l => (long)inventory.AmmoCountOfDef(l.ammo));
            }
            return user.CurMagCount + spare;
        }
    }

    /// <summary>
    /// Patches SS's warmup auto-switch (trySwapToMoreAccurateRangedWeapon) to open the selection
    /// scope across the whole call so the held gun is scored target-adjusted like the candidate.
    /// </summary>
    [HarmonyPatch(typeof(WeaponAssingment), nameof(WeaponAssingment.trySwapToMoreAccurateRangedWeapon),
                  new[] { typeof(Pawn), typeof(LocalTargetInfo), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
    public static class TrySwap_ScopePatch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(WeaponAssingment), "trySwapToMoreAccurateRangedWeapon",
            new[] { typeof(Pawn), typeof(LocalTargetInfo), typeof(bool), typeof(bool), typeof(bool), typeof(bool) },
            "target-aware scoring will compare an adjusted challenger against a raw incumbent (swaps suppressed).");

        [HarmonyPrefix]
        public static void Prefix(LocalTargetInfo target,
            out (Pawn prevTarget, List<(ThingWithComps weapon, float raw, float adjusted, bool modeled)> prevRecords)? __state)
        {
            __state = null;
            try
            {
                Pawn targetPawn = target.Thing as Pawn;
                if (!TacticsMod.Settings.targetAwareAmmoScoring || targetPawn == null)
                {
                    return;
                }
                __state = (RangedSelectionScope.Target, RangedSelectionScope.Records);
                RangedSelectionScope.Target = targetPawn;
                RangedSelectionScope.Records = new List<(ThingWithComps, float, float, bool)>();
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Swap-comparison scope failed to open; the raw "
                              + "comparison stands. " + e, 0x5441430D);
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(
            (Pawn prevTarget, List<(ThingWithComps weapon, float raw, float adjusted, bool modeled)> prevRecords)? __state)
        {
            if (__state.HasValue)
            {
                RangedSelectionScope.Target = __state.Value.prevTarget;
                RangedSelectionScope.Records = __state.Value.prevRecords;
            }
        }
    }

    [HarmonyPatch(typeof(StatCalculator), nameof(StatCalculator.RangedDPS),
                  new[] { typeof(ThingWithComps), typeof(float), typeof(float), typeof(float) })]
    public static class StatCalculator_RangedDPS_ScopePatch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(StatCalculator), "RangedDPS",
            new[] { typeof(ThingWithComps), typeof(float), typeof(float), typeof(float) },
            "target-aware ammo scoring is inactive (scores cannot be adjusted in place).");

        [HarmonyPostfix]
        public static void Postfix(ThingWithComps weapon, ref float __result)
        {
            try
            {
                ScopeScoring.Record(weapon, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Target-aware score adjustment failed; the "
                              + "unadjusted score stands. " + e, 0x54414308);
            }
        }
    }

    [HarmonyPatch(typeof(StatCalculator), nameof(StatCalculator.RangedDPSAverage),
                  new[] { typeof(ThingWithComps), typeof(float), typeof(float) })]
    public static class StatCalculator_RangedDPSAverage_ScopePatch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(StatCalculator), "RangedDPSAverage",
            new[] { typeof(ThingWithComps), typeof(float), typeof(float) },
            "the ammo-depth tiebreak cannot see targetless selection scores.");

        [HarmonyPostfix]
        public static void Postfix(ThingWithComps weapon, ref float __result)
        {
            try
            {
                ScopeScoring.Record(weapon, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Selection score recording failed; the "
                              + "unadjusted score stands. " + e, 0x54414309);
            }
        }
    }

    internal static class ScopeScoring
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Record(ThingWithComps weapon, ref float __result)
        {
            if (!RangedSelectionScope.Active)
            {
                return; // gizmos, tooltips, reload-abort's own scan: untouched
            }
            float raw = __result;
            float adjusted = raw;
            bool modeled = false;
            if (RangedSelectionScope.Target != null && raw > 0f
                && TargetScoring.TryRangedMultiplier(weapon, RangedSelectionScope.Target, out float factor))
            {
                adjusted = raw * factor;
                modeled = true;
            }
            // An unmodelable weapon (no CE projectile) keeps its untouched score.
            RangedSelectionScope.Records.Add((weapon, raw, adjusted, modeled));
            __result = adjusted;
        }
    }
}

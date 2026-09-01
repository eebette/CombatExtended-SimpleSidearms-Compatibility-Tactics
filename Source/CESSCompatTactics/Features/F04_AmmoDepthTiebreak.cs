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
    /// Features 4 + 5 share SS's ranged selection pass — and they now live INSIDE it
    /// instead of re-running it. The first version's postfix re-enumerated candidates
    /// through a hand-copied filter chain, which had already drifted from SS's real
    /// one (it missed the biocode check, the VFE-shield and Tacticowl exclusions,
    /// and the per-weapon min/max range window): the re-rank could crown a weapon
    /// SS itself had refused. This rework never enumerates anything.
    ///
    /// Mechanics, three small patches that compose:
    ///  - A prefix on findBestRangedWeapon opens a SCOPE carrying the target (the
    ///    core patch's P01 retrieval-scope pattern), and a finalizer closes it —
    ///    finalizers run even when the original throws.
    ///  - A postfix on the two scoring entry points SS uses inside that pass
    ///    (RangedDPS with a target, RangedDPSAverage without) multiplies the
    ///    outgoing score by the loaded-ammo-vs-target factor (feature 5,
    ///    TargetScoring.RangedMultiplier) and RECORDS the (weapon, raw, adjusted)
    ///    pair. SS's own loop, filters, and comparison then pick with adjusted
    ///    numbers natively. The core patch's P02 cache is untouched: its prefix
    ///    stores before this postfix rewrites the one outgoing value.
    ///  - A postfix on findBestRangedWeapon applies the ammo-depth tiebreak
    ///    (feature 4) and the all-hopeless defer from the RECORDED pairs — no
    ///    second scan, ever.
    ///
    /// Defer: when every adjusted score is zero against this target (centipede
    /// plate vs everything in a colonist's pocket), re-ranking zeros is noise —
    /// the recorded RAW ranking stands in, which is exactly SS's own target-blind
    /// pick. SS grows a new filter tomorrow → inherited automatically.
    /// NO SelectedAmmo writes, no state anywhere but the call-lifetime scope.
    /// </summary>
    internal static class RangedSelectionScope
    {
        internal static Pawn Target;
        internal static List<(ThingWithComps weapon, float raw, float adjusted)> Records;

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
            out (Pawn prevTarget, List<(ThingWithComps weapon, float raw, float adjusted)> prevRecords)? __state)
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
                RangedSelectionScope.Records = new List<(ThingWithComps, float, float)>();
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Ranged-selection scope failed to open; Simple "
                              + "Sidearms' own pick stands. " + e, 0x54414307);
            }
        }

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
            (Pawn prevTarget, List<(ThingWithComps weapon, float raw, float adjusted)> prevRecords)? __state)
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

            // All-hopeless defer: every adjusted score zeroed against this target.
            // The recorded raw ranking IS SS's target-blind pick — restore it.
            bool deferred = false;
            if (RangedSelectionScope.Target != null
                && records.All(r => r.adjusted <= 0f) && records.Any(r => r.raw > 0f))
            {
                // Only guns that can actually fire: records include dry weapons at
                // full paper score, and the compat patch's dry-pick correction (P03)
                // has already run — resurrecting a dry gun here handed pawns an
                // empty weapon against the hardest targets (T3-4).
                var usable = records.Where(r => HasRounds(r.weapon) && r.raw > 0f).ToList();
                if (usable.Count > 0)
                {
                    var bestRaw = usable.MaxBy(r => r.raw);
                    __result = (bestRaw.weapon, bestRaw.raw, __result.averageSpeed);
                    deferred = true;
                }
            }

            if (!settings.ammoDepthTiebreak)
            {
                return;
            }
            // Tie window over the scores this selection actually ranked by.
            Func<(ThingWithComps weapon, float raw, float adjusted), float> score =
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
                __result = (best.weapon, score(best), __result.averageSpeed);
            }
        }

        /// <summary>Same has-rounds rule the rest of the suite uses.</summary>
        private static bool HasRounds(ThingWithComps weapon)
        {
            CompAmmoUser user = weapon.TryGetComp<CompAmmoUser>();
            return user == null || !user.UseAmmo || user.HasAmmoOrMagazine;
        }

        /// <summary>Rounds on hand: magazine + carried spares; non-CE weapons never
        /// run dry — effectively infinite depth.</summary>
        private static long AmmoDepth(ThingWithComps weapon)
        {
            CompAmmoUser user = weapon.TryGetComp<CompAmmoUser>();
            if (user == null || !user.UseAmmo)
            {
                return long.MaxValue;
            }
            Pawn holder = (weapon.ParentHolder as Pawn_InventoryTracker)?.pawn
                          ?? (weapon.ParentHolder as Pawn_EquipmentTracker)?.pawn;
            var ammoDefs = user.Props?.ammoSet?.ammoTypes?.Select(l => (ThingDef)l.ammo).ToList();
            long spare = 0;
            if (ammoDefs != null && holder?.inventory?.innerContainer != null)
            {
                spare = holder.inventory.innerContainer
                    .Where(t => ammoDefs.Contains(t.def))
                    .Sum(t => (long)t.stackCount);
            }
            return user.CurMagCount + spare;
        }
    }

    /// <summary>
    /// T3-3: SS's warmup auto-switch (trySwapToMoreAccurateRangedWeapon) scored the
    /// CHALLENGER inside the selection scope (armor-adjusted, ≤ raw) but re-scored
    /// the INCUMBENT after the scope closed (raw) — a rigged comparison the
    /// challenger could essentially never win, so the feature could veto swaps but
    /// never produce the AP-rifle draw it exists for. Opening the scope across the
    /// whole caller makes line-378's incumbent score adjusted too: symmetric
    /// comparison, SS's own anti-oscillation margin preserved. The nested
    /// findBestRangedWeapon call stacks its own scope via __state as usual.
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
            out (Pawn prevTarget, List<(ThingWithComps weapon, float raw, float adjusted)> prevRecords)? __state)
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
                RangedSelectionScope.Records = new List<(ThingWithComps, float, float)>();
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Swap-comparison scope failed to open; the raw "
                              + "comparison stands. " + e, 0x5441430D);
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(
            (Pawn prevTarget, List<(ThingWithComps weapon, float raw, float adjusted)> prevRecords)? __state)
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
                return; // gizmos, tooltips, F01's own scan: untouched
            }
            float raw = __result;
            float adjusted = raw;
            if (RangedSelectionScope.Target != null && raw > 0f)
            {
                adjusted = raw * TargetScoring.RangedMultiplier(weapon, RangedSelectionScope.Target);
            }
            RangedSelectionScope.Records.Add((weapon, raw, adjusted));
            __result = adjusted;
        }
    }
}

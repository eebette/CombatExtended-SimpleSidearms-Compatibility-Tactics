using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using Verse;
using SSCore = PeteTimesSix.SimpleSidearms.SimpleSidearms;

namespace CESSCompatTactics.Features
{
    /// <summary>
    /// Features 4 + 5 share one ranking pass on findBestRangedWeapon — they must
    /// see the same adjusted scores or they would fight each other.
    ///
    /// Feature 5 (target-aware loaded-ammo scoring): each candidate's DPS is scaled
    /// by TargetScoring.RangedMultiplier — penetration of the LOADED projectile vs
    /// the target's armor, EMP vs mechs. NO ammo switching, NO SelectedAmmo writes.
    ///
    /// Feature 4 (ammo-depth tiebreak): among candidates within the epsilon window
    /// of the (adjusted) winner, prefer deeper ammo reserves. STRICT tiebreak —
    /// subordinate to the DPS ranking by construction.
    ///
    /// Scoring calls the same public entry points the core patch's P02/P03 correct,
    /// so numbers cannot diverge from the ranking being adjusted. Inert unless a
    /// relevant toggle is on.
    /// </summary>
    [HarmonyPatch(typeof(GettersFilters), nameof(GettersFilters.findBestRangedWeapon),
                  new[] { typeof(Pawn), typeof(LocalTargetInfo?), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
    public static class RangedSelection_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(GettersFilters), "findBestRangedWeapon",
            new[] { typeof(Pawn), typeof(LocalTargetInfo?), typeof(bool), typeof(bool), typeof(bool), typeof(bool) },
            "target-aware ammo scoring and the ammo-depth tiebreak are inactive.");

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, LocalTargetInfo? target, bool skipManualUse,
            bool skipDangerous, bool skipEMP, bool includeEquipped,
            ref (ThingWithComps weapon, float dps, float averageSpeed) __result)
        {
            try
            {
                PostfixInner(pawn, target, skipManualUse, skipDangerous, skipEMP, includeEquipped, ref __result);
            }
            catch (System.Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Ranged re-rank failed; Simple Sidearms' own "
                              + "pick stands. " + e, 0x54414303);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, LocalTargetInfo? target, bool skipManualUse,
            bool skipDangerous, bool skipEMP, bool includeEquipped,
            ref (ThingWithComps weapon, float dps, float averageSpeed) __result)
        {
            TacticsSettings settings = TacticsMod.Settings;
            Pawn targetPawn = target.HasValue ? target.Value.Thing as Pawn : null;
            bool targetAware = settings.targetAwareAmmoScoring && targetPawn != null;
            bool tiebreak = settings.ammoDepthTiebreak;
            if ((!targetAware && !tiebreak) || __result.weapon == null || pawn == null)
            {
                return;
            }

            float distance = target.HasValue && target.Value.IsValid
                ? target.Value.Cell.DistanceTo(pawn.Position)
                : -1f;
            float bias = SSCore.Settings.SpeedSelectionBiasRanged;

            var scored = new List<(ThingWithComps weapon, float adjDps)>();
            foreach (ThingWithComps candidate in Candidates(pawn, skipManualUse, skipDangerous, skipEMP, includeEquipped))
            {
                float dps = distance >= 0f
                    ? StatCalculator.RangedDPS(candidate, bias, __result.averageSpeed, distance)
                    : StatCalculator.RangedDPSAverage(candidate, bias, __result.averageSpeed);
                if (targetAware)
                {
                    dps *= TargetScoring.RangedMultiplier(candidate, targetPawn);
                }
                scored.Add((candidate, dps));
            }
            if (scored.Count == 0)
            {
                return;
            }

            (ThingWithComps weapon, float adjDps) best = scored.MaxBy(s => s.adjDps);
            if (best.adjDps <= 0f)
            {
                // Every candidate is hopeless against this target (or scores zero
                // outright) — re-ranking zeros is noise. SS's own pick stands as the
                // least-bad generic choice.
                return;
            }

            if (tiebreak)
            {
                float floor = best.adjDps * (1f - settings.tiebreakEpsilonPct / 100f);
                long bestDepth = AmmoDepth(pawn, best.weapon);
                foreach (var s in scored)
                {
                    if (s.weapon == best.weapon || s.adjDps < floor)
                    {
                        continue;
                    }
                    long depth = AmmoDepth(pawn, s.weapon);
                    if (depth > bestDepth)
                    {
                        best = s;
                        bestDepth = depth;
                    }
                }
            }

            if (best.weapon != __result.weapon)
            {
                __result = (best.weapon, best.adjDps, __result.averageSpeed);
            }
        }

        private static IEnumerable<ThingWithComps> Candidates(Pawn pawn, bool skipManualUse,
            bool skipDangerous, bool skipEMP, bool includeEquipped)
        {
            foreach (ThingWithComps weapon in pawn.GetCarriedWeapons(includeEquipped: includeEquipped, includeTools: false))
            {
                if (!weapon.def.IsRangedWeapon)
                {
                    continue;
                }
                if (skipManualUse && GettersFilters.isManualUse(weapon))
                {
                    continue;
                }
                if (skipDangerous && GettersFilters.isDangerousWeapon(weapon))
                {
                    continue;
                }
                if (skipEMP && GettersFilters.isEMPWeapon(weapon))
                {
                    continue;
                }
                CompAmmoUser user = weapon.TryGetComp<CompAmmoUser>();
                if (user != null && user.UseAmmo && !user.HasAmmoOrMagazine)
                {
                    continue; // dry — same eligibility rule as the core patch's axis 3
                }
                yield return weapon;
            }
        }

        /// <summary>Rounds on hand: magazine + carried spares; non-CE weapons never
        /// run dry — effectively infinite depth.</summary>
        private static long AmmoDepth(Pawn pawn, ThingWithComps weapon)
        {
            CompAmmoUser user = weapon.TryGetComp<CompAmmoUser>();
            if (user == null || !user.UseAmmo)
            {
                return long.MaxValue;
            }
            var ammoDefs = user.Props?.ammoSet?.ammoTypes?.Select(l => (ThingDef)l.ammo).ToList();
            long spare = 0;
            if (ammoDefs != null && pawn.inventory?.innerContainer != null)
            {
                spare = pawn.inventory.innerContainer
                    .Where(t => ammoDefs.Contains(t.def))
                    .Sum(t => (long)t.stackCount);
            }
            return user.CurMagCount + spare;
        }
    }
}

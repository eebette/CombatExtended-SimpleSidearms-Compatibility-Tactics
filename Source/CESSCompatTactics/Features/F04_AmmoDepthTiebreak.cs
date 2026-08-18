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
    /// Feature 4: ammo-depth tiebreak. The core patch's axis 3 made SS's selection
    /// binary on ammo (has / hasn't); a gun with 5 loose rounds ranks equal to one
    /// with 200 spare. Among candidates scoring within a strict epsilon of the
    /// winner, prefer the one with the deepest ammo reserves.
    ///
    /// GUARD (strict tiebreak): the epsilon window keeps this subordinate to the
    /// primary DPS ranking — as a general weighting it would redesign SS scoring.
    /// Scoring calls the same public entry point the core patch's P03 uses
    /// (StatCalculator.RangedDPS, itself CE-corrected by P02), so numbers can't
    /// diverge from the ranking being tiebroken.
    /// </summary>
    [HarmonyPatch(typeof(GettersFilters), nameof(GettersFilters.findBestRangedWeapon))]
    public static class AmmoDepthTiebreak_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, LocalTargetInfo? target, bool skipManualUse,
            bool skipDangerous, bool skipEMP, bool includeEquipped,
            ref (ThingWithComps weapon, float dps, float averageSpeed) __result)
        {
            TacticsSettings settings = TacticsMod.Settings;
            if (!settings.ammoDepthTiebreak || __result.weapon == null || pawn == null)
            {
                return;
            }

            float distance = target.HasValue && target.Value.IsValid
                ? target.Value.Cell.DistanceTo(pawn.Position)
                : -1f;
            float bias = SSCore.Settings.SpeedSelectionBiasRanged;
            float epsilon = settings.tiebreakEpsilonPct / 100f;
            float floor = __result.dps * (1f - epsilon);

            ThingWithComps bestWeapon = __result.weapon;
            long bestDepth = AmmoDepth(pawn, bestWeapon);
            float bestDps = __result.dps;

            foreach (ThingWithComps candidate in Candidates(pawn, skipManualUse, skipDangerous, skipEMP, includeEquipped))
            {
                if (candidate == __result.weapon)
                {
                    continue;
                }
                float dps = distance >= 0f
                    ? StatCalculator.RangedDPS(candidate, bias, __result.averageSpeed, distance)
                    : StatCalculator.RangedDPSAverage(candidate, bias, __result.averageSpeed);
                if (dps < floor)
                {
                    continue; // outside the tie window — primary ranking stands
                }
                long depth = AmmoDepth(pawn, candidate);
                if (depth > bestDepth)
                {
                    bestWeapon = candidate;
                    bestDepth = depth;
                    bestDps = dps;
                }
            }

            if (bestWeapon != __result.weapon)
            {
                __result = (bestWeapon, bestDps, __result.averageSpeed);
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

        /// <summary>Rounds on hand for this weapon: magazine + carried spares. Weapons
        /// outside CE's ammo system never run dry — effectively infinite depth.</summary>
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

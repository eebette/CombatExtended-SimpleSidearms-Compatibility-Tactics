using System.Linq;
using CombatExtended;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using Verse;
using Verse.AI;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;

namespace CESSCompatTactics.Features
{
    /// <summary>
    /// Feature 1: reload-abort when threatened. A colonist running CE's reload job
    /// with a hostile in effective range swaps to a loaded carried weapon instead of
    /// finishing the reload.
    ///
    /// No Harmony patch on the reload driver: a lightweight GameComponent scan every
    /// 30 ticks over the (few) pawns currently reloading. Target provenance per the
    /// brief: vanilla AttackTargetFinder.BestAttackTarget supplies the target, and
    /// its non-null result IS the "threatened" trigger — trigger and target are one
    /// computation. GUARD: player-forced reload jobs are untouchable. The abandoned
    /// reload is left to CE's own idle reload flow (JobGiver_CheckReload) — no
    /// resume bookkeeping here.
    /// </summary>
    public class ReloadAbortComponent : GameComponent
    {
        private const int CheckIntervalTicks = 30;

        public ReloadAbortComponent(Game game)
        {
        }

        public override void GameComponentTick()
        {
            if (!TacticsMod.Settings.reloadAbort)
            {
                return;
            }
            if (Find.TickManager.TicksGame % CheckIntervalTicks != 0)
            {
                return;
            }
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.CurJobDef != CE_JobDefOf.ReloadWeapon
                    || pawn.CurJob.playerForced
                    || pawn.Downed || pawn.InMentalState
                    || !pawn.IsValidSidearmsCarrierRightNow())
                {
                    continue;
                }
                TryAbort(pawn);
            }
        }

        private static void TryAbort(Pawn pawn)
        {
            float maxRange = MaxCarriedRange(pawn);
            if (maxRange <= 0f)
            {
                return;
            }
            var target = (Thing)AttackTargetFinder.BestAttackTarget(
                pawn,
                TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable | TargetScanFlags.NeedLOSToAll,
                maxDist: maxRange);
            if (target == null)
            {
                return; // not threatened — finish the reload in peace
            }

            // NOTE: SS's ranking (with the core patch's axis 3) counts
            // reloadable-from-inventory weapons as viable — which is exactly the gun
            // being reloaded right now. Mid-reload the comparison must be "loaded THIS
            // INSTANT", so scan loaded secondaries directly instead of findBest, and
            // equip the specific winner (equipBest would re-pick the reloadable
            // primary and loop).
            float distance = target.Position.DistanceTo(pawn.Position);
            float bias = PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings.SpeedSelectionBiasRanged;
            ThingWithComps winner = null;
            float winnerDps = 0f;
            foreach (ThingWithComps weapon in pawn.GetCarriedWeapons(includeEquipped: false, includeTools: false))
            {
                if (!weapon.def.IsRangedWeapon || !IsLoaded(weapon)
                    || GettersFilters.isManualUse(weapon)
                    || GettersFilters.isDangerousWeapon(weapon)
                    || GettersFilters.isEMPWeapon(weapon))
                {
                    continue;
                }
                float dps = StatCalculator.RangedDPS(weapon, bias, 0f, distance);
                if (dps > winnerDps)
                {
                    winnerDps = dps;
                    winner = weapon;
                }
            }
            if (winner == null)
            {
                return; // nothing loaded to swap to — keep reloading
            }

            // Mirror the core patch's explicit-swap semantics (axis 5): end the reload
            // cleanly FIRST — its guard blocks SS-side swaps while the job runs.
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            WeaponAssingment.equipSpecificWeaponFromInventory(pawn, winner, dropCurrent: false, intentionalDrop: false);
        }

        private static bool IsLoaded(ThingWithComps weapon)
        {
            CompAmmoUser user = weapon.TryGetComp<CompAmmoUser>();
            if (user == null || !user.UseAmmo)
            {
                return true; // no CE ammo concept — always usable
            }
            // Loaded THIS INSTANT: rounds in the magazine, or for magazine-less
            // weapons, rounds on hand to fire from directly.
            return user.HasMagazine ? user.CurMagCount > 0 : user.HasAmmo;
        }

        private static float MaxCarriedRange(Pawn pawn)
        {
            float max = 0f;
            foreach (ThingWithComps weapon in pawn.GetCarriedWeapons(includeEquipped: true, includeTools: false))
            {
                if (!weapon.def.IsRangedWeapon)
                {
                    continue;
                }
                float range = weapon.def.Verbs?.FirstOrDefault()?.range ?? 0f;
                if (range > max)
                {
                    max = range;
                }
            }
            return max;
        }
    }
}

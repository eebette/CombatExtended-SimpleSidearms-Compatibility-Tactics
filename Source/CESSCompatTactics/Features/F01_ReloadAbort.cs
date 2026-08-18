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

            var (best, _, _) = GettersFilters.findBestRangedWeapon(pawn, new LocalTargetInfo(target));
            if (best == null || best == pawn.equipment?.Primary || !IsLoaded(best))
            {
                return;
            }

            // Mirror the core patch's explicit-swap semantics (axis 5): end the reload
            // cleanly FIRST — its guard blocks SS preference swaps while the job runs.
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            WeaponAssingment.equipBestWeaponFromInventoryByPreference(pawn, DroppingModeEnum.Combat,
                target: target as Pawn);
        }

        private static bool IsLoaded(ThingWithComps weapon)
        {
            CompAmmoUser user = weapon.TryGetComp<CompAmmoUser>();
            if (user == null || !user.UseAmmo)
            {
                return true; // no CE ammo concept — always usable
            }
            return !user.HasMagazine || user.CurMagCount > 0;
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

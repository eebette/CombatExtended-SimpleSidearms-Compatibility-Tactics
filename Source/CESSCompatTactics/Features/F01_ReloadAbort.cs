using System.Linq;
using CombatExtended;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using SimpleSidearms.rimworld;
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
            // Every loaded map, not just the watched one — whether the feature
            // protects a pawn must not depend on where the camera is (T3-10).
            foreach (Map map in Find.Maps)
            {
                Tick(map);
            }
        }

        private static void Tick(Map map)
        {
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.CurJobDef != CE_JobDefOf.ReloadWeapon
                    || pawn.CurJob.playerForced
                    || pawn.Downed || pawn.InMentalState
                    || !pawn.IsValidSidearmsCarrierRightNow())
                {
                    continue;
                }
                try
                {
                    TryAbort(pawn);
                }
                catch (System.Exception e)
                {
                    // Once per session, not per tick: this runs from the game loop.
                    Log.ErrorOnce(PatchGuard.LogPrefix + "Reload-abort scan failed for " + pawn + ". " + e,
                                  0x54414305 ^ (pawn?.thingIDNumber ?? 0));
                }
            }
        }

        private static void TryAbort(Pawn pawn)
        {
            // ONLY reloads of the gun in the pawn's hands. CE's undrafted top-offs and
            // F07's drafted top-offs create ReloadWeapon jobs for INVENTORY guns while
            // the primary is loaded and fine; aborting those swapped a working primary
            // for nothing and livelocked against the think tree's re-issue (T3-1).
            if (pawn.CurJob?.targetB.Thing != pawn.equipment?.Primary || pawn.equipment?.Primary == null)
            {
                return;
            }
            // A forced weapon mid-reload stays put: every SS auto-swap respects the
            // player's forced flag, and this abort is no exception (T3-5).
            if (CompSidearmMemory.GetMemoryCompForPawn(pawn, fillExistingIfCreating: false)
                    ?.IsCurrentWeaponForced(alsoCountPreferredOrDefault: false) ?? false)
            {
                return;
            }
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
                // SS's own usability rule (biocode, bladelink bond, Ideology role),
                // honoring the player's allow-blocked setting — filtered at candidacy,
                // never discovered at equip time after the reload is already dead (T3-5).
                if (!PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings.AllowBlockedWeaponUse
                    && !StatCalculator.canUseSidearmInstance(weapon, pawn, out _))
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
            // startNewJob:false, deliberately: the default restarts the think tree
            // synchronously INSIDE this call, and whatever it hands the pawn runs
            // before the equip below — the swap must land on a jobless pawn.
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
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

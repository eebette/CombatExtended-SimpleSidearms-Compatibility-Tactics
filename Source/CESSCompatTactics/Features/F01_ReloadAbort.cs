using System;
using System.Linq;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using SimpleSidearms.rimworld;
using System.Collections.Generic;
using Verse;
using Verse.AI;
using static PeteTimesSix.SimpleSidearms.Utilities.Enums;

namespace CESSCompatTactics.Features
{
    /// <summary>
    /// Feature 1: reload-abort when threatened. A colonist mid-reload of the gun in
    /// their HANDS, with a hostile in effective range, swaps to a loaded carried
    /// weapon instead of finishing the reload. Backpack top-offs (CE's undrafted
    /// pass, F07's drafted pass) are never touched — the primary is fine (T3-1).
    ///
    /// No Harmony patch on the reload driver: a lightweight GameComponent scan every
    /// 30 ticks over the (few) pawns currently reloading, on every loaded map.
    /// Target provenance per the brief: vanilla AttackTargetFinder.BestAttackTarget
    /// supplies the target, and its non-null result IS the "threatened" trigger.
    /// GUARDS: player-forced reload jobs and player-forced weapons are untouchable.
    /// The abandoned reload is left to CE's own idle reload flow.
    ///
    /// SELECTION IS SS'S, NOT OURS (convergence C3): the winner comes from SS's own
    /// findBestRangedWeapon — its full filter chain (biocode, VFE shields,
    /// Tacticowl, manual/dangerous/EMP, the per-weapon range window) and, through
    /// F04's scope, the same target-aware scoring as everywhere else. The one thing
    /// SS cannot know is that mid-reload "viable" must mean "loaded THIS INSTANT"
    /// (the core patch's axis 3 deliberately counts reloadable-from-inventory guns
    /// as viable — including the very gun being reloaded), so for the one call a
    /// scope hides every ranged gun without rounds ready to fire, the same
    /// call-lifetime pattern the core patch's P03 uses for dry guns. Nothing here
    /// enumerates or re-implements a filter; SS growing a new one is inherited.
    /// </summary>
    public class ReloadAbortComponent : GameComponent
    {
        private const int CheckIntervalTicks = 30;

        public ReloadAbortComponent(Game game)
        {
        }

        public override void GameComponentTick()
        {
            if (TacticsMod.Settings == null || !TacticsMod.Settings.reloadAbort)
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
            // ONLY reloads of the gun in the pawn's hands (T3-1).
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

            // SS's own selection, with SS's own argument conventions (the same shape
            // its warmup auto-switch uses) and the loaded-this-instant scope open.
            bool mechTarget = (target as Pawn)?.RaceProps?.IsMechanoid ?? false;
            bool skipDangerous = pawn.IsColonistPlayerControlled
                                 && PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings.SkipDangerousWeapons;
            bool skipEMP = (pawn.IsColonistPlayerControlled
                            && PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings.SkipEMPWeapons)
                           || !mechTarget;
            ThingWithComps winner;
            float dps;
            LoadedNowScope.For = pawn;
            try
            {
                (winner, dps, _) = GettersFilters.findBestRangedWeapon(
                    pawn, new LocalTargetInfo(target),
                    skipManualUse: true, skipDangerous: skipDangerous, skipEMP: skipEMP,
                    includeEquipped: false);
            }
            finally
            {
                LoadedNowScope.For = null;
            }
            if (winner == null || dps <= 0f)
            {
                return; // nothing loaded reaches this threat — keep reloading
            }

            // Mirror the core patch's explicit-swap semantics (axis 5): end the reload
            // cleanly FIRST — its guard blocks SS-side swaps while the job runs.
            // startNewJob:false, deliberately: the default restarts the think tree
            // synchronously INSIDE this call, and whatever it hands the pawn runs
            // before the equip below — the swap must land on a jobless pawn.
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            WeaponAssingment.equipSpecificWeaponFromInventory(pawn, winner, dropCurrent: false, intentionalDrop: false);
        }

        internal static bool LoadedNow(ThingWithComps weapon)
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
                // The live primary verb (attachments, verb-swapped guns), the way SS
                // reads range; def fallback for anything without equippable comps.
                float range = weapon.TryGetComp<CompEquippable>()?.PrimaryVerb?.verbProps?.range
                              ?? weapon.def.Verbs?.FirstOrDefault()?.range ?? 0f;
                if (range > max)
                {
                    max = range;
                }
            }
            return max;
        }
    }

    /// <summary>Call-lifetime scope for the one ask above.</summary>
    internal static class LoadedNowScope
    {
        internal static Pawn For;
    }

    /// <summary>
    /// While the reload-abort's ask is in flight, the pawn's carried-weapon list
    /// shows only ranged guns with rounds ready to fire — the mid-reload meaning of
    /// "viable". The same seam the core patch's P03 uses to hide dry guns during
    /// its re-run; both postfixes filter, so their order does not matter.
    /// </summary>
    [HarmonyPatch(typeof(Extensions), nameof(Extensions.GetCarriedWeapons),
                  new[] { typeof(Pawn), typeof(bool), typeof(bool) })]
    public static class Extensions_GetCarriedWeapons_LoadedNowPatch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(Extensions), "GetCarriedWeapons",
            new[] { typeof(Pawn), typeof(bool), typeof(bool) },
            "reload-abort cannot restrict Simple Sidearms' selection to loaded weapons and stays inactive.");

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, List<ThingWithComps> __result)
        {
            try
            {
                PostfixInner(pawn, __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Loaded-now filter failed; reload-abort may "
                              + "consider a gun that needs reloading. " + e, 0x5441430E);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, List<ThingWithComps> __result)
        {
            if (LoadedNowScope.For == null || LoadedNowScope.For != pawn || __result == null)
            {
                return;
            }
            __result.RemoveAll(w => w.def.IsRangedWeapon && !ReloadAbortComponent.LoadedNow(w));
        }
    }
}

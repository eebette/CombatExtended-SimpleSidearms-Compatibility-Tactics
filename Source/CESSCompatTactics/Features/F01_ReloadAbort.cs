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
    /// GUARDS: player-ORDERED reloads (the reload gizmo) and player-forced weapons
    /// are untouchable. CE stamps job.playerForced=true on EVERY reload its
    /// TryStartReload issues — the gizmo AND the automatic ran-dry reload that
    /// fires the instant a magazine empties mid-attack (T4-2: gating on the flag
    /// alone made the abort dead in its flagship scenario). The gizmo's one
    /// distinct entry point, CompAmmoUser.SyncedTryStartReload, is therefore
    /// tagged by the marker patch below; a player-forced reload with no fresh
    /// tag is CE's automatic one and is fair game. If the marker cannot install
    /// (upstream drift), EVERY player-forced reload stays untouchable — the
    /// conservative direction. The abandoned reload is left to CE's own flow.
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
            // Constructed once per new-or-loaded game: the marker's stamps do not
            // survive the transition (see PlayerReloadMarker.Reset).
            PlayerReloadMarker.Reset();
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
                    || IsPlayerOrderedReload(pawn)
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

        /// <summary>See the header: playerForced alone cannot separate the gizmo
        /// from CE's ran-dry auto-reload — only a marker-tagged job is the
        /// player's. No marker installed → every forced job is (conservatively)
        /// the player's.</summary>
        private static bool IsPlayerOrderedReload(Pawn pawn)
        {
            if (!pawn.CurJob.playerForced)
            {
                return false; // CE's lull top-offs (JobGiver_CheckReload) land here
            }
            if (!PlayerReloadMarker.Installed)
            {
                return true;
            }
            return PlayerReloadMarker.WasPlayerOrdered(pawn, pawn.CurJob.startTick);
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

    /// <summary>
    /// The reload GIZMO's fingerprint: CompAmmoUser.SyncedTryStartReload is the one
    /// entry only the player's command reaches (CE's automatic ran-dry reload calls
    /// TryStartReload directly). The prefix stamps the wielder and tick; the reload
    /// job starts synchronously inside the same call, so its startTick equals the
    /// stamp — that equality IS "the player ordered this one" (T4-2). Multiplayer's
    /// sync layer defers the inner call, but this module targets single-player,
    /// where the path is synchronous.
    /// </summary>
    [HarmonyPatch(typeof(CompAmmoUser), "SyncedTryStartReload", new Type[0])]
    public static class CompAmmoUser_SyncedTryStartReload_Patch
    {
        public static bool Prepare()
        {
            PlayerReloadMarker.Installed = PatchGuard.Require(typeof(CompAmmoUser), "SyncedTryStartReload",
                new Type[0],
                "the reload gizmo cannot be told apart from CE's automatic ran-dry reload, so "
                + "reload-abort will leave EVERY player-forced reload alone (lull top-offs only).");
            return PlayerReloadMarker.Installed;
        }

        [HarmonyPrefix]
        public static void Prefix(CompAmmoUser __instance)
        {
            try
            {
                Pawn wielder = __instance?.Wielder;
                if (wielder != null)
                {
                    PlayerReloadMarker.Stamp(wielder);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Reload-gizmo marker failed; player-forced "
                              + "reloads stay untouchable this session. " + e, 0x5441430F);
                PlayerReloadMarker.Installed = false;
            }
        }

        /// <summary>Prepare runs BEFORE the prefix is applied, and application can
        /// still throw (a co-loaded mod's broken patch on the same method fails the
        /// whole patch merge) — without this, Installed stayed true with no prefix
        /// installed, silently inverting the documented conservative degrade into
        /// "every player-forced reload is abortable" (T5-A). The exception is
        /// returned, not swallowed, so Bootstrap's per-class accounting still logs
        /// the failure.</summary>
        [HarmonyCleanup]
        public static Exception Cleanup(Exception ex)
        {
            if (ex != null)
            {
                PlayerReloadMarker.Installed = false;
            }
            return ex;
        }
    }

    /// <summary>
    /// The under-barrel MODE SWITCH is the fourth reload entry and it is a player
    /// command too: CompUnderBarrel.SwitchToUB / SwithToB (sic — upstream's own
    /// spelling) are gizmo-invoked and end in CompAmmo.TryStartReload() when the
    /// launcher needs loading, which stamps playerForced like the ran-dry auto
    /// path. Unstamped, the abort would override an explicit "use the launcher
    /// now" (T5-E). Same marker join as the gizmo: the switch ends any running
    /// reload job and starts the new one inside the same call, so the stamp
    /// lands at-or-before its startTick. These classes never touch
    /// PlayerReloadMarker.Installed — if CE reshapes CompUnderBarrel, only the
    /// switch-reload protection is lost, and the guard names exactly that.
    /// </summary>
    [HarmonyPatch(typeof(CompUnderBarrel), nameof(CompUnderBarrel.SwitchToUB), new Type[0])]
    public static class CompUnderBarrel_SwitchToUB_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(CompUnderBarrel), "SwitchToUB",
            new Type[0],
            "under-barrel mode-switch reloads cannot be marked as player-ordered; "
            + "reload-abort may interrupt them (main gizmo reloads stay protected).");

        [HarmonyPrefix]
        public static void Prefix(CompUnderBarrel __instance)
        {
            UnderBarrelMarker.StampSwitch(__instance);
        }
    }

    [HarmonyPatch(typeof(CompUnderBarrel), nameof(CompUnderBarrel.SwithToB), new Type[0])]
    public static class CompUnderBarrel_SwithToB_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(CompUnderBarrel), "SwithToB",
            new Type[0],
            "under-barrel switch-back reloads cannot be marked as player-ordered; "
            + "reload-abort may interrupt them (main gizmo reloads stay protected).");

        [HarmonyPrefix]
        public static void Prefix(CompUnderBarrel __instance)
        {
            UnderBarrelMarker.StampSwitch(__instance);
        }
    }

    internal static class UnderBarrelMarker
    {
        internal static void StampSwitch(CompUnderBarrel comp)
        {
            try
            {
                Pawn wielder = comp?.CompAmmo?.Wielder;
                if (wielder != null)
                {
                    PlayerReloadMarker.Stamp(wielder);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Under-barrel switch marker failed; the abort "
                              + "may interrupt switch reloads this session. " + e, 0x54414310);
            }
        }
    }

    internal static class PlayerReloadMarker
    {
        internal static bool Installed;
        private static readonly Dictionary<Pawn, int> stamps = new Dictionary<Pawn, int>();

        internal static void Stamp(Pawn pawn)
        {
            // Opportunistic prune keeps the map at live-order size.
            if (stamps.Count > 32)
            {
                int now = Find.TickManager.TicksGame;
                stamps.RemoveAll(kv => now - kv.Value > 2500 || kv.Key.Destroyed);
            }
            stamps[pawn] = Find.TickManager.TicksGame;
        }

        internal static bool WasPlayerOrdered(Pawn pawn, int jobStartTick)
        {
            // AT-OR-AFTER, not equality: re-clicking the gizmo mid-reload re-stamps
            // while CE's TryStartReload early-outs on the already-running job — the
            // stamp then postdates startTick, and exact equality would strip the very
            // protection the click expressed (T5-C). A stamp can only postdate a
            // running reload's start if the player ordered a reload DURING it, which
            // blesses that job; a stamp older than the start belongs to a previous
            // job and correctly fails.
            return stamps.TryGetValue(pawn, out int tick) && tick >= jobStartTick;
        }

        /// <summary>New game or loaded save: stamps are session-plumbing keyed on
        /// object identity and tick clocks that both reset across loads — stale
        /// entries could otherwise pin dead pawn graphs and, after loading an
        /// earlier-tick save, sit unprunable behind the tick-delta test (T5-B).
        /// Cost of the wipe: a reload that was mid-flight at save time degrades to
        /// flag-only protection for that one job.</summary>
        internal static void Reset()
        {
            stamps.Clear();
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

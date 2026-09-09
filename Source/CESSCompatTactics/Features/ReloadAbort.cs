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
    /// Patches colonist behavior so that a colonist mid-reload of the gun in
    /// their hands swaps to a loaded carried if a hostile is in effective range.
    /// </summary>
    public class ReloadAbortComponent : GameComponent
    {
        private const int CheckIntervalTicks = 30;

        public ReloadAbortComponent(Game game)
        {
            // Constructed once per new-or-loaded game.
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
            // Every loaded map, not just the watched one..
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
            // ONLY reloads of the gun in the pawn's hands.
            if (pawn.CurJob?.targetB.Thing != pawn.equipment?.Primary || pawn.equipment?.Primary == null)
            {
                return;
            }
            // A forced weapon mid-reload stays put.
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
                return; // not threatened - finish the reload in peace
            }

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
                return; // nothing loaded reaches this threat - keep reloading
            }

            // Mirror the core patch's swap semantics: end the reload cleanly first.
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            WeaponAssingment.equipSpecificWeaponFromInventory(pawn, winner, dropCurrent: false, intentionalDrop: false);
        }

        /// <summary>Only a marker-tagged job is the player's.</summary>
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
                return true; // no CE ammo concept - always usable
            }
            // Loaded THIS INSTANT: rounds in the magazine or on hand.
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
    /// The reload GIZMO's fingerprint: CompAmmoUser.SyncedTryStartReload is the entry.
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

        /// <summary>Without this, Installed stayed true with no prefix installed.</summary>
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
    /// Also patch the under-barrel MODE SWITCH reload entry.
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
            // A stamp can only postdate a running reload's start if the player
            // ordered a reload DURING it, which blesses that job; a stamp older
            // than the start belongs to a previous job and correctly fails.
            return stamps.TryGetValue(pawn, out int tick) && tick >= jobStartTick;
        }

        /// <summary>Clear on session reset.</summary>
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
    /// Removes unloaded guns from switch candidates.
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

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using Verse;

namespace CESSCompatTactics.Features
{
    /// <summary>
    /// Drafted sidearm top-off. CE only reloads the primary while drafted.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_CheckReload), "DoReloadCheck",
                  new[] { typeof(Pawn), typeof(ThingWithComps), typeof(AmmoDef) },
                  new[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out })]
    public static class JobGiver_CheckReload_DoReloadCheck_Patch
    {
        public static bool Prepare()
        {
            // The gates below re-read CE's drafted-path conditions (values live,
            // SHAPES copied - the drafted-top-off ruling: postfix + fingerprint). Any change to the
            // upstream method turns from silent divergence into a loud re-verify.
            UpstreamFingerprint.Verify(typeof(JobGiver_CheckReload), "DoReloadCheck",
                UpstreamFingerprint.DoReloadCheckHash,
                "the drafted-lull gate conditions the drafted top-off re-reads per sidearm");
            return PatchGuard.Require(typeof(JobGiver_CheckReload), "DoReloadCheck",
                new[] { typeof(Pawn), typeof(ThingWithComps).MakeByRefType(), typeof(AmmoDef).MakeByRefType() },
                "drafted pawns will not top off sidearm magazines during combat lulls.");
        }

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref ThingWithComps reloadWeapon, ref AmmoDef reloadAmmo,
                                   ref bool __result)
        {
            try
            {
                PostfixInner(pawn, ref reloadWeapon, ref reloadAmmo, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Drafted sidearm top-off failed; CE's own "
                              + "reload check stands. " + e, 0x54414306);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, ref ThingWithComps reloadWeapon, ref AmmoDef reloadAmmo,
                                         ref bool __result)
        {
            if (__result || !TacticsMod.Settings.draftedSidearmReload)
            {
                return; // CE found its own reload - never compete with it
            }
            if (pawn == null || !pawn.Drafted || pawn.Downed)
            {
                return; // the undrafted path already covers inventory guns
            }
            if (Controller.settings.OpportunisticReloadMode == OpportunisticReloadMode.Off)
            {
                return;
            }
            CompInventory inventory = pawn.TryGetComp<CompInventory>();
            if (inventory?.rangedWeaponList == null)
            {
                return;
            }
            foreach (ThingWithComps gun in inventory.rangedWeaponList)
            {
                CompAmmoUser comp = gun.TryGetComp<CompAmmoUser>();
                if (comp == null || !comp.HasMagazine)
                {
                    continue;
                }
                // IsOpportunisticReloadActive minus the Wielder requirement.
                if (comp.MagSize <= 1 || (gun.def.weaponTags?.Contains("OpportunisticReloadDisabled") ?? false))
                {
                    continue;
                }
                // From CE: only guns at or below their TryReloadOn mark are worth a lull reload.
                if (comp.CurMagCount > comp.TryReloadOn || comp.CurMagCount >= comp.MagSize)
                {
                    continue;
                }
                // CE's drafted availability rule: one round on hand is enough.
                if (comp.UseAmmo && inventory.AmmoCountOfDef(comp.SelectedAmmo) < 1)
                {
                    continue;
                }
                if (Find.TickManager.TicksGame - pawn.LastAttackTargetTick < comp.MinimalTicksAfterFight)
                {
                    continue;
                }
                float safeDistance = comp.SafeDistanceToReload;
                bool hostileNear = pawn.Map != null && pawn.Map.mapPawns.AllPawnsSpawned.Any(x =>
                    x.Position.InHorDistOf(pawn.Position, safeDistance)
                    && !x.IsPsychologicallyInvisible() && x.HostileTo(pawn));
                if (hostileNear)
                {
                    continue;
                }
                reloadWeapon = gun;
                reloadAmmo = comp.SelectedAmmo;
                __result = true;
                return;
            }
        }
    }
}

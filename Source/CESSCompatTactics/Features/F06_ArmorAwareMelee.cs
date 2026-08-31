using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using Verse;
using SSCore = PeteTimesSix.SimpleSidearms.SimpleSidearms;

namespace CESSCompatTactics.Features
{
    /// <summary>
    /// Feature 6: armor-aware melee choice. SS's melee scoring averages
    /// damage/penetration; under CE's armor model the right pick is
    /// target-dependent (blunt mace vs an armored target, fast blade vs flesh).
    /// Postfix on SS's own findBestMeleeWeapon — which already carries the target —
    /// re-ranking candidates by their best CE melee tool's effectiveness against
    /// THAT target (TargetScoring.MeleeScore). Target comes from SS's caller (the
    /// CQC attacker / selection context) per the brief's provenance rules; no
    /// melee target choice is invented here. Extends the same path core P06
    /// feeds; no fork. Inert when the toggle is off or no target flows in.
    /// </summary>
    [HarmonyPatch(typeof(GettersFilters), nameof(GettersFilters.findBestMeleeWeapon),
                  new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool), typeof(Pawn) },
                  new[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal })]
    public static class ArmorAwareMelee_Patch
    {
        public static bool Prepare() => PatchGuard.Require(typeof(GettersFilters), "findBestMeleeWeapon",
            new[] { typeof(Pawn), typeof(ThingWithComps).MakeByRefType(), typeof(bool), typeof(bool), typeof(Pawn) },
            "armor-aware melee choice is inactive.");

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref ThingWithComps result,
            bool includeEquipped, bool includeRangedWithBash, Pawn target, ref bool __result)
        {
            try
            {
                PostfixInner(pawn, ref result, includeEquipped, includeRangedWithBash, target, ref __result);
            }
            catch (System.Exception e)
            {
                Log.ErrorOnce(PatchGuard.LogPrefix + "Melee re-rank failed; Simple Sidearms' own "
                              + "pick stands. " + e, 0x54414304);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, ref ThingWithComps result,
            bool includeEquipped, bool includeRangedWithBash, Pawn target, ref bool __result)
        {
            if (!TacticsMod.Settings.armorAwareMelee || target == null || pawn == null)
            {
                return;
            }

            float bias = SSCore.Settings.SpeedSelectionBiasMelee;
            ThingWithComps best = null;
            float bestScore = 0f;
            foreach (ThingWithComps candidate in pawn.GetCarriedWeapons(includeEquipped: includeEquipped, includeTools: true))
            {
                if (candidate.def.IsRangedWeapon && !includeRangedWithBash)
                {
                    continue;
                }
                float fallback = StatCalculator.getMeleeDPSBiased(candidate, pawn, bias, 0f);
                float score = TargetScoring.MeleeScore(candidate, target, fallback);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best != null && best != result)
            {
                result = best;
                __result = true;
            }
        }
    }
}

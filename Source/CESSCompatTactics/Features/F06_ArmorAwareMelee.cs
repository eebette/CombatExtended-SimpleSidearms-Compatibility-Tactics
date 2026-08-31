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
    /// Feature 6: armor-aware melee choice. SS's melee ranking (with the core
    /// patch's P12 penetration input) is target-blind by design — P12's header
    /// hands the target axis to this module. Postfix on SS's own
    /// findBestMeleeWeapon — which already carries the target — re-ranking
    /// candidates by SS's OWN biased score times the fraction of their damage
    /// that survives THIS target's armor (TargetScoring.MeleeTargetFactor,
    /// CE's TryPenetrateArmor in expectation form). SS keeps the ranking and
    /// the speed bias; the factor only adds the matchup. Target comes from
    /// SS's caller per the brief's provenance rules; no melee target choice is
    /// invented here. Inert when the toggle is off or no target flows in.
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
                // SS's biased score factors as (dmg/biasedSpeed) × (1 + penetration):
                // the (1 + pen) term is a GENERIC armor bonus, paid against every
                // target. Divide it out and substitute the actual through-armor
                // fraction for THIS target — against flesh that leaves pure CE
                // damage-per-second (a fast blade beats a slow mace), against armor
                // the fraction takes over. MeleePenetration is the same P12-backed
                // input SS itself used, so the division is exact, not a guess.
                float score = StatCalculator.getMeleeDPSBiased(candidate, pawn, bias, 0f)
                              / (1f + StatCalculator.MeleePenetration(candidate, pawn))
                              * TargetScoring.MeleeTargetFactor(candidate, target);
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

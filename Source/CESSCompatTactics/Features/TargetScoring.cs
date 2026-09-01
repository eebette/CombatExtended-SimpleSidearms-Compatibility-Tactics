using System;
using System.Linq;
using System.Reflection;
using CESimpleSidearmsCompat;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CESSCompatTactics.Features
{
    /// <summary>
    /// Features 5/6 shared math: what fraction of a weapon's damage actually arrives
    /// against THIS target, under CE's own armor mechanics. Pure scoring — never
    /// writes SelectedAmmo or any other state.
    ///
    /// Per-hit arithmetic is CE'S OWN CODE, not a reproduction (convergence ruling):
    /// ArmorUtilityCE.TryPenetrateArmor guards every side effect — the armor-damage
    /// roll and the durability hit — behind its `armor != null` block, so invoking
    /// the real private method with armor:null runs exactly the live arithmetic
    /// (through-factor, the penAmount==0 pass-whole case, noDamageOnDeflect, the
    /// sharp full-deflect verdict) with zero mutation. A cached open delegate makes
    /// the call free; if CE reshapes the method the delegate misses and the modeled
    /// FALLBACK below takes over with a loud, named error.
    ///
    /// Two pieces remain modeled, each cited and fingerprint-guarded (a checksum of
    /// the upstream method's IL at load turns silent drift into a loud re-verify
    /// error): the deflect-to-blunt CONVERSION — cbrt(bluntPen × 10000)/10, from
    /// GetDeflectDamageInfo, whose live execution would need a fabricated
    /// DamageInfo and the Verb_MeleeAttackCE.LastAttackVerb global — and the
    /// composition order (deflected sharp re-runs as blunt vs blunt armor, per
    /// GetAfterArmorDamage). The AGGREGATION — chance-weighting over tools and
    /// maneuvers, secondary-damage summation, the harmsHealth rule — copies
    /// nothing: CE has no expected-damage-vs-target function at any layer.
    ///
    /// Damage that cannot harm health (EMP's stun) leaves BOTH sides of the ratio:
    /// no derivable exchange rate, so SS's own EMP mode filters keep governing.
    /// Weapons the model CANNOT judge (no CE projectile, no CE tools) report
    /// modeled=false via the Try variants: callers leave their scores untouched and
    /// exclude them from all-hopeless reasoning — the same mixing SS itself does
    /// with the features off (convergence C5 ruling).
    /// </summary>
    public static class TargetScoring
    {
        /// <summary>CE's private TryPenetrateArmor, invoked with armor:null (the
        /// pure path). Null when upstream reshaped it — fallback model runs.</summary>
        private delegate bool TryPenDel(DamageDef def, float armorAmount, ref float penAmount,
                                        ref float dmgAmount, Thing armor, float partDensity);

        private static readonly TryPenDel TryPen;

        static TargetScoring()
        {
            try
            {
                MethodInfo mi = AccessTools.Method(typeof(ArmorUtilityCE), "TryPenetrateArmor",
                    new[] { typeof(DamageDef), typeof(float), typeof(float).MakeByRefType(),
                            typeof(float).MakeByRefType(), typeof(Thing), typeof(float) });
                if (mi != null)
                {
                    TryPen = AccessTools.MethodDelegate<TryPenDel>(mi);
                }
            }
            catch (Exception e)
            {
                TryPen = null;
                Log.Error(PatchGuard.LogPrefix + "Could not bind CE's TryPenetrateArmor — target "
                          + "scoring falls back to its modeled arithmetic (re-verify against CE). " + e);
            }
            if (TryPen == null)
            {
                Log.Error(PatchGuard.LogPrefix + "CE's TryPenetrateArmor is not the expected shape — "
                          + "target scoring uses its modeled fallback arithmetic (re-verify against CE).");
            }
            UpstreamFingerprint.Verify(typeof(ArmorUtilityCE), "TryPenetrateArmor",
                UpstreamFingerprint.TryPenetrateArmorHash,
                "the armor-through arithmetic TargetScoring executes");
            UpstreamFingerprint.Verify(typeof(ArmorUtilityCE), "GetDeflectDamageInfo",
                UpstreamFingerprint.GetDeflectDamageInfoHash,
                "the deflect-to-blunt conversion TargetScoring models");
        }

        /// <summary>
        /// Fraction of the loaded projectile's damage (primary + health-harming
        /// secondaries, e.g. an ion round's ballistic core) that arrives through the
        /// target's armor. False when the weapon has no CE projectile to judge —
        /// callers must then leave the score untouched (modeled=false).
        /// </summary>
        public static bool TryRangedMultiplier(ThingWithComps weapon, Pawn target, out float factor)
        {
            factor = 1f;
            if (target == null)
            {
                return false;
            }
            ThingDef projectile = CompatUtil.CurrentProjectile(weapon);
            var props = projectile?.projectile as ProjectilePropertiesCE;
            if (props == null)
            {
                return false; // not modelable: unpatched mod gun — do not pretend
            }
            float primaryDmg = props.GetDamageAmount(weapon);
            float sharpArmor = target.GetStatValue(StatDefOf.ArmorRating_Sharp);
            float bluntArmor = target.GetStatValue(StatDefOf.ArmorRating_Blunt);

            float arrives = 0f;
            float total = 0f;
            if (props.damageDef?.harmsHealth ?? false)
            {
                arrives += DamageThrough(props.damageDef, primaryDmg,
                    props.armorPenetrationSharp, props.armorPenetrationBlunt, sharpArmor, bluntArmor);
                total += primaryDmg;
            }
            if (props.secondaryDamage != null)
            {
                foreach (SecondaryDamage sec in props.secondaryDamage)
                {
                    if (sec.def == null || !sec.def.harmsHealth)
                    {
                        continue;
                    }
                    // CE hands a sharp secondary the primary's penetration, an
                    // explosive one amount×0.8, anything else zero
                    // (SecondaryDamage.GetDinfo) — and zero-pen passes whole.
                    float pen = SecondaryPen(sec, props);
                    float expected = sec.amount * Mathf.Clamp01(sec.chance);
                    arrives += DamageThrough(sec.def, expected, pen, props.armorPenetrationBlunt, sharpArmor, bluntArmor);
                    total += expected;
                }
            }
            if (total <= 0f)
            {
                return false;
            }
            factor = arrives / total;
            return true;
        }

        /// <summary>Convenience wrapper: 1 when unmodelable (forensics only —
        /// feature code must use the Try variant and honor modeled=false).</summary>
        public static float RangedMultiplier(ThingWithComps weapon, Pawn target)
        {
            return TryRangedMultiplier(weapon, target, out float f) ? f : 1f;
        }

        /// <summary>
        /// Fraction of this weapon's expected melee damage that arrives through the
        /// target's armor: chance-weighted over its CE tools (the core patch's P12
        /// weighting), each tool judged by its own maneuvers' damage types, with the
        /// instance's CE MeleePenetrationFactor (material and quality) applied.
        /// False when the weapon has no CE tools to judge.
        /// </summary>
        public static bool TryMeleeTargetFactor(ThingWithComps weapon, Pawn target, out float factor)
        {
            factor = 1f;
            if (target == null || weapon?.def?.tools == null)
            {
                return false;
            }
            var tools = weapon.def.tools.OfType<ToolCE>().ToList();
            if (tools.Count == 0)
            {
                return false; // not modelable: vanilla-tool mod weapon — do not pretend
            }
            float totalChance = tools.Sum(t => t.chanceFactor);
            if (totalChance <= 0f)
            {
                return false;
            }
            float instanceFactor = weapon.GetStatValue(CE_StatDefOf.MeleePenetrationFactor);
            float sharpArmor = target.GetStatValue(StatDefOf.ArmorRating_Sharp);
            float bluntArmor = target.GetStatValue(StatDefOf.ArmorRating_Blunt);

            float arrives = 0f;
            float total = 0f;
            foreach (ToolCE tool in tools)
            {
                float weight = tool.chanceFactor / totalChance;
                float penSharp = tool.armorPenetrationSharp * instanceFactor;
                float penBlunt = tool.armorPenetrationBlunt * instanceFactor;
                var damageDefs = tool.Maneuvers
                    .Select(m => m.verb?.meleeDamageDef)
                    .Where(d => d != null && d.harmsHealth)
                    .ToList();
                if (damageDefs.Count == 0)
                {
                    continue; // stun-only tool: unscorable, see header
                }
                float perManeuver = weight * tool.power / damageDefs.Count;
                foreach (DamageDef def in damageDefs)
                {
                    arrives += DamageThrough(def, perManeuver, penSharp, penBlunt, sharpArmor, bluntArmor);
                    total += perManeuver;
                }
            }
            if (total <= 0f)
            {
                return false;
            }
            factor = arrives / total;
            return true;
        }

        /// <summary>Convenience wrapper: 1 when unmodelable (forensics only).</summary>
        public static float MeleeTargetFactor(ThingWithComps weapon, Pawn target)
        {
            return TryMeleeTargetFactor(weapon, target, out float f) ? f : 1f;
        }

        private static float SecondaryPen(SecondaryDamage sec, ProjectilePropertiesCE props)
        {
            if (sec.def.isExplosive)
            {
                return sec.amount * 0.8f;
            }
            return sec.def.armorCategory == DamageArmorCategoryDefOf.Sharp
                ? props.armorPenetrationSharp
                : 0f;
        }

        /// <summary>
        /// Expected damage arriving through armor for one damage packet — CE's own
        /// TryPenetrateArmor executed on the pure (armor:null) path, composed the way
        /// GetAfterArmorDamage composes it: a fully deflected sharp hit converts to
        /// blunt (the one modeled line) and re-runs the real arithmetic against
        /// blunt armor.
        /// </summary>
        private static float DamageThrough(DamageDef def, float dmg, float penSharp, float penBlunt,
                                           float sharpArmor, float bluntArmor)
        {
            StatDef armorStat = def.armorCategory?.armorRatingStat;
            if (armorStat == StatDefOf.ArmorRating_Sharp)
            {
                if (TryPen != null)
                {
                    float pen = penSharp;
                    float through = dmg;
                    if (TryPen(def, sharpArmor, ref pen, ref through, null, 0f))
                    {
                        return through;
                    }
                    // Full deflection: CE converts the hit to blunt at
                    // cbrt(bluntPen × 10000)/10 damage (GetDeflectDamageInfo — the
                    // modeled, fingerprint-guarded line) and runs it at blunt armor.
                    float pen2 = penBlunt;
                    float deflected = Mathf.Pow(penBlunt * 10000f, 1f / 3f) / 10f;
                    TryPen(DamageDefOf.Blunt, bluntArmor, ref pen2, ref deflected, null, 0f);
                    return deflected;
                }
                return FallbackSharp(def, dmg, penSharp, penBlunt, sharpArmor, bluntArmor);
            }
            if (armorStat == StatDefOf.ArmorRating_Blunt)
            {
                if (TryPen != null)
                {
                    float pen = penBlunt;
                    float through = dmg;
                    TryPen(def, bluntArmor, ref pen, ref through, null, 0f);
                    return through;
                }
                return dmg * FallbackPasses(penBlunt, bluntArmor);
            }
            // No armor category CE models here (Heat and untyped): pass whole.
            return dmg;
        }

        // ---- Modeled fallback (runs ONLY when CE's method could not be bound) ----
        // TryPenetrateArmor in expectation form: through = dmg × clamp01(1 − armor/pen);
        // zero-pen passes whole; sharp with armor ≥ pen deflects to the blunt
        // conversion above. Kept verbatim from the T2 model, source cited there.

        private static float FallbackSharp(DamageDef def, float dmg, float penSharp, float penBlunt,
                                           float sharpArmor, float bluntArmor)
        {
            if (penSharp <= 0f)
            {
                return dmg;
            }
            if (penSharp > sharpArmor)
            {
                return dmg * FallbackPasses(penSharp, sharpArmor);
            }
            float deflected = Mathf.Pow(penBlunt * 10000f, 1f / 3f) / 10f;
            return deflected * FallbackPasses(penBlunt, bluntArmor);
        }

        private static float FallbackPasses(float pen, float armor)
        {
            if (pen <= 0f)
            {
                return 1f;
            }
            return Mathf.Clamp01(1f - armor / pen);
        }
    }

    /// <summary>
    /// IL checksum guard for upstream methods whose ARITHMETIC this module executes
    /// or models (convergence ruling: option C). A changed checksum means the
    /// upstream body changed — the numbers may still be right, but the assumption
    /// needs re-verifying, so the drift is made LOUD instead of silent. With an
    /// empty expected hash the computed value is logged for baking in.
    /// </summary>
    internal static class UpstreamFingerprint
    {
        // Baked against CE 16.7.3.0 / SS v1.6 — re-harvest on upstream updates.
        internal const string TryPenetrateArmorHash = "7b66aeb80967e00d";
        internal const string GetDeflectDamageInfoHash = "cf4967c8bf864887";
        internal const string DoReloadCheckHash = "24ec22f8bfb61eb8";

        internal static void Verify(Type type, string method, string expected, string protects)
        {
            try
            {
                MethodBase mb = AccessTools.Method(type, method);
                if (mb == null)
                {
                    Log.Error($"{PatchGuard.LogPrefix}{type.Name}.{method} not found — {protects} "
                              + "cannot be verified against upstream.");
                    return;
                }
                ulong hash = 14695981039346656037UL; // FNV-1a
                foreach (var instruction in PatchProcessor.GetOriginalInstructions(mb))
                {
                    string token = instruction.opcode.Name + (instruction.operand?.ToString() ?? "");
                    foreach (char c in token)
                    {
                        hash = (hash ^ c) * 1099511628211UL;
                    }
                }
                string computed = hash.ToString("x16");
                if (string.IsNullOrEmpty(expected))
                {
                    Log.Message($"{PatchGuard.LogPrefix}FINGERPRINT {type.Name}.{method} = {computed} (bake me)");
                    return;
                }
                if (computed != expected)
                {
                    Log.Error($"{PatchGuard.LogPrefix}{type.Name}.{method} changed shape upstream "
                              + $"(fingerprint {computed}, expected {expected}) — re-verify {protects}.");
                }
            }
            catch (Exception e)
            {
                Log.Warning($"{PatchGuard.LogPrefix}Fingerprint check for {type.Name}.{method} failed to run: " + e.Message);
            }
        }
    }
}

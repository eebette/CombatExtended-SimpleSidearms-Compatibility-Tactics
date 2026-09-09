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
    /// Shared math for target-aware ammo scoring and armor-aware melee: what
    /// fraction of a weapon's damage arrives against THIS target, under CE's
    /// armor mechanics.
    /// </summary>
    public static class TargetScoring
    {
        /// <summary>CE's private TryPenetrateArmor, invoked with armor:null.</summary>
        private delegate bool TryPenDel(DamageDef def, float armorAmount, ref float penAmount,
                                        ref float dmgAmount, Thing armor, float partDensity);

        private static readonly TryPenDel TryPen;

        /// <summary>vanilla ProjectileProperties.damageAmountBase (private) - CE's
        /// deflect conversion divides by it; null bind -> scale 1.</summary>
        private static readonly AccessTools.FieldRef<Verse.ProjectileProperties, int> DamageBase;

        static TargetScoring()
        {
            try
            {
                DamageBase = AccessTools.FieldRefAccess<Verse.ProjectileProperties, int>("damageAmountBase");
            }
            catch (Exception e)
            {
                DamageBase = null;
                Log.Warning(PatchGuard.LogPrefix + "Could not bind ProjectileProperties.damageAmountBase - "
                            + "deflect conversions score unscaled (exact for normal-quality primaries). " + e.Message);
            }
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
                Log.Error(PatchGuard.LogPrefix + "Could not bind CE's TryPenetrateArmor - target "
                          + "scoring falls back to its modeled arithmetic (re-verify against CE). " + e);
            }
            if (TryPen == null)
            {
                Log.Error(PatchGuard.LogPrefix + "CE's TryPenetrateArmor is not the expected shape - "
                          + "target scoring uses its modeled fallback arithmetic (re-verify against CE).");
            }
            UpstreamFingerprint.Verify(typeof(ArmorUtilityCE), "TryPenetrateArmor",
                UpstreamFingerprint.TryPenetrateArmorHash,
                "the armor-through arithmetic TargetScoring executes");
            UpstreamFingerprint.Verify(typeof(ArmorUtilityCE), "GetDeflectDamageInfo",
                UpstreamFingerprint.GetDeflectDamageInfoHash,
                "the deflect-to-blunt conversion TargetScoring models");
            UpstreamFingerprint.Verify(typeof(ArmorUtilityCE), "GetAfterArmorDamage",
                UpstreamFingerprint.GetAfterArmorDamageHash,
                "the composition TargetScoring mirrors (deflect re-run, partial-pen bonus hit)");
        }

        /// <summary>
        /// Fraction of the loaded projectile's damage that arrives through the target's armor.
        /// </summary>
        public static bool TryRangedMultiplier(ThingWithComps weapon, Pawn target, out float factor)
        {
            factor = 1f;
            if (target == null)
            {
                return false;
            }
            if (TryPen == null)
            {
                return false; // CE's penetration binding failed - degrade, don't score off a stale copy of its math
            }
            ThingDef projectile = CompatUtil.CurrentProjectile(weapon);
            var props = projectile?.projectile as ProjectilePropertiesCE;
            if (props == null)
            {
                return false; // not modelable: unpatched mod gun - do not pretend
            }
            float primaryDmg = props.GetDamageAmount(weapon);
            float sharpArmor = target.GetStatValue(StatDefOf.ArmorRating_Sharp);
            float bluntArmor = target.GetStatValue(StatDefOf.ArmorRating_Blunt);

            // CE's deflect conversions scale by amount/damageAmountBase for
            // projectile hits (GetDeflectDamageInfo).
            float damageBase = DamageBase != null ? DamageBase(props) : -1f;
            float ScaleFor(float actual) => damageBase > 0f ? actual / damageBase : 1f;

            float arrives = 0f;
            float total = 0f;
            if (props.damageDef?.harmsHealth ?? false)
            {
                arrives += DamageThrough(props.damageDef, primaryDmg,
                    props.armorPenetrationSharp, props.armorPenetrationBlunt, sharpArmor, bluntArmor,
                    ScaleFor(primaryDmg));
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
                    // explosive one amount×0.8, anything else zero.
                    float pen = SecondaryPen(sec, props);
                    float chance = Mathf.Clamp01(sec.chance);
                    float perHit = DamageThrough(sec.def, sec.amount, pen,
                        props.armorPenetrationBlunt, sharpArmor, bluntArmor, ScaleFor(sec.amount));
                    arrives += perHit * chance;
                    total += sec.amount * chance;
                }
            }
            if (total <= 0f)
            {
                return false;
            }
            factor = arrives / total;
            return true;
        }

        /// <summary>Convenience wrapper: 1 when unmodelable (forensics only -
        /// feature code must use the Try variant and honor modeled=false).</summary>
        public static float RangedMultiplier(ThingWithComps weapon, Pawn target)
        {
            return TryRangedMultiplier(weapon, target, out float f) ? f : 1f;
        }

        /// <summary>
        /// Fraction of this weapon's expected melee damage that arrives through the target's armor.
        /// </summary>
        public static bool TryMeleeTargetFactor(ThingWithComps weapon, Pawn target, out float factor)
        {
            factor = 1f;
            if (target == null || weapon?.def?.tools == null)
            {
                return false;
            }
            if (TryPen == null)
            {
                return false; // CE's penetration binding failed - degrade, don't score off a stale copy of its math
            }
            var tools = weapon.def.tools.OfType<ToolCE>().ToList();
            if (tools.Count == 0)
            {
                return false; // not modelable: vanilla-tool mod weapon - do not pretend
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
                    // Melee: GetDeflectDamageInfo has no amount scaling branch.
                    arrives += DamageThrough(def, perManeuver, penSharp, penBlunt, sharpArmor, bluntArmor, 1f);
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
        /// Expected damage arriving through armor for one damage packet.
        /// </summary>
        private static float DamageThrough(DamageDef def, float dmg, float penSharp, float penBlunt,
                                           float sharpArmor, float bluntArmor, float deflectScale)
        {
            // Callers gate on TryPen != null (unmodelable otherwise), so it is bound here.
            StatDef armorStat = def.armorCategory?.armorRatingStat;
            if (armorStat == StatDefOf.ArmorRating_Sharp)
            {
                float pen = penSharp;
                float through = dmg;
                if (TryPen(def, sharpArmor, ref pen, ref through, null, 0f))
                {
                    // Partial penetration: the LOST fraction of the penetration converts to blunt.
                    if (through < dmg && penSharp > 0f && dmg > 0f)
                    {
                        float lostFraction = (penSharp - pen) / penSharp * ((dmg - through) / dmg);
                        float penPartial = penBlunt * lostFraction;
                        if (penPartial > 0f)
                        {
                            float bonus = Mathf.Pow(penPartial * 10000f, 1f / 3f) / 10f * deflectScale;
                            TryPen(DamageDefOf.Blunt, bluntArmor, ref penPartial, ref bonus, null, 0f);
                            through += bonus;
                        }
                    }
                    return through;
                }
                // Full deflection: CE converts the hit to blunt.
                float pen2 = penBlunt;
                float deflected = Mathf.Pow(penBlunt * 10000f, 1f / 3f) / 10f * deflectScale;
                TryPen(DamageDefOf.Blunt, bluntArmor, ref pen2, ref deflected, null, 0f);
                return deflected;
            }
            if (armorStat == StatDefOf.ArmorRating_Blunt)
            {
                float pen = penBlunt;
                float through = dmg;
                TryPen(def, bluntArmor, ref pen, ref through, null, 0f);
                return through;
            }
            // No armor category CE models here (Heat and untyped): pass whole.
            return dmg;
        }
    }

    /// <summary>
    /// IL checksum guard for upstream methods whose ARITHMETIC this module executes
    /// or models. A changed checksum means the upstream body changed.
    /// </summary>
    internal static class UpstreamFingerprint
    {
        // Baked against CE 16.7.3.0 / SS v1.6 - re-harvest on upstream updates.
        internal const string TryPenetrateArmorHash = "7b66aeb80967e00d";
        internal const string GetDeflectDamageInfoHash = "cf4967c8bf864887";
        internal const string DoReloadCheckHash = "24ec22f8bfb61eb8";
        internal const string GetAfterArmorDamageHash = "d65d743e005da6c9";

        internal static void Verify(Type type, string method, string expected, string protects)
        {
            try
            {
                MethodBase mb = AccessTools.Method(type, method);
                if (mb == null)
                {
                    Log.Error($"{PatchGuard.LogPrefix}{type.Name}.{method} not found - {protects} "
                              + "cannot be verified against upstream.");
                    return;
                }
                ulong hash = 14695981039346656037UL; // FNV-1a
                foreach (var instruction in PatchProcessor.GetOriginalInstructions(mb))
                {
                    // Invariant formatting: float operands ToString by CurrentCulture.
                    string token = instruction.opcode.Name
                        + (instruction.operand is IFormattable formattable
                            ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
                            : instruction.operand?.ToString() ?? "");
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
                              + $"(fingerprint {computed}, expected {expected}) - re-verify {protects}.");
                }
            }
            catch (Exception e)
            {
                Log.Warning($"{PatchGuard.LogPrefix}Fingerprint check for {type.Name}.{method} failed to run: " + e.Message);
            }
        }
    }
}

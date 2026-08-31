using System.Linq;
using CESimpleSidearmsCompat;
using CombatExtended;
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
    /// The model is ArmorUtilityCE.TryPenetrateArmor in expectation form, not an
    /// invented curve (the first version's clamp(pen/armor) floors and EMP constants
    /// were fiction and are gone):
    ///
    ///   - Damage through armor is dmg × clamp01(1 − armor/pen); zero-pen damage
    ///     passes whole (CE's own penAmount==0 special case).
    ///   - An under-penetrating SHARP attack deflects entirely and CE converts it to
    ///     a BLUNT hit of cbrt(bluntPen × 10000)/10 damage, which then runs the same
    ///     formula against blunt armor (GetDeflectDamageInfo) — that conversion, not
    ///     a hand-tuned floor, is why blunt is the anti-armor choice.
    ///   - Damage that cannot harm health (EMP's stun, for one) is left out of both
    ///     sides of the ratio: a stun's utility has no derivable damage exchange
    ///     rate, so it is not scored — SS's own EMP mode filters keep governing
    ///     when EMP weapons are picked at all.
    ///
    /// Deliberately unmodeled, all second-order for a RANKING: the partial-pen
    /// deflect residue CE adds on top of penetrating sharp hits, per-part apparel
    /// layers (the pawn-level armor stat stands in), and Heat-category armor.
    /// </summary>
    public static class TargetScoring
    {
        /// <summary>
        /// Fraction of the loaded projectile's damage (primary + health-harming
        /// secondaries, e.g. an ion round's ballistic core) that arrives through the
        /// target's armor. 1 when there is nothing to model.
        /// </summary>
        public static float RangedMultiplier(ThingWithComps weapon, Pawn target)
        {
            if (target == null)
            {
                return 1f;
            }
            ThingDef projectile = CompatUtil.CurrentProjectile(weapon);
            var props = projectile?.projectile as ProjectilePropertiesCE;
            if (props == null)
            {
                return 1f;
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
            return total > 0f ? arrives / total : 1f;
        }

        /// <summary>
        /// Fraction of this weapon's expected melee damage that arrives through the
        /// target's armor: chance-weighted over its CE tools (the core patch's P12
        /// weighting), each tool judged by its own maneuvers' damage types, with the
        /// instance's CE MeleePenetrationFactor (material and quality) applied.
        /// Multiply against SS's own biased score — SS keeps the ranking, this adds
        /// only the target axis.
        /// </summary>
        public static float MeleeTargetFactor(ThingWithComps weapon, Pawn target)
        {
            if (target == null || weapon?.def?.tools == null)
            {
                return 1f;
            }
            var tools = weapon.def.tools.OfType<ToolCE>().ToList();
            if (tools.Count == 0)
            {
                return 1f;
            }
            float totalChance = tools.Sum(t => t.chanceFactor);
            if (totalChance <= 0f)
            {
                return 1f;
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
            return total > 0f ? arrives / total : 1f;
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

        /// <summary>Expected damage arriving through armor for one damage packet.</summary>
        private static float DamageThrough(DamageDef def, float dmg, float penSharp, float penBlunt,
                                           float sharpArmor, float bluntArmor)
        {
            StatDef armorStat = def.armorCategory?.armorRatingStat;
            if (armorStat == StatDefOf.ArmorRating_Sharp)
            {
                if (penSharp <= 0f)
                {
                    return dmg; // CE's penAmount==0 special case, before any deflection
                }
                if (penSharp > sharpArmor)
                {
                    return dmg * Passes(penSharp, sharpArmor);
                }
                // Full deflection: CE converts the hit to blunt at
                // cbrt(bluntPen × 10000)/10 damage, against blunt armor.
                float deflected = Mathf.Pow(penBlunt * 10000f, 1f / 3f) / 10f;
                return deflected * Passes(penBlunt, bluntArmor);
            }
            if (armorStat == StatDefOf.ArmorRating_Blunt)
            {
                return dmg * Passes(penBlunt, bluntArmor);
            }
            // No armor category CE models here (Heat and untyped): pass whole.
            return dmg;
        }

        private static float Passes(float pen, float armor)
        {
            if (pen <= 0f)
            {
                return 1f; // CE's penAmount==0 special case: untyped force passes whole
            }
            return Mathf.Clamp01(1f - armor / pen);
        }
    }
}

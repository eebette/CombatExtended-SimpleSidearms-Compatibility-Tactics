using System.Linq;
using CESimpleSidearmsCompat;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;

namespace CESSCompatTactics.Features
{
    /// <summary>
    /// Feature 5/6 shared math: how effective is this weapon's LOADED ammo (ranged)
    /// or its CE melee tools against THIS target. Pure scoring — never writes
    /// SelectedAmmo or any other state. Values are bounded multipliers so the
    /// primary DPS ranking stays in charge and a bad matchup can demote but never
    /// zero a weapon (floor keeps "shoot the wrong ammo" better than fists).
    /// </summary>
    public static class TargetScoring
    {
        // CE armor mechanics differ by damage type: an under-penetrating SHARP attack
        // is deflected almost entirely, while BLUNT transfers trauma through armor
        // regardless — which is exactly why blunt is the anti-armor melee choice.
        // The floors encode that asymmetry.
        private const float FloorSharp = 0.10f;
        private const float FloorBlunt = 0.40f;
        private const float EmpVsMechBoost = 2.5f;
        private const float EmpVsFleshPenalty = 0.2f;

        /// <summary>Multiplier for a ranged weapon's currently-loaded projectile vs the target.</summary>
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

            bool mech = target.RaceProps?.IsMechanoid ?? false;
            if (props.damageDef == DamageDefOf.EMP)
            {
                return mech ? EmpVsMechBoost : EmpVsFleshPenalty;
            }

            return PenFactor(props.armorPenetrationSharp,
                             target.GetStatValue(StatDefOf.ArmorRating_Sharp), FloorSharp);
        }

        /// <summary>Best CE melee-tool score vs the target: (power/cooldown) scaled by
        /// the tool's penetration against the matching armor type. Non-CE tools fall
        /// back to the caller's base score.</summary>
        public static float MeleeScore(ThingWithComps weapon, Pawn target, float fallback)
        {
            if (target == null || weapon?.def?.tools == null)
            {
                return fallback;
            }
            var tools = weapon.def.tools.OfType<ToolCE>().ToList();
            if (tools.Count == 0)
            {
                return fallback;
            }
            float armorSharp = target.GetStatValue(StatDefOf.ArmorRating_Sharp);
            float armorBlunt = target.GetStatValue(StatDefOf.ArmorRating_Blunt);
            float best = 0f;
            foreach (ToolCE tool in tools)
            {
                bool blunt = tool.capacities != null && tool.capacities.Count > 0
                             && tool.capacities.All(c => c.defName == "Blunt" || c.defName == "Poke");
                float pen = blunt ? tool.armorPenetrationBlunt : tool.armorPenetrationSharp;
                float armor = blunt ? armorBlunt : armorSharp;
                float raw = tool.power / Mathf.Max(tool.cooldownTime, 0.1f);
                float score = raw * PenFactor(pen, armor, blunt ? FloorBlunt : FloorSharp);
                if (score > best)
                {
                    best = score;
                }
            }
            return best > 0f ? best : fallback;
        }

        private static float PenFactor(float penetration, float armor, float floor)
        {
            if (armor <= 0.05f)
            {
                return 1f;
            }
            return Mathf.Clamp(penetration / armor, floor, 1f);
        }
    }
}

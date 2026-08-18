using HarmonyLib;
using UnityEngine;
using Verse;

namespace CESSCompatTactics
{
    public class TacticsSettings : ModSettings
    {
        // Everything OFF by default: this module is pure enhancement — new triggers
        // neither upstream mod ever had (module charter).
        public bool reloadAbort = false;
        public bool forcedDryFallthrough = false;
        public bool ammoDepthTiebreak = false;
        public int tiebreakEpsilonPct = 10;
        public bool targetAwareAmmoScoring = false;
        public bool armorAwareMelee = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref reloadAbort, "reloadAbort", false);
            Scribe_Values.Look(ref forcedDryFallthrough, "forcedDryFallthrough", false);
            Scribe_Values.Look(ref ammoDepthTiebreak, "ammoDepthTiebreak", false);
            Scribe_Values.Look(ref tiebreakEpsilonPct, "tiebreakEpsilonPct", 10);
            Scribe_Values.Look(ref targetAwareAmmoScoring, "targetAwareAmmoScoring", false);
            Scribe_Values.Look(ref armorAwareMelee, "armorAwareMelee", false);
        }
    }

    public class TacticsMod : Mod
    {
        public static TacticsSettings Settings { get; private set; }

        public TacticsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<TacticsSettings>();
        }

        public override string SettingsCategory()
        {
            return "CE+SS Tactics";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("Reload-abort when threatened", ref Settings.reloadAbort,
                "A pawn mid-reload with a hostile in effective range swaps to a loaded carried weapon instead of finishing the reload. Player-ordered reloads are never interrupted; the abandoned reload resumes via CE's normal idle reload behavior.");
            listing.CheckboxLabeled("Forced-weapon dry fall-through", ref Settings.forcedDryFallthrough,
                "A pawn forced onto a weapon with an empty magazine AND no compatible ammo carried temporarily uses normal weapon selection. The forced setting itself is never cleared — it resumes the moment ammo is available.");
            listing.CheckboxLabeled("Ammo-depth tiebreak", ref Settings.ammoDepthTiebreak,
                "When two carried guns rank within the margin below, prefer the one with deeper ammo reserves (magazine + carried spares).");
            listing.Label($"Tiebreak margin: {Settings.tiebreakEpsilonPct}% of the top score");
            Settings.tiebreakEpsilonPct = Mathf.RoundToInt(listing.Slider(Settings.tiebreakEpsilonPct, 0f, 30f));
            listing.CheckboxLabeled("Target-aware ammo scoring", ref Settings.targetAwareAmmoScoring,
                "When choosing which gun to draw against a target, weigh the CURRENTLY-LOADED ammo's effectiveness against that target (penetration vs armor, EMP vs mechs). Never switches or reloads ammo.");
            listing.CheckboxLabeled("Armor-aware melee choice", ref Settings.armorAwareMelee,
                "When drawing a melee weapon against a target, pick by CE melee-tool effectiveness against that target's armor (blunt vs armored, fast blades vs flesh).");
            listing.End();
        }
    }

    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            new Harmony("eebette.CESimpleSidearmsCompat.Tactics").PatchAll(typeof(Bootstrap).Assembly);
            Log.Message("[CE+SS Tactics] Patches installed.");
        }
    }
}

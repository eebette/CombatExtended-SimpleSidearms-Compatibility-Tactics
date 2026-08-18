using HarmonyLib;
using UnityEngine;
using Verse;

namespace CESSCompatTactics
{
    public class TacticsSettings : ModSettings
    {
        // Installing THIS mod is the opt-in (owner's call 2026-08-18): the headline
        // behaviors ship ON. The one exception is forced-dry fall-through — it
        // overrides explicit player intent (a forced weapon), a different consent
        // category, so it alone stays OFF by default.
        public bool reloadAbort = true;
        public bool forcedDryFallthrough = false;
        public bool ammoDepthTiebreak = true;
        public int tiebreakEpsilonPct = 10;
        public bool targetAwareAmmoScoring = true;
        public bool armorAwareMelee = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref reloadAbort, "reloadAbort", true);
            Scribe_Values.Look(ref forcedDryFallthrough, "forcedDryFallthrough", false);
            Scribe_Values.Look(ref ammoDepthTiebreak, "ammoDepthTiebreak", true);
            Scribe_Values.Look(ref tiebreakEpsilonPct, "tiebreakEpsilonPct", 10);
            Scribe_Values.Look(ref targetAwareAmmoScoring, "targetAwareAmmoScoring", true);
            Scribe_Values.Look(ref armorAwareMelee, "armorAwareMelee", true);
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

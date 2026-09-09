using HarmonyLib;
using UnityEngine;
using Verse;

namespace CESSCompatTactics
{
    public class TacticsSettings : ModSettings
    {
        // Forced-dry fall-through default: OFF since it
        // overrides explicit player intent (a forced weapon).
        public bool reloadAbort = true;
        public bool forcedDryFallthrough = false;
        public bool ammoDepthTiebreak = true;
        public int tiebreakEpsilonPct = 10;
        public bool targetAwareAmmoScoring = true;
        public bool armorAwareMelee = true;
        public bool draftedSidearmReload = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref reloadAbort, "reloadAbort", true);
            Scribe_Values.Look(ref forcedDryFallthrough, "forcedDryFallthrough", false);
            Scribe_Values.Look(ref ammoDepthTiebreak, "ammoDepthTiebreak", true);
            Scribe_Values.Look(ref tiebreakEpsilonPct, "tiebreakEpsilonPct", 10);
            Scribe_Values.Look(ref targetAwareAmmoScoring, "targetAwareAmmoScoring", true);
            Scribe_Values.Look(ref armorAwareMelee, "armorAwareMelee", true);
            Scribe_Values.Look(ref draftedSidearmReload, "draftedSidearmReload", true);
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
            listing.CheckboxLabeled("Allow weapon switch when threatened while reloading", ref Settings.reloadAbort,
                "A pawn mid-reload with a hostile in effective range swaps to a loaded carried weapon instead of finishing the reload. Player-ordered reloads are never interrupted.");
            listing.CheckboxLabeled("Allow weapon switch on forced weapons if no ammo available.", ref Settings.forcedDryFallthrough,
                "OFF (default): hold the forced weapon no matter what. ON: prefer it while usable, fall back to normal selection while it is out of ammo. The forced setting is never cleared and resumes the moment ammo is available.");
            listing.CheckboxLabeled("Enable tiebreaker for sidearm choice based on amount of ammo for each gun in inventory", ref Settings.ammoDepthTiebreak,
                "When two carried guns rank within the margin below, prefer the one with deeper ammo reserves (magazine + carried spares).");
            listing.Label($"Tiebreak margin: {Settings.tiebreakEpsilonPct}% of the top score");
            Settings.tiebreakEpsilonPct = Mathf.RoundToInt(listing.Slider(Settings.tiebreakEpsilonPct, 0f, 30f));
            listing.CheckboxLabeled("Enable target-aware ammo scoring", ref Settings.targetAwareAmmoScoring,
                "When choosing which gun to draw against a target, weigh the loaded ammo's effectiveness against the specific target.");
            listing.CheckboxLabeled("Enable armor-aware melee choice", ref Settings.armorAwareMelee,
                "When drawing a melee weapon against a target, pick by melee-tool effectiveness against that specific target's armor (blunt vs armored, fast blades vs flesh).");
            listing.CheckboxLabeled("Enable sidearm top-off while drafted", ref Settings.draftedSidearmReload,
                "Extends drafted lull-reload from the equipped weapon to carried sidearms: during a combat lull a drafted pawn also refills empty sidearm magazines.");
            listing.End();
        }
    }

    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        public const string HarmonyId = "eebette.CESimpleSidearmsCompat.Tactics";

        static Bootstrap()
        {
            // Per class, not PatchAll.
            var harmony = new Harmony(HarmonyId);
            int applied = 0;
            var failures = new System.Collections.Generic.List<string>();
            // Two of TargetScoring's upstream fingerprints live in its static ctor
            try
            {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                    typeof(Features.TargetScoring).TypeHandle);
            }
            catch (System.Exception e)
            {
                Log.Error($"{PatchGuard.LogPrefix}Target-scoring initialization failed at load: {e}");
            }
            foreach (System.Type type in typeof(Bootstrap).Assembly.GetTypes())
            {
                try
                {
                    if (type.GetCustomAttributes(typeof(HarmonyPatch), inherit: false).Length == 0)
                    {
                        continue;
                    }
                    // Patch() returns the patched methods.
                    var patched = harmony.CreateClassProcessor(type).Patch();
                    if (patched != null && patched.Count > 0)
                    {
                        applied++;
                    }
                }
                catch (System.Exception e)
                {
                    failures.Add(type.Name);
                    Log.Error($"{PatchGuard.LogPrefix}Patch class {type.Name} could not be applied - "
                              + $"that one feature is inactive, the others still work. {e}");
                }
            }
            if (failures.Count > 0)
            {
                Log.Warning($"{PatchGuard.LogPrefix}Installed {applied} patch class(es); "
                            + $"{failures.Count} failed ({string.Join(", ", failures)}).");
            }
            else
            {
                Log.Message($"{PatchGuard.LogPrefix}Patches installed ({applied} patch classes).");
            }
        }
    }
}

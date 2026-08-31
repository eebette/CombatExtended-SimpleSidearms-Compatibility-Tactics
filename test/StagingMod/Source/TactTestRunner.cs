using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CESSCompatTactics;
using CombatExtended;
using HarmonyLib;
using PeteTimesSix.SimpleSidearms;
using PeteTimesSix.SimpleSidearms.Utilities;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;
using Verse.AI;

namespace CESSTacticsTestStaging
{
    /// <summary>
    /// Acceptance harness for the TACT saves. Launch with:
    ///   -celoadsave=TACT-1-reload-abort -ceassert=tact1
    ///   -celoadsave=TACT-2-forced-dry   -ceassert=tact2
    ///   -celoadsave=TACT-3-tiebreak     -ceassert=tact3
    /// Owns the "tact" scenario prefix (siblings own "cetest"/"supply").
    /// Every scenario starts with the feature OFF (negative control proving
    /// default-off inertness) before enabling it.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class TactTestBoot
    {
        static TactTestBoot()
        {
            Log.Message("[TactStaging] assembly loaded.");
            if (!GenCommandLine.TryGetCommandLineArg("ceassert", out string scenario)
                || scenario.NullOrEmpty() || !scenario.StartsWith("tact"))
            {
                return;
            }
            if (GenCommandLine.TryGetCommandLineArg("celoadsave", out string save) && !save.NullOrEmpty())
            {
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    Log.Message($"[TactTest] Auto-loading save '{save}'.");
                    GameDataSaveLoader.LoadGame(save);
                });
            }
        }
    }

    public class TactTestRunnerComponent : GameComponent
    {
        private class Check
        {
            public string name;
            public Func<(bool pass, string detail)> eval;
            public bool informational; // recorded, never fails the run
            // Must-not-happen: re-evaluated on every poll instead of latching, and a
            // failure fails the phase immediately.
            public bool negative;
            // Must be TRUE before the real checks mean anything; a phase whose
            // precondition never holds reports INVALID (a broken test), not FAIL.
            public bool precondition;
            public bool passed;
            public string lastDetail = "not evaluated";
        }

        private class Phase
        {
            public string label;
            // Establishes everything the phase depends on; runs once, before mutate.
            public Action arrange;
            public Action mutate;
            public List<Check> checks = new List<Check>();
            public int deadlineTicks;
            public int minTicks;
            // Runs on every poll after the act, for phases that drive rather than wait.
            public Action poll;
            public bool failed;
            public bool invalid;
            // mutate is deferred until every precondition holds.
            public bool mutated;
            public string diagnostic;
            // Diagnostics THIS phase deliberately provokes; scoped per phase so the same
            // text anywhere else stays a finding.
            public string[] expectedDiagnostics;
        }

        /// <summary>
        /// Diagnostics accounted for and decided not ours; anything else — any Error
        /// from any mod, any Warning not listed — fails the phase it appeared in.
        /// </summary>
        private static readonly string[] ExpectedDiagnostics =
        {
            "had a null weapon memory, removing",
            "had a missing def or malformed data, removing",
            "[TactTest] Phase ",
            "[TactTest] poll for ",
            "[TactTest] Mutation for phase ",
            "[TactTest] Setup for phase ",
            "[TactTest] Isolated run",
            "[TactTest] Results written",
            "[TactTest] Scenario complete",
            "[TactTest] Loadouts module",
            "[TactStaging]",
            "[RimBridge] STARTUP_TIMING",
        };

        // text -> repeats accounted for (RimWorld merges identical texts into one
        // message with a growing counter; a grown count is a NEW occurrence).
        private readonly Dictionary<string, int> seenDiagnostics = new Dictionary<string, int>();

        private void BaselineDiagnostics()
        {
            foreach (LogMessage msg in Log.Messages)
            {
                seenDiagnostics[msg.text ?? ""] = msg.repeats;
            }
            Log.Message($"[TactTest] Diagnostics baselined at {seenDiagnostics.Count} pre-existing message(s).");
        }

        /// <summary>The baseline hides this mod's own load-time failures; one sweep for
        /// our prefixes closes that hole.</summary>
        private static string StartupDiagnostic()
        {
            foreach (LogMessage msg in Log.Messages)
            {
                if (msg.type != LogMessageType.Error && msg.type != LogMessageType.Warning)
                {
                    continue;
                }
                string text = msg.text ?? "";
                if (text.Contains("[CE+SS Tactics]")
                    || text.Contains("[CE+SimpleSidearms]")
                    || text.Contains("Loadouts module is ACTIVE")
                    || text.Contains("scenarios are contaminated"))
                {
                    return text.Split('\n')[0];
                }
            }
            return null;
        }

        private string NewDiagnostic(Phase phase)
        {
            foreach (LogMessage msg in Log.Messages)
            {
                if (msg.type != LogMessageType.Error && msg.type != LogMessageType.Warning)
                {
                    continue;
                }
                string text = msg.text ?? "";
                if (seenDiagnostics.TryGetValue(text, out int accounted) && msg.repeats <= accounted)
                {
                    continue;
                }
                seenDiagnostics[text] = msg.repeats;
                if (ExpectedDiagnostics.Any(e => text.Contains(e)))
                {
                    continue;
                }
                if (phase?.expectedDiagnostics != null
                    && phase.expectedDiagnostics.Any(e => text.Contains(e)))
                {
                    continue;
                }
                return $"{msg.type}: {text.Split('\n')[0]}";
            }
            return null;
        }

        private List<Phase> phases;
        private int isolatedPhase = -1;
        private int totalPhaseCount;
        private int phaseIndex = -1;
        private int phaseStartTick;
        private string scenario;
        private bool active;
        private bool done;

        public TactTestRunnerComponent(Game game)
        {
        }

        public override void LoadedGame()
        {
            if (!GenCommandLine.TryGetCommandLineArg("ceassert", out scenario)
                || scenario.NullOrEmpty() || !scenario.StartsWith("tact"))
            {
                return;
            }
            // "tact1:2" runs phase 2 alone in its own process against a fresh save.
            int colon = scenario.IndexOf(':');
            if (colon > 0 && int.TryParse(scenario.Substring(colon + 1), out int only))
            {
                isolatedPhase = only;
                scenario = scenario.Substring(0, colon);
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    DisableLoadoutsModule();
                    ResetTacticsSettings();
                    phases = BuildScenario(scenario);
                    phases.Insert(0, PatchInventoryPhase());
                    totalPhaseCount = phases.Count;
                    if (isolatedPhase >= 0)
                    {
                        phases = isolatedPhase < totalPhaseCount
                            ? new List<Phase> { phases[isolatedPhase] }
                            : new List<Phase>();
                        Log.Message($"[TactTest] Isolated run: phase {isolatedPhase} of {totalPhaseCount}"
                                    + (phases.Count == 0 ? " — out of range." : $" ('{phases[0].label}')."));
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[TactTest] Scenario build failed: " + e);
                    WriteResults(crashed: e.ToString());
                    Root.Shutdown();
                    return;
                }
                BaselineDiagnostics();
                active = true;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
                Log.Message($"[TactTest] Scenario '{scenario}' started, {phases.Count} phases.");
                AdvancePhase();
            });
        }

        /// <summary>
        /// Phase 0 of every scenario: reflection patch census + startup-error sweep. A
        /// Prepare that quietly skipped, a Bootstrap per-class failure, or a load-time
        /// error shows up before any behavioral phase runs half-patched.
        /// </summary>
        private static Phase PatchInventoryPhase()
        {
            return new Phase
            {
                label = "patch-inventory",
                deadlineTicks = 1200,
                checks =
                {
                    C("no-startup-errors-from-this-mod", () =>
                    {
                        string bad = StartupDiagnostic();
                        return (bad == null, bad ?? "startup log clean");
                    }),
                    C("all-tactics-patches-applied", () =>
                    {
                        var mine = Harmony.GetAllPatchedMethods()
                            .Where(m =>
                            {
                                var info = Harmony.GetPatchInfo(m);
                                return info != null && info.Owners.Contains("eebette.CESimpleSidearmsCompat.Tactics");
                            })
                            .ToList();
                        // 4 distinct methods today: equipBestWeaponFromInventoryByPreference
                        // (forced-dry), SetWeaponAsForced (lesson note), findBestRangedWeapon
                        // (tiebreak + target-aware), findBestMeleeWeapon (armor-aware).
                        return (mine.Count >= 4,
                            $"methods patched by eebette.CESimpleSidearmsCompat.Tactics={mine.Count} (want >= 4): "
                            + string.Join(", ", mine.Select(m => m.DeclaringType?.Name + "." + m.Name).OrderBy(n => n)));
                    }),
                }
            };
        }

        private static void ResetTacticsSettings()
        {
            TacticsMod.Settings.reloadAbort = false;
            TacticsMod.Settings.forcedDryFallthrough = false;
            TacticsMod.Settings.ammoDepthTiebreak = false;
            TacticsMod.Settings.tiebreakEpsilonPct = 10;
            TacticsMod.Settings.targetAwareAmmoScoring = false;
            TacticsMod.Settings.armorAwareMelee = false;
        }

        private static void DisableLoadoutsModule()
        {
            try
            {
                bool loadoutsActive = ModsConfig.IsActive("eebette.CESimpleSidearmsCompat.Loadouts")
                    || Harmony.GetAllPatchedMethods().Any(m =>
                        Harmony.GetPatchInfo(m)?.Owners
                            .Any(o => o.Contains("CESimpleSidearmsCompat.Loadouts")) ?? false);
                Type mod = GenTypes.GetTypeInAnyAssembly("CESimpleSidearmsCompat.Loadouts.LoadoutsMod");
                object settings = mod?.GetProperty("Settings")?.GetValue(null);
                if (settings == null)
                {
                    if (loadoutsActive)
                    {
                        // Silent return here means every TACT scenario runs with the
                        // Loadouts projections active — fail loud so a rename breaks the
                        // suite, not the results. (The pre-rename reflection did exactly
                        // that silently until 2026-08-31.)
                        Log.Error("[TactTest] Loadouts module is ACTIVE but its settings type was not "
                                  + "found (renamed again?) — scenarios are contaminated by its patches.");
                    }
                    return;
                }
                System.Reflection.FieldInfo field = settings.GetType().GetField("loadoutWeaponsAsSidearms");
                if (field == null)
                {
                    Log.Error("[TactTest] Loadouts settings found but 'loadoutWeaponsAsSidearms' is gone — "
                              + "cannot switch the module off; scenarios are contaminated by its patches.");
                    return;
                }
                field.SetValue(settings, false);
                Log.Message("[TactTest] Loadouts module switched off (in-memory) for this run.");
            }
            catch (Exception e)
            {
                Log.Error("[TactTest] Could not disable Loadouts module — scenarios may be contaminated: " + e.Message);
            }
        }

        public override void GameComponentTick()
        {
            if (!active || done)
            {
                return;
            }
            if (Find.TickManager.Paused || Find.TickManager.CurTimeSpeed != TimeSpeed.Superfast)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            }
            int tick = Find.TickManager.TicksGame;
            if (tick % 30 != 0)
            {
                return;
            }

            if (phases.Count == 0)
            {
                Finish();
                return;
            }
            Phase phase = phases[phaseIndex];

            string diagnostic = NewDiagnostic(phase);
            if (diagnostic != null)
            {
                phase.failed = true;
                phase.diagnostic = diagnostic;
                Log.Warning($"[TactTest] Phase '{phase.label}' FAILED on an unexpected diagnostic: {diagnostic}");
                AdvancePhase();
                return;
            }

            if (phase.mutated)
            {
                try
                {
                    phase.poll?.Invoke();
                }
                catch (Exception e)
                {
                    Log.Error($"[TactTest] poll for '{phase.label}' threw: " + e);
                    phase.failed = true;
                    AdvancePhase();
                    return;
                }
            }

            bool allPass = true;
            bool preconditionsHold = true;
            Check tripped = null;
            foreach (Check check in phase.checks)
            {
                if (!phase.mutated && !check.precondition)
                {
                    continue;
                }
                if (check.passed && !check.informational && !check.negative)
                {
                    continue;
                }
                try
                {
                    (bool pass, string detail) = check.eval();
                    check.lastDetail = detail;
                    check.passed = pass || check.informational;
                    if (!pass && !check.informational)
                    {
                        allPass = false;
                        if (check.precondition)
                        {
                            preconditionsHold = false;
                        }
                        else if (check.negative)
                        {
                            tripped = check;
                        }
                    }
                }
                catch (Exception e)
                {
                    // Full ToString, first-wins: RimWorld dedups repeated stacktraces
                    // into markers, and re-evaluations would overwrite the only copy.
                    if (!(check.lastDetail?.StartsWith("EXCEPTION") ?? false))
                    {
                        check.lastDetail = "EXCEPTION: " + e;
                    }
                    if (!check.informational)
                    {
                        allPass = false;
                        if (check.precondition)
                        {
                            preconditionsHold = false;
                        }
                    }
                }
            }

            if (preconditionsHold && !phase.mutated)
            {
                phase.mutated = true;
                phaseStartTick = tick;
                try
                {
                    phase.mutate?.Invoke();
                }
                catch (Exception e)
                {
                    Log.Error($"[TactTest] Mutation for phase '{phase.label}' threw: " + e);
                    phase.failed = true;
                    AdvancePhase();
                }
                return;
            }
            if (!phase.mutated)
            {
                if (tick - phaseStartTick > phase.deadlineTicks)
                {
                    phase.invalid = true;
                    Log.Warning($"[TactTest] Phase '{phase.label}' INVALID — preconditions never held: "
                                + string.Join(", ", phase.checks.Where(c => c.precondition && !c.passed)
                                                         .Select(c => $"{c.name} ({c.lastDetail})")));
                    AdvancePhase();
                }
                return;
            }

            if (tripped != null && preconditionsHold)
            {
                phase.failed = true;
                Log.Warning($"[TactTest] Phase '{phase.label}' FAILED: '{tripped.name}' must not happen "
                            + $"but did at tick {tick} — {tripped.lastDetail}");
                AdvancePhase();
                return;
            }
            if (tick - phaseStartTick < phase.minTicks)
            {
                return;
            }
            if (allPass)
            {
                Log.Message($"[TactTest] Phase '{phase.label}' PASSED at tick {tick}.");
                AdvancePhase();
            }
            else if (tick - phaseStartTick > phase.deadlineTicks)
            {
                phase.invalid = !preconditionsHold;
                phase.failed = !phase.invalid;
                string why = phase.invalid
                    ? "INVALID — preconditions never held: "
                      + string.Join(", ", phase.checks.Where(c => c.precondition && !c.passed)
                                               .Select(c => $"{c.name} ({c.lastDetail})"))
                    : $"FAILED (deadline {phase.deadlineTicks} ticks).";
                Log.Warning($"[TactTest] Phase '{phase.label}' {why}");
                AdvancePhase();
            }
        }

        private void AdvancePhase()
        {
            phaseIndex++;
            if (phaseIndex >= phases.Count)
            {
                Finish();
                return;
            }
            Phase phase = phases[phaseIndex];
            phaseStartTick = Find.TickManager.TicksGame;
            try
            {
                phase.arrange?.Invoke();
                if (!phase.checks.Any(c => c.precondition))
                {
                    phase.mutate?.Invoke();
                    phase.mutated = true;
                }
            }
            catch (Exception e)
            {
                Log.Error($"[TactTest] Setup for phase '{phase.label}' threw: " + e);
                phase.failed = true;
                foreach (Check c in phase.checks)
                {
                    c.lastDetail = "mutation threw: " + e.Message;
                }
                AdvancePhase();
            }
        }

        private void Finish()
        {
            done = true;
            WriteResults();
            Log.Message("[TactTest] Scenario complete; shutting down.");
            Root.Shutdown();
        }

        private void WriteResults(string crashed = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"scenario\": \"{scenario}\",\n");
            sb.Append($"  \"phaseCount\": {totalPhaseCount},\n");
            if (isolatedPhase >= 0)
            {
                sb.Append($"  \"isolatedPhase\": {isolatedPhase},\n");
            }
            bool overall = crashed == null && phases != null && phases.All(p => !p.failed && !p.invalid);
            sb.Append($"  \"passed\": {(overall ? "true" : "false")},\n");
            if (crashed != null)
            {
                sb.Append($"  \"crashed\": \"{Escape(crashed)}\",\n");
            }
            sb.Append($"  \"ticks\": {(Find.TickManager?.TicksGame ?? 0)},\n");
            sb.Append("  \"phases\": [\n");
            if (phases != null)
            {
                for (int i = 0; i < phases.Count; i++)
                {
                    Phase p = phases[i];
                    sb.Append("    {\n");
                    sb.Append($"      \"label\": \"{Escape(p.label)}\",\n");
                    sb.Append($"      \"passed\": {((!p.failed && !p.invalid) ? "true" : "false")},\n");
                    sb.Append($"      \"invalid\": {(p.invalid ? "true" : "false")},\n");
                    if (p.diagnostic != null)
                    {
                        sb.Append($"      \"diagnostic\": \"{Escape(p.diagnostic)}\",\n");
                    }
                    sb.Append($"      \"reached\": {(i <= phaseIndex ? "true" : "false")},\n");
                    sb.Append("      \"checks\": [\n");
                    for (int j = 0; j < p.checks.Count; j++)
                    {
                        Check c = p.checks[j];
                        sb.Append("        {");
                        sb.Append($"\"name\": \"{Escape(c.name)}\", ");
                        sb.Append($"\"passed\": {(c.passed ? "true" : "false")}, ");
                        sb.Append($"\"informational\": {(c.informational ? "true" : "false")}, ");
                        sb.Append($"\"precondition\": {(c.precondition ? "true" : "false")}, ");
                        sb.Append($"\"detail\": \"{Escape(c.lastDetail)}\"");
                        sb.Append("}");
                        sb.Append(j < p.checks.Count - 1 ? ",\n" : "\n");
                    }
                    sb.Append("      ]\n");
                    sb.Append(i < phases.Count - 1 ? "    },\n" : "    }\n");
                }
            }
            sb.Append("  ]\n}\n");
            string suffix = isolatedPhase >= 0 ? $"-iso-{isolatedPhase:D2}" : "";
            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, $"test-results-{scenario}{suffix}.json");
            File.WriteAllText(path, sb.ToString());
            Log.Message($"[TactTest] Results written to {path}");
        }

        private static string Escape(string s)
        {
            return s == null ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        }

        // ---- helpers ------------------------------------------------------

        private static Pawn Colonist(string nick)
        {
            Pawn pawn = Find.CurrentMap.mapPawns.FreeColonistsSpawned
                .FirstOrDefault(p => p.Name is NameTriple nt && nt.Nick == nick);
            if (pawn == null)
            {
                throw new InvalidOperationException("Colonist not found: " + nick);
            }
            return pawn;
        }

        private static ThingDef D(string defName) => DefDatabase<ThingDef>.GetNamed(defName);

        private static ThingWithComps Carried(Pawn pawn, ThingDef def)
        {
            if (pawn.equipment?.Primary?.def == def)
            {
                return pawn.equipment.Primary;
            }
            return pawn.inventory.innerContainer.OfType<ThingWithComps>().FirstOrDefault(t => t.def == def);
        }

        private static Check C(string name, Func<(bool, string)> eval, bool informational = false)
        {
            return new Check { name = name, eval = eval, informational = informational };
        }

        /// <summary>A must-not-happen check, held across the whole phase.</summary>
        private static Check N(string name, Func<(bool, string)> eval)
        {
            return new Check { name = name, eval = eval, negative = true };
        }

        /// <summary>Must be true for the phase to mean anything; never holding reports INVALID.</summary>
        private static Check P(string name, Func<(bool, string)> eval)
        {
            return new Check { name = name, eval = eval, precondition = true };
        }

        private static Pawn Raider()
        {
            // Humans only — the mech scenarios have their own Mech() finder, and the
            // flesh-target phases must never accidentally grab the centipede.
            return Find.CurrentMap.mapPawns.AllPawnsSpawned
                .FirstOrDefault(p => p.HostileTo(Faction.OfPlayer) && !p.Dead && !p.Downed
                                     && !(p.RaceProps?.IsMechanoid ?? false));
        }

        private static void ParkRaiderAt(Pawn colonist, int distance)
        {
            Pawn raider = Raider() ?? SpawnThreat(colonist.Map);
            IntVec3 cell = colonist.Position + new IntVec3(distance, 0, 0);
            cell = cell.ClampInsideMap(colonist.Map);
            if (!cell.Standable(colonist.Map))
            {
                CellFinder.TryFindRandomCellNear(cell, colonist.Map, 8, c => c.Standable(colonist.Map), out cell);
            }
            raider.Position = cell;
            raider.Notify_Teleported();
        }

        /// <summary>Disarmed hostile — a valid AttackTargetFinder threat that can't
        /// meaningfully hurt or be quickly killed across phases.</summary>
        private static Pawn SpawnThreat(Map map)
        {
            Faction pirates = Find.FactionManager.FirstFactionOfDef(FactionDefOf.Pirate);
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Pirate_Gunner")
                               ?? PawnKindDefOf.Drifter;
            var request = new PawnGenerationRequest(kind, pirates, PawnGenerationContext.NonPlayer,
                          forceGenerateNewPawn: true, canGeneratePawnRelations: false);
            Pawn raider = PawnGenerator.GeneratePawn(request);
            raider.equipment?.DestroyAllEquipment();
            GenSpawn.Spawn(raider, map.Center, map);
            Verse.AI.Group.LordMaker.MakeNewLord(pirates,
                new RimWorld.LordJob_AssaultColony(pirates, canKidnap: false, canTimeoutOrFlee: false,
                    sappers: false, useAvoidGridSmart: false, canSteal: false), map,
                new System.Collections.Generic.List<Pawn> { raider });
            return raider;
        }

        private static void StartReload(Pawn pawn, bool playerForced)
        {
            ThingWithComps primary = pawn.equipment.Primary;
            CompAmmoUser user = primary.TryGetComp<CompAmmoUser>();
            user.CurMagCount = 0;
            Job job = user.TryMakeReloadJob();
            if (job == null)
            {
                throw new InvalidOperationException("TryMakeReloadJob returned null");
            }
            job.playerForced = playerForced;
            pawn.jobs.StartJob(job, JobCondition.InterruptForced);
        }

        private List<Phase> BuildScenario(string name)
        {
            switch (name)
            {
                case "tact1": return BuildTact1();
                case "tact2": return BuildTact2();
                case "tact3": return BuildTact3();
                case "tact4": return BuildTact4();
                case "tact5": return BuildTact5();
                default: throw new InvalidOperationException("Unknown scenario: " + name);
            }
        }

        private static Pawn Mech()
        {
            return Find.CurrentMap.mapPawns.AllPawnsSpawned
                .FirstOrDefault(p => (p.RaceProps?.IsMechanoid ?? false) && p.HostileTo(Faction.OfPlayer) && !p.Dead);
        }

        private static void ParkPawnNear(Pawn anchor, Pawn parked, int distance)
        {
            IntVec3 cell = anchor.Position + new IntVec3(distance, 0, 0);
            cell = cell.ClampInsideMap(anchor.Map);
            if (!cell.Standable(anchor.Map))
            {
                CellFinder.TryFindRandomCellNear(cell, anchor.Map, 8, c => c.Standable(anchor.Map), out cell);
            }
            parked.Position = cell;
            parked.Notify_Teleported();
        }

        // -- TACT-4: target-aware loaded-ammo scoring -----------------------

        private List<Phase> BuildTact4()
        {
            Pawn ammy = Colonist("Ammy");
            ThingDef rifle = D("Gun_AssaultRifle");
            ThingDef shotgun = D("Gun_PumpShotgun");

            (ThingWithComps weapon, float dps, float averageSpeed) FindBest(Pawn target) =>
                GettersFilters.findBestRangedWeapon(ammy, new LocalTargetInfo(target));

            string Detail(Pawn target)
            {
                ThingWithComps r = Carried(ammy, rifle);
                ThingWithComps g = Carried(ammy, shotgun);
                float mr = CESSCompatTactics.Features.TargetScoring.RangedMultiplier(r, target);
                float mg = CESSCompatTactics.Features.TargetScoring.RangedMultiplier(g, target);
                return $"mult rifle={mr:F2} shotgun={mg:F2} armor={target.GetStatValue(StatDefOf.ArmorRating_Sharp):F1}";
            }

            return new List<Phase>
            {
                new Phase
                {
                    label = "off-close-range-pick",
                    deadlineTicks = 3000,
                    mutate = () =>
                    {
                        Pawn mech = Mech() ?? throw new InvalidOperationException("Mech missing");
                        ParkPawnNear(ammy, mech, 8);
                    },
                    checks =
                    {
                        C("off-pick-recorded", () =>
                        {
                            var (weapon, dps, _) = FindBest(Mech());
                            return (true, $"OFF pick={weapon?.def?.defName} dps={dps:F2}; {Detail(Mech())}");
                        }, informational: true),
                    }
                },
                new Phase
                {
                    label = "on-armor-flips-to-penetrator",
                    deadlineTicks = 3000,
                    mutate = () => { TacticsMod.Settings.targetAwareAmmoScoring = true; },
                    checks =
                    {
                        C("winner-is-rifle-vs-armored-mech", () =>
                        {
                            var (weapon, dps, _) = FindBest(Mech());
                            return (weapon?.def == rifle, $"ON pick={weapon?.def?.defName} adj={dps:F2}; {Detail(Mech())}");
                        }),
                    }
                },
            };
        }

        // -- TACT-5: armor-aware melee choice -------------------------------

        private List<Phase> BuildTact5()
        {
            Pawn marcy = Colonist("Marcy");
            ThingDef knife = D("MeleeWeapon_Knife");
            ThingDef mace = D("MeleeWeapon_Mace");
            Pawn fleshTarget = null;

            ThingWithComps FindMelee(Pawn target)
            {
                GettersFilters.findBestMeleeWeapon(marcy, out ThingWithComps result,
                    includeEquipped: true, includeRangedWithBash: true, target: target);
                return result;
            }

            return new List<Phase>
            {
                new Phase
                {
                    label = "off-pick-recorded",
                    deadlineTicks = 3000,
                    checks =
                    {
                        C("off-vs-mech", () =>
                        {
                            ThingWithComps w = FindMelee(Mech());
                            return (true, $"OFF pick vs mech={w?.def?.defName ?? "fists"}");
                        }, informational: true),
                    }
                },
                new Phase
                {
                    label = "on-vs-flesh-picks-blade",
                    deadlineTicks = 3000,
                    mutate = () =>
                    {
                        TacticsMod.Settings.armorAwareMelee = true;
                        fleshTarget = Raider();
                        if (fleshTarget == null)
                        {
                            IntVec3 c = marcy.Position;
                            Faction pirates = Find.FactionManager.FirstFactionOfDef(FactionDefOf.Pirate);
                            var request = new PawnGenerationRequest(
                                DefDatabase<PawnKindDef>.GetNamedSilentFail("Pirate_Gunner") ?? PawnKindDefOf.Drifter,
                                pirates, PawnGenerationContext.NonPlayer,
                                forceGenerateNewPawn: true, canGeneratePawnRelations: false);
                            fleshTarget = PawnGenerator.GeneratePawn(request);
                            GenSpawn.Spawn(fleshTarget, marcy.Map.Center, marcy.Map);
                        }
                        fleshTarget.equipment?.DestroyAllEquipment();
                        fleshTarget.apparel?.DestroyAll();
                        ParkPawnNear(marcy, fleshTarget, 30);
                    },
                    checks =
                    {
                        C("blade-vs-flesh", () =>
                        {
                            ThingWithComps w = FindMelee(fleshTarget);
                            return (w?.def == knife, $"pick vs flesh={w?.def?.defName ?? "fists"} armor={fleshTarget.GetStatValue(StatDefOf.ArmorRating_Sharp):F2}");
                        }),
                    }
                },
                new Phase
                {
                    label = "on-vs-armor-picks-blunt",
                    deadlineTicks = 3000,
                    // Isolated runs never see phase 2's enable — and with the core
                    // patch's P12 giving SS's raw melee score real CE penetration, a
                    // feature-off pick can land on the mace by itself. The enable keeps
                    // this phase pinned to F06's scoring, not P12's.
                    arrange = () => { TacticsMod.Settings.armorAwareMelee = true; },
                    checks =
                    {
                        C("blunt-vs-armored-mech", () =>
                        {
                            Pawn mech = Mech();
                            ThingWithComps w = FindMelee(mech);
                            return (w?.def == mace, $"pick vs mech={w?.def?.defName ?? "fists"} sharpArmor={mech.GetStatValue(StatDefOf.ArmorRating_Sharp):F1} bluntArmor={mech.GetStatValue(StatDefOf.ArmorRating_Blunt):F1}");
                        }),
                        C("tool-forensics", () =>
                        {
                            Pawn mech = Mech();
                            var sb = new System.Text.StringBuilder();
                            foreach (ThingDef def in new[] { knife, mace })
                            {
                                ThingWithComps inst = Carried(marcy, def);
                                float score = CESSCompatTactics.Features.TargetScoring.MeleeScore(inst, mech, -1f);
                                sb.Append($"{def.defName}: score={score:F2} tools=[");
                                foreach (Verse.Tool t in def.tools)
                                {
                                    var tce = t as CombatExtended.ToolCE;
                                    string caps = string.Join("+", t.capacities.Select(c => c.defName));
                                    sb.Append(tce != null
                                        ? $"({caps} pow={t.power:F1} cd={t.cooldownTime:F2} penS={tce.armorPenetrationSharp:F2} penB={tce.armorPenetrationBlunt:F2})"
                                        : $"({caps} VANILLA pow={t.power:F1})");
                                }
                                sb.Append("] ");
                            }
                            return (true, sb.ToString());
                        }, informational: true),
                    }
                },
            };
        }

        // -- TACT-1: reload-abort when threatened ---------------------------

        private List<Phase> BuildTact1()
        {
            Pawn abort = Colonist("Abort");
            ThingDef rifle = D("Gun_AssaultRifle");
            ThingDef pistol = D("Gun_Autopistol");

            return new List<Phase>
            {
                new Phase
                {
                    label = "default-off-reload-completes",
                    deadlineTicks = 8000,
                    mutate = () =>
                    {
                        ParkRaiderAt(abort, 40);
                        StartReload(abort, playerForced: false);
                    },
                    checks =
                    {
                        C("reload-finished-no-abort", () =>
                        {
                            ThingWithComps primary = abort.equipment?.Primary;
                            CompAmmoUser user = primary?.TryGetComp<CompAmmoUser>();
                            bool full = primary?.def == rifle && user != null && user.CurMagCount == user.MagSize;
                            return (full, $"primary={primary?.def?.defName} mag={user?.CurMagCount}/{user?.MagSize} (feature OFF must be inert)");
                        }),
                    }
                },
                new Phase
                {
                    label = "abort-swaps-to-loaded-pistol",
                    deadlineTicks = 4000,
                    mutate = () =>
                    {
                        TacticsMod.Settings.reloadAbort = true;
                        // Inside the autopistol's CE range (16), NOT the historical 40:
                        // the feature scores the swap candidate at the threat's actual
                        // distance, and with the core patch's corrected range gate an
                        // out-of-range pistol scores zero — no viable swap, reload
                        // correctly finishes. The old 40-tile park only ever "worked"
                        // through SS's squared-distance bug. A threat the pistol cannot
                        // reach is phase 1's territory (finish the reload); THIS phase
                        // stages the swap being the right call.
                        ParkRaiderAt(abort, 12);
                        StartReload(abort, playerForced: false);
                    },
                    checks =
                    {
                        C("primary-becomes-pistol", () =>
                        {
                            ThingDef primary = abort.equipment?.Primary?.def;
                            return (primary == pistol, $"primary={primary?.defName ?? "none"} job={abort.CurJobDef?.defName}");
                        }),
                    }
                },
                new Phase
                {
                    label = "player-forced-reload-untouchable",
                    deadlineTicks = 10000,
                    // The feature must be ON for this phase to test anything: isolated
                    // runs never see phase 2's enable, and with it off the reload
                    // completes for the wrong reason.
                    arrange = () => { TacticsMod.Settings.reloadAbort = true; },
                    mutate = () =>
                    {
                        // Back onto the rifle, then a PLAYER-FORCED reload with the
                        // threat still present — must complete despite the feature.
                        ThingWithComps rifleThing = Carried(abort, rifle);
                        abort.TryGetComp<CompInventory>().TrySwitchToWeapon(rifleThing);
                        // Close park on purpose: a loaded pistol in range means the
                        // unforced path WOULD abort-and-swap here, so the playerForced
                        // gate is the only thing letting this reload finish. At the old
                        // 40 tiles the phase passed vacuously — no in-range secondary,
                        // nothing to guard against.
                        ParkRaiderAt(abort, 12);
                        StartReload(abort, playerForced: true);
                    },
                    checks =
                    {
                        C("forced-reload-completes", () =>
                        {
                            ThingWithComps primary = abort.equipment?.Primary;
                            CompAmmoUser user = primary?.TryGetComp<CompAmmoUser>();
                            bool full = primary?.def == rifle && user != null && user.CurMagCount == user.MagSize;
                            return (full, $"primary={primary?.def?.defName} mag={user?.CurMagCount}/{user?.MagSize} job={abort.CurJobDef?.defName}");
                        }),
                    }
                },
            };
        }

        // -- TACT-2: forced-weapon dry fall-through -------------------------

        private List<Phase> BuildTact2()
        {
            Pawn forcy = Colonist("Forcy");
            ThingDef revolver = D("Gun_Revolver");
            ThingDef pistol = D("Gun_Autopistol");

            void CallEquip() => WeaponAssingment.equipBestWeaponFromInventoryByPreference(
                forcy, PeteTimesSix.SimpleSidearms.Utilities.Enums.DroppingModeEnum.Combat);

            (bool, string) ForcedStill()
            {
                var forced = CompSidearmMemory.GetMemoryCompForPawn(forcy).ForcedWeapon;
                bool ok = forced != null && forced.Value.thing == revolver;
                return (ok, $"ForcedWeapon={(forced?.thing?.defName ?? "null")} (must never be cleared)");
            }

            // Every phase below re-establishes the forced-dry revolver itself, so each
            // one stands alone against a fresh save (the isolated sweep). Sequenced,
            // the re-staging is idempotent: same flag value, an already-dry magazine.
            void ForceDryRevolver()
            {
                CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(forcy);
                ThingWithComps revolverThing = Carried(forcy, revolver);
                memory.ForcedWeapon = revolverThing.toThingDefStuffDefPair();
                revolverThing.TryGetComp<CompAmmoUser>().CurMagCount = 0; // dry: no spares staged
            }

            return new List<Phase>
            {
                new Phase
                {
                    label = "default-off-forced-holds-while-dry",
                    deadlineTicks = 3000,
                    mutate = () =>
                    {
                        ForceDryRevolver();
                        CallEquip();
                    },
                    checks =
                    {
                        C("primary-still-dry-revolver", () =>
                        {
                            ThingDef primary = forcy.equipment?.Primary?.def;
                            return (primary == revolver, $"primary={primary?.defName ?? "none"} (feature OFF: forced branch must hold)");
                        }),
                        C("forced-flag-intact", () => ForcedStill()),
                    }
                },
                new Phase
                {
                    label = "on-falls-through-to-pistol",
                    deadlineTicks = 3000,
                    arrange = () => ForceDryRevolver(),
                    mutate = () =>
                    {
                        TacticsMod.Settings.forcedDryFallthrough = true;
                        CallEquip();
                    },
                    checks =
                    {
                        P("forced-and-dry", () =>
                        {
                            var forced = CompSidearmMemory.GetMemoryCompForPawn(forcy).ForcedWeapon;
                            CompAmmoUser user = Carried(forcy, revolver)?.TryGetComp<CompAmmoUser>();
                            bool ok = forced?.thing == revolver && user != null && user.CurMagCount == 0;
                            return (ok, $"forced={forced?.thing?.defName ?? "null"} mag={user?.CurMagCount}");
                        }),
                        C("primary-becomes-pistol", () =>
                        {
                            ThingDef primary = forcy.equipment?.Primary?.def;
                            return (primary == pistol, $"primary={primary?.defName ?? "none"}");
                        }),
                        C("forced-flag-never-cleared", () => ForcedStill()),
                    }
                },
                new Phase
                {
                    label = "ammo-back-forced-resumes",
                    deadlineTicks = 3000,
                    // The resume must be FROM the fallen-through state, not from a pawn
                    // that never left the revolver — arrange replays the fall-through and
                    // the precondition proves it landed before ammo comes back.
                    arrange = () =>
                    {
                        ForceDryRevolver();
                        TacticsMod.Settings.forcedDryFallthrough = true;
                        CallEquip();
                    },
                    mutate = () =>
                    {
                        ThingWithComps revolverThing = Carried(forcy, revolver);
                        CompAmmoUser user = revolverThing.TryGetComp<CompAmmoUser>();
                        AmmoDef ammo = user.SelectedAmmo ?? user.CurrentAmmo;
                        Thing stack = ThingMaker.MakeThing(ammo);
                        stack.stackCount = user.MagSize * 2;
                        forcy.inventory.innerContainer.TryAdd(stack, true);
                        CallEquip();
                    },
                    checks =
                    {
                        P("fallen-through-to-pistol", () =>
                        {
                            ThingDef primary = forcy.equipment?.Primary?.def;
                            return (primary == pistol, $"primary={primary?.defName ?? "none"}");
                        }),
                        C("primary-back-to-revolver", () =>
                        {
                            ThingDef primary = forcy.equipment?.Primary?.def;
                            return (primary == revolver, $"primary={primary?.defName ?? "none"} (forced resumes when ammo exists)");
                        }),
                        C("forced-flag-intact-throughout", () => ForcedStill()),
                    }
                },
            };
        }

        // -- TACT-3: ammo-depth tiebreak ------------------------------------

        private List<Phase> BuildTact3()
        {
            Pawn tiedy = Colonist("Tiedy");
            ThingDef pistol = D("Gun_Autopistol");
            ThingDef rifle = D("Gun_AssaultRifle");

            ThingWithComps EquippedTwin() => tiedy.equipment.Primary;
            ThingWithComps InventoryTwin() => tiedy.inventory.innerContainer
                .OfType<ThingWithComps>().First(t => t.def == pistol);
            (ThingWithComps weapon, float dps, float averageSpeed) FindBest() =>
                GettersFilters.findBestRangedWeapon(tiedy, null);

            return new List<Phase>
            {
                new Phase
                {
                    label = "default-off-informational",
                    deadlineTicks = 3000,
                    mutate = () =>
                    {
                        // Depth difference: equipped twin nearly dry, inventory twin full;
                        // shared caliber spares stay in inventory (both reloadable).
                        EquippedTwin().TryGetComp<CompAmmoUser>().CurMagCount = 1;
                    },
                    checks =
                    {
                        C("off-pick-recorded", () =>
                        {
                            var (weapon, dps, _) = FindBest();
                            bool isEquipped = weapon == EquippedTwin();
                            return (true, $"feature OFF pick: {(isEquipped ? "equipped(drained)" : "inventory(full)")} dps={dps:F2}");
                        }, informational: true),
                    }
                },
                new Phase
                {
                    label = "on-picks-deeper-twin",
                    deadlineTicks = 3000,
                    // Stands alone against a fresh save: the depth difference is staged
                    // here, not inherited from phase 1 (idempotent when sequenced — the
                    // magazine is already at 1).
                    arrange = () => { EquippedTwin().TryGetComp<CompAmmoUser>().CurMagCount = 1; },
                    mutate = () => { TacticsMod.Settings.ammoDepthTiebreak = true; },
                    checks =
                    {
                        P("depth-differs", () =>
                        {
                            int eq = EquippedTwin().TryGetComp<CompAmmoUser>().CurMagCount;
                            int inv = InventoryTwin().TryGetComp<CompAmmoUser>().CurMagCount;
                            return (eq < inv, $"equipped mag={eq} inventory mag={inv}");
                        }),
                        C("winner-is-full-inventory-twin", () =>
                        {
                            var (weapon, dps, _) = FindBest();
                            CompAmmoUser user = weapon?.TryGetComp<CompAmmoUser>();
                            bool ok = weapon == InventoryTwin();
                            return (ok, $"winner mag={user?.CurMagCount}/{user?.MagSize} equippedTwin mag={EquippedTwin().TryGetComp<CompAmmoUser>().CurMagCount}");
                        }),
                    }
                },
                new Phase
                {
                    label = "epsilon-subordinate-to-dps",
                    deadlineTicks = 3000,
                    // Isolated runs never see phase 2's enable; without this the phase
                    // "passes" with the feature off — the rifle wins raw and proves
                    // nothing about the tie window staying subordinate.
                    arrange = () => { TacticsMod.Settings.ammoDepthTiebreak = true; },
                    mutate = () =>
                    {
                        // A clearly-better rifle with a nearly-empty mag and zero spares:
                        // outside the tie window, depth must NOT demote it.
                        var rifleThing = (ThingWithComps)ThingMaker.MakeThing(rifle);
                        CompAmmoUser user = rifleThing.TryGetComp<CompAmmoUser>();
                        user.ResetAmmoCount();
                        user.CurMagCount = 1;
                        tiedy.inventory.innerContainer.TryAdd(rifleThing, true);
                        CompSidearmMemory.GetMemoryCompForPawn(tiedy)?.InformOfAddedSidearm(rifleThing);
                    },
                    checks =
                    {
                        C("high-dps-rifle-wins-despite-shallow-ammo", () =>
                        {
                            var (weapon, dps, _) = FindBest();
                            return (weapon?.def == rifle, $"winner={weapon?.def?.defName} dps={dps:F2} (depth must stay subordinate)");
                        }),
                    }
                },
            };
        }
    }
}

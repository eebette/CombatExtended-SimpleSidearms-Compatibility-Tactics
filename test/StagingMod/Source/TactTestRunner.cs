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
using UnityEngine;
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
                        // 11 distinct methods today: equipBestWeaponFromInventoryByPreference
                        // (forced-dry hide + melee scope), SetWeaponAsForced (lesson note),
                        // tryCQCWeaponSwapToMelee (forced-dry CQC coverage),
                        // findBestRangedWeapon (selection scope), RangedDPS + RangedDPSAverage
                        // (in-scope adjustment/recording), trySwapToMoreAccurateRangedWeapon
                        // (symmetric swap comparison), findBestMeleeWeapon (all-hopeless defer),
                        // getMeleeDPSBiased (in-scope melee adjustment),
                        // JobGiver_CheckReload.DoReloadCheck (drafted sidearm top-off),
                        // Extensions.GetCarriedWeapons (reload-abort's loaded-now scope).
                        return (mine.Count >= 11,
                            $"methods patched by eebette.CESimpleSidearmsCompat.Tactics={mine.Count} (want >= 11): "
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
            TacticsMod.Settings.draftedSidearmReload = false;
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

        /// <summary>An invariant held across the whole phase: eval returns TRUE while
        /// the world stays good, and the first FALSE trips the phase (the compat
        /// suite's convention — not "return true when the bad thing happens").</summary>
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
                case "tact6": return BuildTact6();
                default: throw new InvalidOperationException("Unknown scenario: " + name);
            }
        }

        private List<Phase> BuildTact6()
        {
            Pawn abort = Colonist("Abort");
            ThingDef pistol = D("Gun_Autopistol");
            Pawn closeHostile = null; // Raider() filters Downed — hold the instance ourselves

            CompAmmoUser PistolComp() => Carried(abort, pistol).TryGetComp<CompAmmoUser>();

            // Drafted pawn, empty sidearm, ammo on hand, cooldown long elapsed,
            // hostile far outside CE's safe distance (12.9 default). Idempotent, so
            // every phase stands alone against a fresh save.
            void StageDraftedDry()
            {
                abort.drafter.Drafted = true;
                CompAmmoUser pu = PistolComp();
                pu.CurMagCount = 0;
                AmmoDef ammo = pu.SelectedAmmo ?? pu.CurrentAmmo;
                CompInventory inv = abort.TryGetComp<CompInventory>();
                if (inv.AmmoCountOfDef(ammo) < 7)
                {
                    Thing stack = ThingMaker.MakeThing(ammo);
                    stack.stackCount = 20;
                    abort.inventory.innerContainer.TryAdd(stack, false);
                    inv.UpdateInventory(); // cached availables lie after bare inserts
                }
                abort.mindState.lastAttackTargetTick = Find.TickManager.TicksGame - 60000;
                // WELL outside the rifle's 55 range: a wandering raider inside it makes
                // the drafted pawn auto-fire, refreshing the cooldown forever.
                ParkRaiderAt(abort, 100);
            }

            (bool, string) StagedState()
            {
                CompAmmoUser pu = PistolComp();
                CompInventory inv = abort.TryGetComp<CompInventory>();
                AmmoDef ammo = pu.SelectedAmmo ?? pu.CurrentAmmo;
                bool ok = abort.Drafted && pu.CurMagCount == 0 && inv.AmmoCountOfDef(ammo) >= 7;
                return (ok, $"drafted={abort.Drafted} mag={pu.CurMagCount} ammo={inv.AmmoCountOfDef(ammo)}");
            }

            return new List<Phase>
            {
                new Phase
                {
                    label = "default-off-sidearm-stays-empty",
                    deadlineTicks = 4000,
                    minTicks = 1800,
                    arrange = () => StageDraftedDry(),
                    checks =
                    {
                        P("staged-drafted-dry", StagedState),
                        N("sidearm-stays-empty", () =>
                        {
                            int mag = PistolComp().CurMagCount;
                            return (mag == 0, $"mag={mag} (feature OFF must be inert)");
                        }),
                        C("still-drafted-and-dry", () =>
                            (abort.Drafted && PistolComp().CurMagCount == 0,
                             $"drafted={abort.Drafted} mag={PistolComp().CurMagCount}")),
                    }
                },
                new Phase
                {
                    label = "lull-tops-off-the-sidearm",
                    deadlineTicks = 8000,
                    arrange = () => StageDraftedDry(),
                    mutate = () => { TacticsMod.Settings.draftedSidearmReload = true; },
                    poll = () =>
                    {
                        Pawn r = Raider();
                        if (r != null && !r.Downed && r.Position.DistanceTo(abort.Position) < 70f)
                        {
                            ParkRaiderAt(abort, 100);
                            abort.mindState.lastAttackTargetTick = Find.TickManager.TicksGame - 60000;
                        }
                    },
                    checks =
                    {
                        P("staged-drafted-dry", StagedState),
                        C("sidearm-magazine-full", () =>
                        {
                            CompAmmoUser pu = PistolComp();
                            return (pu.CurMagCount == pu.MagSize,
                                $"mag={pu.CurMagCount}/{pu.MagSize} job={abort.CurJobDef?.defName} drafted={abort.Drafted}");
                        }),
                        C("still-drafted", () => (abort.Drafted, $"drafted={abort.Drafted}")),
                    }
                },
                new Phase
                {
                    label = "hostile-in-safe-distance-blocks",
                    deadlineTicks = 4000,
                    minTicks = 1800,
                    // The close hostile is DOWNED on purpose: CE's safe-distance
                    // predicate counts any non-invisible hostile pawn, downed
                    // included, and this feature mirrors that predicate exactly —
                    // while a downed raider cannot beat up the defenseless subject
                    // for the length of the negative window.
                    arrange = () =>
                    {
                        StageDraftedDry();
                        TacticsMod.Settings.draftedSidearmReload = true;
                        closeHostile = Raider() ?? SpawnThreat(abort.Map);
                        ParkPawnNear(abort, closeHostile, 8);
                        if (!closeHostile.Downed)
                        {
                            HealthUtility.DamageUntilDowned(closeHostile, allowBleedingWounds: false);
                        }
                    },
                    checks =
                    {
                        P("staged-with-close-hostile", () =>
                        {
                            var (ok, detail) = StagedState();
                            bool alive = closeHostile != null && !closeHostile.Dead && closeHostile.Spawned;
                            float dist = alive ? closeHostile.Position.DistanceTo(abort.Position) : -1f;
                            return (ok && alive && dist < 12f,
                                $"{detail} raiderDist={dist:F0} downed={closeHostile?.Downed}");
                        }),
                        N("no-reload-under-threat", () =>
                        {
                            int mag = PistolComp().CurMagCount;
                            bool reloading = abort.CurJobDef == CE_JobDefOf.ReloadWeapon;
                            return (mag == 0 && !reloading, $"mag={mag} reloading={reloading}");
                        }),
                        C("still-dry-with-threat-near", () =>
                            (PistolComp().CurMagCount == 0, $"mag={PistolComp().CurMagCount}")),
                    }
                },
            };
        }

        private static void ForceMeleeAttack(Pawn attacker, Pawn victim)
        {
            Job job = JobMaker.MakeJob(JobDefOf.AttackMelee, victim);
            job.expiryInterval = 500;
            job.checkOverrideOnExpire = false;
            attacker.jobs.StartJob(job, JobCondition.InterruptForced);
        }

        /// <summary>ParkPawnNear only places due EAST; from some anchors that whole
        /// line is LOS-blocked at every distance. Walk a ring of bearings and take
        /// the first standable cell the anchor can actually see.</summary>
        private static void ParkWithLOS(Pawn anchor, Pawn parked, int distance)
        {
            Map map = anchor.Map;
            for (int i = 0; i < 16; i++)
            {
                float angle = i * 22.5f * Mathf.Deg2Rad;
                IntVec3 cell = anchor.Position + new IntVec3(
                    Mathf.RoundToInt(distance * Mathf.Cos(angle)), 0,
                    Mathf.RoundToInt(distance * Mathf.Sin(angle)));
                cell = cell.ClampInsideMap(map);
                if (cell.Standable(map) && GenSight.LineOfSight(anchor.Position, cell, map, skipFirstCell: true))
                {
                    parked.Position = cell;
                    parked.Notify_Teleported();
                    return;
                }
            }
            ParkPawnNear(anchor, parked, distance); // no visible ring cell — east fallback
        }

        private static Pawn SpawnMech(string kindDefName, Pawn anchor, int distance)
        {
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(kindDefName)
                ?? throw new InvalidOperationException(kindDefName + " kind missing");
            Pawn mech = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                kind, Faction.OfMechanoids, PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true, canGeneratePawnRelations: false));
            GenSpawn.Spawn(mech, anchor.Map.Center, anchor.Map);
            ParkPawnNear(anchor, mech, distance);
            return mech;
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
            Pawn scyther = null;
            Pawn warmupScyther = null;
            int hopelessAdjustTick = 0;
            string hopelessStaging = "";

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
                    // The staged centipede's plate stops BOTH loads outright under the
                    // CE-true model (see the defer phase below) — the flip needs armor
                    // the rifle penetrates and buckshot does not, so this phase brings
                    // its own scyther.
                    mutate = () =>
                    {
                        TacticsMod.Settings.targetAwareAmmoScoring = true;
                        scyther = SpawnMech("Mech_Scyther", ammy, 8);
                    },
                    checks =
                    {
                        C("winner-is-rifle-vs-scyther", () =>
                        {
                            var (weapon, dps, _) = FindBest(scyther);
                            return (weapon?.def == rifle, $"ON pick={weapon?.def?.defName} adj={dps:F2}; {Detail(scyther)}");
                        }),
                    }
                },
                new Phase
                {
                    label = "on-hopeless-armor-defers-to-raw",
                    deadlineTicks = 3000,
                    // Centipede plate zeroes every multiplier; re-ranking zeros would be
                    // noise, so the feature must stand down and let SS's raw pick
                    // through (the close-range shotgun) — pins F04's zero-defer branch.
                    // The scyther goes away first: it would otherwise shadow Mech() and
                    // carve up Ammy while the phase polls.
                    arrange = () =>
                    {
                        TacticsMod.Settings.targetAwareAmmoScoring = true;
                        if (scyther != null && !scyther.Destroyed)
                        {
                            scyther.Destroy();
                        }
                        // Isolated runs skip phase 1's park: at the centipede's saved
                        // position both guns are out of range, the phase runs red to
                        // deadline, and CE's loadout enforcement strips the unshielded
                        // colonist during the window (the core TESTPLAN's known
                        // staging weakness). Idempotent when sequenced.
                        ParkPawnNear(ammy, Mech(), 8);
                    },
                    checks =
                    {
                        C("raw-pick-stands-when-all-zero", () =>
                        {
                            var (weapon, dps, _) = FindBest(Mech());
                            return (weapon?.def == shotgun, $"pick={weapon?.def?.defName} adj={dps:F2}; {Detail(Mech())}");
                        }),
                    }
                },
                new Phase
                {
                    label = "defer-never-resurrects-a-dry-gun",
                    deadlineTicks = 3000,
                    // T3-4: records include dry weapons at full paper score, and the
                    // core patch's dry-pick correction has already run by the time the
                    // defer fires. Drain the raw-best shotgun completely: the stand-down
                    // must land on the loaded rifle, never the empty shotgun.
                    arrange = () =>
                    {
                        TacticsMod.Settings.targetAwareAmmoScoring = true;
                        CompAmmoUser su = Carried(ammy, shotgun).TryGetComp<CompAmmoUser>();
                        su.CurMagCount = 0;
                        var shotgunAmmo = su.Props?.ammoSet?.ammoTypes?.Select(l => (ThingDef)l.ammo).ToList();
                        if (shotgunAmmo != null)
                        {
                            foreach (Thing t in ammy.inventory.innerContainer
                                .Where(t => shotgunAmmo.Contains(t.def)).ToList())
                            {
                                t.Destroy(DestroyMode.Vanish);
                            }
                            ammy.TryGetComp<CompInventory>().UpdateInventory();
                        }
                        if (scyther != null && !scyther.Destroyed)
                        {
                            scyther.Destroy();
                        }
                        ParkPawnNear(ammy, Mech(), 8);
                    },
                    // The centipede wanders: past ~16 cells the dry shotgun leaves its
                    // own range window, gets no record at all, and the A-leg passes
                    // vacuously (seen at dist=17, raw=-1). Keep the matchup staged.
                    poll = () =>
                    {
                        Pawn mech = Mech();
                        if (mech != null && mech.Position.DistanceTo(ammy.Position) > 12f)
                        {
                            ParkPawnNear(ammy, mech, 8);
                        }
                    },
                    checks =
                    {
                        P("shotgun-truly-dry", () =>
                        {
                            CompAmmoUser su = Carried(ammy, shotgun).TryGetComp<CompAmmoUser>();
                            return (su.CurMagCount == 0 && !su.HasAmmo, $"mag={su.CurMagCount} hasAmmo={su.HasAmmo}");
                        }),
                        C("defer-forensics", () =>
                        {
                            float bias = PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings.SpeedSelectionBiasRanged;
                            Pawn mech = Mech();
                            float dist = mech.Position.DistanceTo(ammy.Position);
                            string line = string.Join(" ", new[] { rifle, shotgun }.Select(def =>
                            {
                                ThingWithComps w = Carried(ammy, def);
                                CompAmmoUser u = w?.TryGetComp<CompAmmoUser>();
                                float raw = w != null ? StatCalculator.RangedDPS(w, bias, 0f, dist) : -9f;
                                float mult = w != null ? CESSCompatTactics.Features.TargetScoring.RangedMultiplier(w, mech) : -9f;
                                return $"{def.defName}: raw={raw:F1} mult={mult:F2} mag={u?.CurMagCount} hasAmmo={u?.HasAmmo}";
                            }));
                            return (true, $"dist={dist:F0} {line}");
                        }, informational: true),
                        C("stand-down-lands-on-a-loaded-gun", () =>
                        {
                            var (weapon, dps, _) = FindBest(Mech());
                            return (weapon?.def == rifle, $"pick={weapon?.def?.defName} adj={dps:F2}");
                        }),
                    }
                },
                new Phase
                {
                    label = "warmup-swap-actually-draws-the-penetrator",
                    deadlineTicks = 7000,
                    // T3-3: the ONLY in-game path that feeds a target into ranged
                    // selection is the warmup auto-switch — and it used to compare the
                    // challenger's armor-adjusted score against the incumbent's RAW
                    // score, so the flip never fired outside the harness. Staging lives
                    // in mutate behind a world-is-ticking gate: isolated runs arrange at
                    // tick 0, where CE's caches and stance machinery lie (core suite
                    // lesson), and this phase broke both ways there before the gate.
                    arrange = () =>
                    {
                        TacticsMod.Settings.targetAwareAmmoScoring = true;
                        PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings.RangedCombatAutoSwitch = true;
                        PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings.RangedCombatAutoSwitchMaxWarmup = 0.9f;
                    },
                    mutate = () =>
                    {
                        // reload whatever earlier phases drained; both guns must be live
                        foreach (ThingDef def in new[] { rifle, shotgun })
                        {
                            CompAmmoUser u = Carried(ammy, def).TryGetComp<CompAmmoUser>();
                            if (u.CurMagCount < u.MagSize)
                            {
                                u.ResetAmmoCount();
                            }
                        }
                        ammy.TryGetComp<CompInventory>().UpdateInventory();
                        ThingWithComps sg = Carried(ammy, shotgun);
                        if (ammy.equipment?.Primary != sg)
                        {
                            ammy.TryGetComp<CompInventory>().TrySwitchToWeapon(sg);
                        }
                        // The staged centipede sits ~8 cells out and RETURNS FIRE the
                        // moment Ammy drafts and shoots — and downing it here races its
                        // in-flight burst (and sometimes kills it outright). Park it out
                        // of blaster range instead; the hopeless phase stages its own.
                        Pawn centipede = Mech();
                        if (centipede != null)
                        {
                            ParkPawnNear(ammy, centipede, 60);
                        }
                        warmupScyther = SpawnMech("Mech_Scyther", ammy, 14);
                        ammy.drafter.Drafted = true;
                        Job job = JobMaker.MakeJob(JobDefOf.AttackStatic, warmupScyther);
                        ammy.jobs.StartJob(job, JobCondition.InterruptForced);
                    },
                    // The moment the swap lands the scyther has done its job — destroy
                    // it before its charge reaches Ammy (it killed the subject between
                    // phases and left the NEXT phase anchored on a dead pawn).
                    poll = () =>
                    {
                        if (ammy.equipment?.Primary?.def == rifle
                            && warmupScyther != null && !warmupScyther.Destroyed)
                        {
                            warmupScyther.Destroy();
                        }
                    },
                    checks =
                    {
                        P("world-is-ticking", () =>
                            (Find.TickManager.TicksGame > 60, $"tick={Find.TickManager.TicksGame}")),
                        C("staging-forensics", () =>
                        {
                            float dist = warmupScyther != null && !warmupScyther.Destroyed && warmupScyther.Spawned
                                ? warmupScyther.Position.DistanceTo(ammy.Position) : -1f;
                            return (true, $"primary={ammy.equipment?.Primary?.def?.defName} dist={dist:F0} "
                                + $"job={ammy.CurJobDef?.defName}");
                        }, informational: true),
                        C("warmup-draws-the-rifle", () =>
                        {
                            ThingDef primary = ammy.equipment?.Primary?.def;
                            return (primary == rifle, $"primary={primary?.defName} job={ammy.CurJobDef?.defName}");
                        }),
                    }
                },
                new Phase
                {
                    label = "warmup-vs-hopeless-armor-still-fires",
                    deadlineTicks = 9000,
                    // Convergence C1: the all-hopeless defer used to hand trySwap a RAW
                    // score against an in-scope incumbent adjusted to ~0 — a phantom
                    // "swap" to the already-equipped gun every warmup, which reset the
                    // attack job forever: the pawn aimed eternally and never fired,
                    // flooding the log with SS's already-equipped warning. That warning
                    // is not on any allowlist, so the diagnostics machinery alone turns
                    // the A-leg red; the positive check pins the shot actually leaving
                    // the barrel.
                    arrange = () =>
                    {
                        TacticsMod.Settings.targetAwareAmmoScoring = true;
                        PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings.RangedCombatAutoSwitch = true;
                        PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings.RangedCombatAutoSwitchMaxWarmup = 0.9f;
                        // Neutralize the centipede HERE, not in mutate: isolated runs
                        // load with it live at its saved ~8 cells, and the
                        // world-is-ticking gate hands it 60 free ticks to down the
                        // subject before mutate could act. Position and health writes
                        // are tick-0-safe (the tick-0 lie is about caches and stances).
                        // NOTE: DamageUntilDowned often CANNOT down a mech (it ends
                        // alive at ~8% hp) — the real shield is the 45-cell park,
                        // outside the charge blaster's reach; the wounding just makes
                        // the first rifle hit decisive.
                        Pawn mech0 = Mech();
                        int attempts = 0;
                        for (int attempt = 0; attempt < 2 && (mech0 == null || !mech0.Downed); attempt++)
                        {
                            attempts++;
                            if (mech0 == null)
                            {
                                mech0 = SpawnMech("Mech_CentipedeBlaster", ammy, 45);
                            }
                            ParkWithLOS(ammy, mech0, 45);
                            if (!mech0.Downed)
                            {
                                HealthUtility.DamageUntilDowned(mech0, allowBleedingWounds: false);
                            }
                            if (mech0.Dead)
                            {
                                mech0 = null;
                            }
                        }
                        hopelessStaging = mech0 == null
                            ? $"attempts={attempts} mech=DEAD"
                            : $"attempts={attempts} dist={mech0.Position.DistanceTo(ammy.Position):F0} "
                              + $"downed={mech0.Downed} hp={mech0.health.summaryHealth.SummaryHealthPercent:F2} "
                              + $"ammyPos={ammy.Position} mechPos={mech0.Position}";
                    },
                    mutate = () =>
                    {
                        string step = "recover-guns";
                        try
                        {
                            // The centipede's last in-flight burst can DOWN Ammy right as
                            // the previous phase latches — the rifle lands on the floor.
                            // Recover anything dropped, then re-arm.
                            foreach (ThingDef def in new[] { rifle, shotgun })
                            {
                                if (Carried(ammy, def) == null)
                                {
                                    Thing ground = ammy.Map.listerThings.ThingsOfDef(def).FirstOrDefault();
                                    if (ground is ThingWithComps rec)
                                    {
                                        if (rec.Spawned)
                                        {
                                            rec.DeSpawn();
                                        }
                                        ammy.inventory.innerContainer.TryAdd(rec, false);
                                    }
                                }
                            }
                            ammy.TryGetComp<CompInventory>().UpdateInventory();
                            step = "top-guns";
                            foreach (ThingDef def in new[] { rifle, shotgun })
                            {
                                CompAmmoUser u = Carried(ammy, def)?.TryGetComp<CompAmmoUser>();
                                if (u != null && u.CurMagCount < u.MagSize)
                                {
                                    u.ResetAmmoCount();
                                }
                            }
                            step = "re-equip";
                            // The RIFLE, unconditionally: isolated runs load with the
                            // save's shotgun in hand, whose ~16 range never reaches the
                            // 45-cell target — the pawn stood Mobile forever. Sequenced
                            // only worked because the warmup phase had already swapped.
                            if (ammy.equipment?.Primary?.def != rifle && Carried(ammy, rifle) != null)
                            {
                                ammy.TryGetComp<CompInventory>().TrySwitchToWeapon(Carried(ammy, rifle));
                            }
                            step = "clear-scyther";
                            if (warmupScyther != null && !warmupScyther.Destroyed)
                            {
                                warmupScyther.Destroy(); // must not shadow Mech()
                            }
                            step = "park-centipede";
                            Pawn mech = Mech() ?? throw new InvalidOperationException("no downed centipede staged");
                            step = "attack";
                            ammy.drafter.Drafted = true;
                            Job job = JobMaker.MakeJob(JobDefOf.AttackStatic, mech);
                            ammy.jobs.StartJob(job, JobCondition.InterruptForced);
                        }
                        catch (Exception e)
                        {
                            throw new Exception($"hopeless-warmup step '{step}': {e.Message}", e);
                        }
                    },
                    // 45 cells can lack a firing line on this map (stance never leaves
                    // Mobile). Step the downed centipede closer every ~300 ticks until a
                    // shot happens; floor 22 keeps clear of the ammo cook-off blast.
                    poll = () =>
                    {
                        if (!ammy.Spawned || ammy.stances?.curStance is Stance_Cooldown
                            || Find.TickManager.TicksGame - hopelessAdjustTick < 300)
                        {
                            return;
                        }
                        Pawn mech = Mech();
                        if (mech == null)
                        {
                            return;
                        }
                        hopelessAdjustTick = Find.TickManager.TicksGame;
                        float d = mech.Position.DistanceTo(ammy.Position);
                        if (d > 52f)
                        {
                            ParkWithLOS(ammy, mech, 45);
                        }
                        else if (ammy.stances?.curStance is Stance_Mobile)
                        {
                            // Still no firing solution: LOS-park on a ring, stepping
                            // closer each pass (floor 22 — cook-off clearance).
                            ParkWithLOS(ammy, mech, (int)Mathf.Max(d - 8f, 22f));
                        }
                    },
                    checks =
                    {
                        P("world-is-ticking", () =>
                            (Find.TickManager.TicksGame > 60, $"tick={Find.TickManager.TicksGame}")),
                        C("staging-snapshot", () => (true, hopelessStaging), informational: true),
                        C("subject-forensics", () =>
                        {
                            var hostiles = ammy.Spawned ? ammy.Map.mapPawns.AllPawnsSpawned
                                .Where(x => x.HostileTo(Faction.OfPlayer)).Select(x =>
                                    $"{x.def.defName}@{x.Position.DistanceTo(ammy.Position):F0}{(x.Downed ? "(downed)" : "")}")
                                .ToList() : new List<string>();
                            return (true, $"dead={ammy.Dead} spawned={ammy.Spawned} "
                                + $"hp={(ammy.Dead ? 0f : ammy.health.summaryHealth.SummaryHealthPercent):F2} "
                                + $"hostiles=[{string.Join(",", hostiles)}]");
                        }, informational: true),
                        C("a-shot-actually-fires", () =>
                        {
                            bool fired = ammy.stances?.curStance is Stance_Cooldown;
                            return (fired, $"stance={ammy.stances?.curStance?.GetType()?.Name} "
                                + $"job={ammy.CurJobDef?.defName} primary={ammy.equipment?.Primary?.def?.defName}");
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
            Pawn cqcAttacker = null;
            string fleshForensics = "";

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
                        // Synchronous capture BEFORE the raider can close: later polls
                        // can be poisoned by the brawl (the check itself stays live).
                        float bias = PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings.SpeedSelectionBiasMelee;
                        var sb = new System.Text.StringBuilder();
                        sb.Append($"rawPickAtMutate={FindMelee(fleshTarget)?.def?.defName ?? "fists"} (unscoped SS pick) ");
                        foreach (ThingWithComps w in marcy.GetCarriedWeapons(true, true))
                        {
                            float ss = StatCalculator.getMeleeDPSBiased(w, marcy, bias, 0f);
                            float pen = StatCalculator.MeleePenetration(w, marcy);
                            float f = CESSCompatTactics.Features.TargetScoring.MeleeTargetFactor(w, fleshTarget);
                            sb.Append($"{w.def.defName}: ss={ss:F2} pen={pen:F2} factor={f:F2} final={ss / (1f + pen) * f:F2}; ");
                        }
                        fleshForensics = sb.ToString();
                    },
                    checks =
                    {
                        C("flesh-forensics", () => (true, $"{fleshForensics} nowDowned={marcy.Downed}"), informational: true),
                        C("blade-vs-flesh", () =>
                        {
                            // Through the preference tree WITH the target — the entry the
                            // T3-2 rework hooks. A bare findBestMeleeWeapon call opens no
                            // scope on purpose (that is the dead wiring the rework fixed).
                            WeaponAssingment.equipBestWeaponFromInventoryByPreference(
                                marcy, PeteTimesSix.SimpleSidearms.Utilities.Enums.DroppingModeEnum.Combat,
                                PeteTimesSix.SimpleSidearms.Utilities.Enums.PrimaryWeaponMode.Melee, fleshTarget);
                            ThingDef primary = marcy.equipment?.Primary?.def;
                            return (primary == knife, $"primary vs flesh={primary?.defName ?? "fists"} armor={fleshTarget.GetStatValue(StatDefOf.ArmorRating_Sharp):F2}");
                        }),
                    }
                },
                new Phase
                {
                    label = "on-vs-armor-picks-blunt",
                    deadlineTicks = 3000,
                    // Centipede plate (blunt 45 MPa vs a 5.6 mace) zeroes every
                    // candidate under the CE-true model: the feature stands down and
                    // SS's own P12-backed ranking picks the mace as least-bad. The
                    // observable pick matches feature-off ON PURPOSE — this phase pins
                    // the defer semantics; the feature's actual flip is phase 2's
                    // knife-vs-flesh. A VANILLA-tools club (staging def, no CE data)
                    // rides along: unmodelable weapons must neither win the armor
                    // matchup by dodging the zeroing nor block the defer
                    // (convergence C5).
                    arrange = () =>
                    {
                        TacticsMod.Settings.armorAwareMelee = true;
                        if (Carried(marcy, D("CESSTest_VanillaClub")) == null)
                        {
                            var club = (ThingWithComps)ThingMaker.MakeThing(D("CESSTest_VanillaClub"));
                            marcy.inventory.innerContainer.TryAdd(club, false);
                            marcy.TryGetComp<CompInventory>().UpdateInventory();
                            CompSidearmMemory.GetMemoryCompForPawn(marcy)?.InformOfAddedSidearm(club);
                        }
                    },
                    checks =
                    {
                        C("blunt-vs-armored-mech", () =>
                        {
                            Pawn mech = Mech();
                            WeaponAssingment.equipBestWeaponFromInventoryByPreference(
                                marcy, PeteTimesSix.SimpleSidearms.Utilities.Enums.DroppingModeEnum.Combat,
                                PeteTimesSix.SimpleSidearms.Utilities.Enums.PrimaryWeaponMode.Melee, mech);
                            ThingDef primary = marcy.equipment?.Primary?.def;
                            return (primary == mace, $"primary vs mech={primary?.defName ?? "fists"} sharpArmor={mech.GetStatValue(StatDefOf.ArmorRating_Sharp):F1} bluntArmor={mech.GetStatValue(StatDefOf.ArmorRating_Blunt):F1}");
                        }),
                        C("tool-forensics", () =>
                        {
                            Pawn mech = Mech();
                            string line = string.Join(" ", new[] { knife, mace }.Select(def =>
                            {
                                ThingWithComps inst = Carried(marcy, def);
                                float f = CESSCompatTactics.Features.TargetScoring.MeleeTargetFactor(inst, mech);
                                return $"{def.defName}: targetFactor={f:F2}";
                            }));
                            return (true, line);
                        }, informational: true),
                    }
                },
                new Phase
                {
                    label = "a-real-swing-draws-the-knife",
                    deadlineTicks = 9000,
                    // T3-2: the direct-call phases above proved the MATH while the
                    // WIRING was dead — no in-game caller ever passed a target. This
                    // phase drives the real chain: an adjacent flesh raider swings,
                    // doCQC fires, the scope carries the attacker into SS's melee
                    // selection, and vs bare flesh the de-biased CE dps picks the
                    // knife over the mace SS would choose target-blind.
                    arrange = () =>
                    {
                        TacticsMod.Settings.armorAwareMelee = true;
                        // The armor phase's vanilla club raw-beats the knife vs FLESH
                        // even in stock SS — that phase's prop, not this one's.
                        foreach (var club in marcy.GetCarriedWeapons(true, true)
                            .Where(w => w.def == D("CESSTest_VanillaClub")).ToList())
                        {
                            club.Destroy(DestroyMode.Vanish);
                        }
                        marcy.TryGetComp<CompInventory>().UpdateInventory();
                        var ss = PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings;
                        ss.CQCAutoSwitch = true;
                        ss.OptimalMelee = true; // marcy holds a melee weapon; CQC must still evaluate
                        ThingWithComps maceInst = Carried(marcy, mace);
                        if (marcy.equipment?.Primary != maceInst)
                        {
                            marcy.TryGetComp<CompInventory>().TrySwitchToWeapon(maceInst);
                        }
                        cqcAttacker = Raider();
                        if (cqcAttacker == null)
                        {
                            SpawnThreat(marcy.Map);
                            cqcAttacker = Raider();
                        }
                        cqcAttacker.equipment?.DestroyAllEquipment();
                        cqcAttacker.apparel?.DestroyAll();
                        ParkPawnNear(marcy, cqcAttacker, 2);
                        ForceMeleeAttack(cqcAttacker, marcy);
                    },
                    poll = () =>
                    {
                        if (cqcAttacker != null && !cqcAttacker.Dead && !cqcAttacker.Downed
                            && (cqcAttacker.CurJobDef != JobDefOf.AttackMelee
                                || cqcAttacker.CurJob?.targetA.Thing != marcy))
                        {
                            ParkPawnNear(marcy, cqcAttacker, 2);
                            ForceMeleeAttack(cqcAttacker, marcy);
                        }
                    },
                    checks =
                    {
                        P("mace-in-hand-attacker-adjacent", () =>
                        {
                            float dist = cqcAttacker?.Position.DistanceTo(marcy.Position) ?? -1f;
                            return (marcy.equipment?.Primary?.def == mace && cqcAttacker != null
                                    && !cqcAttacker.Dead && dist < 8f,
                                $"primary={marcy.equipment?.Primary?.def?.defName} dist={dist:F0}");
                        }),
                        C("swing-triggers-the-knife", () =>
                        {
                            ThingDef primary = marcy.equipment?.Primary?.def;
                            string carried = string.Join(",", marcy.GetCarriedWeapons(true, true).Select(w => w.def.defName));
                            return (primary == knife, $"primary={primary?.defName ?? "none"} carried=[{carried}] raiderJob={cqcAttacker?.CurJobDef?.defName ?? "-"}");
                        }),
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
                        // A loaded rifle biocoded to someone else outranks the pistol on
                        // DPS; the winner scan must skip it (SS's own usability rule) or
                        // the abort dies at equip time in a reload-restart loop (T3-5).
                        Pawn coder = abort.Map.mapPawns.AllPawnsSpawned
                            .FirstOrDefault(x => x != abort && x.RaceProps.Humanlike);
                        if (coder != null && Carried(abort, D("Gun_AssaultRifle")) is ThingWithComps
                            && !abort.inventory.innerContainer.OfType<ThingWithComps>()
                                .Any(t => t.TryGetComp<CompBiocodable>()?.Biocoded ?? false))
                        {
                            var coded = (ThingWithComps)ThingMaker.MakeThing(D("Gun_AssaultRifle"));
                            coded.TryGetComp<CompAmmoUser>()?.ResetAmmoCount();
                            coded.TryGetComp<CompBiocodable>()?.CodeFor(coder);
                            abort.inventory.innerContainer.TryAdd(coded, false);
                            abort.TryGetComp<CompInventory>().UpdateInventory();
                            CompSidearmMemory.GetMemoryCompForPawn(abort)?.InformOfAddedSidearm(coded);
                        }
                        // A DRY second rifle with spares on hand: under the core patch's
                        // axis 3 it counts as "viable" to SS — the loaded-now scope must
                        // hide it or the abort equips an empty gun (C3's failing case).
                        bool dryDecoyPresent = abort.GetCarriedWeapons(true, true).Any(w =>
                            w.def == D("Gun_AssaultRifle") && w != abort.equipment?.Primary
                            && (w.TryGetComp<CompAmmoUser>()?.CurMagCount ?? 1) == 0);
                        if (!dryDecoyPresent)
                        {
                            var decoy = (ThingWithComps)ThingMaker.MakeThing(D("Gun_AssaultRifle"));
                            var du = decoy.TryGetComp<CompAmmoUser>();
                            if (du != null)
                            {
                                du.CurMagCount = 0;
                            }
                            abort.inventory.innerContainer.TryAdd(decoy, false);
                            abort.TryGetComp<CompInventory>().UpdateInventory();
                            CompSidearmMemory.GetMemoryCompForPawn(abort)?.InformOfAddedSidearm(decoy);
                        }
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
                new Phase
                {
                    label = "a-backpack-top-off-is-not-our-problem",
                    deadlineTicks = 7000,
                    // T3-1: reload-abort must ignore reloads of INVENTORY guns — here CE's
                    // OWN undrafted top-off (priority 9.1, no safe-distance gate) with a
                    // hostile visible in the old trigger band. Unfixed, F01 killed the
                    // top-off and swapped the loaded primary every 30 ticks, forever.
                    // Hostility response Ignore keeps the pawn from fleeing or firing, so
                    // the only actors are CE's job giver and F01's gate.
                    arrange = () =>
                    {
                        string step = "settings";
                        try
                        {
                            TacticsMod.Settings.reloadAbort = true;
                            step = "undraft";
                            abort.drafter.Drafted = false;
                            abort.playerSettings.hostilityResponse = HostilityResponseMode.Ignore;
                            step = "find-pistol";
                            ThingWithComps pi = Carried(abort, pistol);
                            if (pi == null)
                            {
                                // An earlier phase's weapon switch can bulk-drop the pistol
                                // to the floor (the biocoded rifle eats capacity). Recover.
                                Thing ground = abort.Map.listerThings.ThingsOfDef(pistol).FirstOrDefault();
                                pi = ground as ThingWithComps
                                    ?? (ThingWithComps)ThingMaker.MakeThing(pistol);
                                if (pi.Spawned)
                                {
                                    pi.DeSpawn();
                                }
                                abort.inventory.innerContainer.TryAdd(pi, false);
                                abort.TryGetComp<CompInventory>().UpdateInventory();
                                CompSidearmMemory.GetMemoryCompForPawn(abort)?.InformOfAddedSidearm(pi);
                            }
                            CompAmmoUser pu = pi.TryGetComp<CompAmmoUser>();
                            pu.CurMagCount = 0;
                            step = "loaded-sidearm";
                            // The livelock needs a LOADED inventory winner: without one the
                            // old code's scan came up empty and "kept reloading" by luck.
                            if (Carried(abort, D("Gun_Revolver")) == null)
                            {
                                var rev = (ThingWithComps)ThingMaker.MakeThing(D("Gun_Revolver"));
                                rev.TryGetComp<CompAmmoUser>()?.ResetAmmoCount();
                                abort.inventory.innerContainer.TryAdd(rev, false);
                                abort.TryGetComp<CompInventory>().UpdateInventory();
                                CompSidearmMemory.GetMemoryCompForPawn(abort)?.InformOfAddedSidearm(rev);
                            }
                            step = "ammo";
                            AmmoDef ammo = pu.SelectedAmmo ?? pu.CurrentAmmo;
                            CompInventory inv = abort.TryGetComp<CompInventory>();
                            if (ammo != null && inv.AmmoCountOfDef(ammo) < 7)
                            {
                                Thing stack = ThingMaker.MakeThing(ammo);
                                stack.stackCount = 20;
                                abort.inventory.innerContainer.TryAdd(stack, false);
                                inv.UpdateInventory();
                            }
                            step = "park";
                            ParkRaiderAt(abort, 20); // inside the rifle's range: the OLD trigger band
                        }
                        catch (Exception e)
                        {
                            throw new Exception($"backpack-arrange step '{step}': {e.Message}", e);
                        }
                    },
                    checks =
                    {
                        P("staged-undrafted-dry-with-threat", () =>
                        {
                            CompAmmoUser pu = Carried(abort, pistol).TryGetComp<CompAmmoUser>();
                            Pawn r = Raider();
                            float dist = r?.Position.DistanceTo(abort.Position) ?? -1f;
                            CompAmmoUser rv = Carried(abort, D("Gun_Revolver"))?.TryGetComp<CompAmmoUser>();
                            return (!abort.Drafted && pu.CurMagCount == 0 && rv != null && rv.CurMagCount > 0
                                    && r != null && !r.Downed && dist > 13f && dist < 45f,
                                $"drafted={abort.Drafted} mag={pu.CurMagCount} revolverMag={rv?.CurMagCount} dist={dist:F0}");
                        }),
                        N("primary-never-swaps", () =>
                        {
                            ThingDef primary = abort.equipment?.Primary?.def;
                            return (primary == rifle, $"primary={primary?.defName ?? "none"}");
                        }),
                        C("top-off-completes-despite-the-threat", () =>
                        {
                            CompAmmoUser pu = Carried(abort, pistol).TryGetComp<CompAmmoUser>();
                            return (pu.CurMagCount == pu.MagSize,
                                $"mag={pu.CurMagCount}/{pu.MagSize} job={abort.CurJobDef?.defName}");
                        }),
                    }
                },
            };
        }

        // -- TACT-2: forced-weapon dry fall-through -------------------------

        private List<Phase> BuildTact2()
        {
            Pawn forcy = Colonist("Forcy");
            ThingDef midRefillPrimary = null;
            string midRefillJobInfo = "";
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
                new Phase
                {
                    label = "cqc-draws-the-knife-past-a-dry-forced-gun",
                    deadlineTicks = 6000,
                    // T3-6: SS's melee-attacked reflex checks the forced flag one call
                    // ABOVE everything the fall-through used to hide — a pawn holding a
                    // truly-dry forced gun who got stabbed never drew a knife. This
                    // phase drives the REAL entry point: an adjacent raider swings, doCQC
                    // fires, and the extended hide lets the knife come out. The forced
                    // flag itself must survive untouched.
                    arrange = () =>
                    {
                        TacticsMod.Settings.forcedDryFallthrough = true;
                        ForceDryRevolver();
                        // TRULY dry: the ammo-back phase leaves two magazines of .44 in the
                        // pack, and spares mean "not dry" — strip them so the reflex faces
                        // the state this phase is about.
                        CompAmmoUser dryUser = Carried(forcy, revolver).TryGetComp<CompAmmoUser>();
                        var calibers = dryUser.Props?.ammoSet?.ammoTypes?.Select(l => (ThingDef)l.ammo).ToList();
                        if (calibers != null)
                        {
                            foreach (Thing t in forcy.inventory.innerContainer
                                .Where(t => calibers.Contains(t.def)).ToList())
                            {
                                t.Destroy(DestroyMode.Vanish);
                            }
                            forcy.TryGetComp<CompInventory>().UpdateInventory();
                        }
                        // back onto the dry forced revolver so the reflex is what acts
                        ThingWithComps rev = Carried(forcy, revolver);
                        if (forcy.equipment?.Primary != rev)
                        {
                            forcy.TryGetComp<CompInventory>().TrySwitchToWeapon(rev);
                        }
                        if (Carried(forcy, D("MeleeWeapon_Knife")) == null)
                        {
                            var knife = (ThingWithComps)ThingMaker.MakeThing(D("MeleeWeapon_Knife"), D("Steel"));
                            forcy.inventory.innerContainer.TryAdd(knife, false);
                            forcy.TryGetComp<CompInventory>().UpdateInventory();
                            CompSidearmMemory.GetMemoryCompForPawn(forcy)?.InformOfAddedSidearm(knife);
                        }
                        PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings.CQCAutoSwitch = true;
                        Pawn raider = Raider() ?? SpawnThreat(forcy.Map);
                        ParkPawnNear(forcy, raider, 2);
                        ForceMeleeAttack(raider, forcy);
                    },
                    // The map has other colonists; a free raider re-targets, walks off
                    // (observed at 70 tiles), or loses the swing race to the deadline
                    // (~50% of isolated runs). Drive the swing THROUGH THE REAL VERB
                    // ourselves: TryMeleeAttack goes verb → TryCastShot → the core
                    // patch's P06 re-attach → doCQC — the exact in-game chain, minus
                    // the AI's mood.
                    poll = () =>
                    {
                        Pawn raider = Raider();
                        if (raider == null || forcy.equipment?.Primary?.def == D("MeleeWeapon_Knife"))
                        {
                            return;
                        }
                        if (raider.Position.DistanceTo(forcy.Position) > 2f)
                        {
                            ParkPawnNear(forcy, raider, 1);
                        }
                        raider.meleeVerbs.TryMeleeAttack(forcy, null, surpriseAttack: false);
                    },
                    checks =
                    {
                        P("forced-dry-and-attacker-adjacent", () =>
                        {
                            var forced = CompSidearmMemory.GetMemoryCompForPawn(forcy).ForcedWeapon;
                            Pawn r = Raider();
                            float dist = r?.Position.DistanceTo(forcy.Position) ?? -1f;
                            return (forced?.thing == revolver && forcy.equipment?.Primary?.def == revolver
                                    && Carried(forcy, D("MeleeWeapon_Knife")) != null && r != null && dist < 8f,
                                $"forced={forced?.thing?.defName} primary={forcy.equipment?.Primary?.def?.defName} dist={dist:F0}");
                        }),
                        C("cqc-forensics", () =>
                        {
                            Pawn r = Raider();
                            float dist = r?.Position.DistanceTo(forcy.Position) ?? -1f;
                            var forced = CompSidearmMemory.GetMemoryCompForPawn(forcy).ForcedWeapon;
                            CompAmmoUser ru = Carried(forcy, revolver)?.TryGetComp<CompAmmoUser>();
                            return (true, $"raiderJob={r?.CurJobDef?.defName} dist={dist:F0} forcyHp={forcy.health.summaryHealth.SummaryHealthPercent:F2} "
                                + $"forced={forced?.thing?.defName ?? "null"} revMag={ru?.CurMagCount} revHasAmmo={ru?.HasAmmo} "
                                + $"cqcOn={PeteTimesSix.SimpleSidearms.SimpleSidearms.Settings.CQCAutoSwitch}");
                        }, informational: true),
                        C("knife-drawn-on-the-swing", () =>
                        {
                            ThingDef primary = forcy.equipment?.Primary?.def;
                            string carried = string.Join(",", forcy.GetCarriedWeapons(true, true).Select(w => w.def.defName));
                            return (primary == D("MeleeWeapon_Knife"), $"primary={primary?.defName ?? "none"} carried=[{carried}]");
                        }),
                        C("forced-flag-still-set", () => ForcedStill()),
                    }
                },
                new Phase
                {
                    label = "a-loaded-twin-keeps-the-forced-branch-alive",
                    deadlineTicks = 4000,
                    // Convergence C2: dryness is judged for the forced PAIR, and the old
                    // first-instance test let a drained twin speak for a loaded one —
                    // hiding a forced gun SS's own branch would have equipped. Stage
                    // both: twin A drained in hand, twin B loaded in the pack, no
                    // spares. The forced branch must equip the LOADED twin.
                    arrange = () =>
                    {
                        TacticsMod.Settings.forcedDryFallthrough = true;
                        if (Carried(forcy, revolver) == null)
                        {
                            var rec = (ThingWithComps)ThingMaker.MakeThing(revolver);
                            forcy.inventory.innerContainer.TryAdd(rec, false);
                            forcy.TryGetComp<CompInventory>().UpdateInventory();
                            CompSidearmMemory.GetMemoryCompForPawn(forcy)?.InformOfAddedSidearm(rec);
                        }
                        ForceDryRevolver(); // twin A: forced, in hand, drained; spares absent by staging
                        var all = forcy.GetCarriedWeapons(true, true)
                            .Where(w => w.def == revolver).ToList();
                        if (all.Count < 2)
                        {
                            var twin = (ThingWithComps)ThingMaker.MakeThing(revolver);
                            twin.TryGetComp<CompAmmoUser>()?.ResetAmmoCount();
                            forcy.inventory.innerContainer.TryAdd(twin, false);
                            forcy.TryGetComp<CompInventory>().UpdateInventory();
                            CompSidearmMemory.GetMemoryCompForPawn(forcy)?.InformOfAddedSidearm(twin);
                        }
                        else
                        {
                            foreach (var w in all.Where(w => w != forcy.equipment?.Primary))
                            {
                                w.TryGetComp<CompAmmoUser>()?.ResetAmmoCount();
                            }
                        }
                    },
                    mutate = () => { CallEquip(); },
                    checks =
                    {
                        P("dry-twin-in-hand-loaded-twin-in-pack", () =>
                        {
                            var all = forcy.GetCarriedWeapons(true, true)
                                .Where(w => w.def == revolver)
                                .Select(w => w.TryGetComp<CompAmmoUser>()?.CurMagCount ?? -1).ToList();
                            bool ok = all.Count >= 2 && all.Any(m => m == 0) && all.Any(m => m > 0);
                            return (ok, $"revolverMags=[{string.Join(",", all)}]");
                        }),
                        C("forced-branch-stays-alive", () =>
                        {
                            // WHICH twin SS draws is its own MarketValue tie-break (equal
                            // twins → arbitrary) — the pin is that the pair is NOT hidden:
                            // the forced branch runs and a revolver ends up in hand
                            // instead of the fall-through pistol.
                            ThingDef primary = forcy.equipment?.Primary?.def;
                            return (primary == revolver,
                                $"primary={primary?.defName ?? "none"} (fall-through here = the dry twin spoke for the pair)");
                        }),
                    }
                },
                new Phase
                {
                    label = "mid-refill-the-forced-branch-waits",
                    deadlineTicks = 4000,
                    // T3-11: while the forced gun's refill job is literally in flight,
                    // backpack ammo must NOT make it look "not dry" — the forced branch
                    // used to re-equip it at 0 rounds and kill its own refill. All
                    // synchronous: stage, start the refill, poke the preference pass,
                    // observe in the same call.
                    arrange = () =>
                    {
                        TacticsMod.Settings.forcedDryFallthrough = true;
                        // The loaded-twin phase leaves a second pair instance; with a
                        // loaded twin the pair is (correctly) never dry, which is that
                        // phase's point but this one's poison. Keep exactly one.
                        var twins = forcy.GetCarriedWeapons(true, true)
                            .Where(w => w.def == revolver).Skip(1).ToList();
                        foreach (var extra in twins)
                        {
                            extra.Destroy(DestroyMode.Vanish);
                        }
                        if (twins.Count > 0)
                        {
                            forcy.TryGetComp<CompInventory>().UpdateInventory();
                        }
                        CompSidearmMemory mem6 = CompSidearmMemory.GetMemoryCompForPawn(forcy);
                        if (mem6 != null)
                        {
                            // SS's DefaultRanged branch re-equips a preferred gun with no dry
                            // check of its own — that is SS's normal preference behavior, not
                            // the forced branch this phase pins. Clear it for isolation.
                            mem6.DefaultRangedWeapon = null;
                        }
                        // The CQC phase's distress swap throws the revolver on the GROUND —
                        // pick it back up (or mint a fresh one) so this phase stands alone
                        // sequenced as well as isolated.
                        if (Carried(forcy, revolver) == null)
                        {
                            Thing ground = forcy.Map.listerThings.ThingsOfDef(revolver).FirstOrDefault();
                            ThingWithComps rec = ground as ThingWithComps
                                ?? (ThingWithComps)ThingMaker.MakeThing(revolver);
                            if (rec.Spawned)
                            {
                                rec.DeSpawn();
                            }
                            forcy.inventory.innerContainer.TryAdd(rec, false);
                            forcy.TryGetComp<CompInventory>().UpdateInventory();
                            CompSidearmMemory.GetMemoryCompForPawn(forcy)?.InformOfAddedSidearm(rec);
                        }
                        ForceDryRevolver();
                        // fall through to the pistol first
                        CallEquip();
                        // ammo arrives; the refill starts (a reload job for the INVENTORY revolver)
                        ThingWithComps rev = Carried(forcy, revolver);
                        CompAmmoUser user = rev.TryGetComp<CompAmmoUser>();
                        AmmoDef ammo = user.SelectedAmmo ?? user.CurrentAmmo;
                        Thing stack = ThingMaker.MakeThing(ammo);
                        stack.stackCount = user.MagSize * 2;
                        forcy.inventory.innerContainer.TryAdd(stack, false);
                        forcy.TryGetComp<CompInventory>().UpdateInventory();
                        Job job = user.TryMakeReloadJob();
                        if (job != null)
                        {
                            forcy.jobs.StartJob(job, JobCondition.InterruptForced);
                        }
                        midRefillJobInfo = $"jobMade={job != null} curJob={forcy.CurJobDef?.defName} "
                            + $"targetB={(forcy.CurJob?.targetB.Thing as ThingWithComps)?.def?.defName} selAmmo={user.SelectedAmmo?.defName}";
                        // A preference event lands mid-job — the doCQC shape: the MELEE
                        // override, which the core patch's P05 guard deliberately lets
                        // through while a reload runs (a plain Combat pass is blocked, and
                        // pinned P05 rather than this fix in the first version). The
                        // forced branch runs before the mode split either way.
                        WeaponAssingment.equipBestWeaponFromInventoryByPreference(
                            forcy, PeteTimesSix.SimpleSidearms.Utilities.Enums.DroppingModeEnum.InDistress,
                            PeteTimesSix.SimpleSidearms.Utilities.Enums.PrimaryWeaponMode.Melee, null);
                        midRefillPrimary = forcy.equipment?.Primary?.def;
                    },
                    checks =
                    {
                        C("refill-forensics", () => (true, midRefillJobInfo), informational: true),
                        P("refill-actually-in-flight", () =>
                            (midRefillPrimary != null, $"captured={midRefillPrimary?.defName ?? "null"}")),
                        C("empty-forced-gun-not-re-equipped", () =>
                            (midRefillPrimary != revolver,
                             $"primaryAtPreferencePass={midRefillPrimary?.defName ?? "none"} (must not be the 0-round forced gun)")),
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

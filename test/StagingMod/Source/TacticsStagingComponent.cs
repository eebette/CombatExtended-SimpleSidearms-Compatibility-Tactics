using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;
using Verse.AI.Group;

namespace CESSTacticsTestStaging
{
    /// <summary>
    /// Builds the TACT-* staged saves. Only runs with: -quicktest -cetactstage
    /// Saves capture the PRE-mutation state; the assert runner drives settings
    /// flips and feature triggers at load time.
    /// </summary>
    public class TacticsStagingComponent : GameComponent
    {
        private readonly List<Thing> staged = new List<Thing>();
        private IntVec3 anchor = IntVec3.Invalid;

        public TacticsStagingComponent(Game game)
        {
        }

        public override void StartedNewGame()
        {
            if (!GenCommandLine.CommandLineArgPassed("cetactstage"))
            {
                return;
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    StageAll();
                }
                catch (Exception e)
                {
                    Log.Error("[TactStaging] Staging failed: " + e);
                }
            });
        }

        private void StageAll()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                Log.Error("[TactStaging] No current map; launch with -quicktest -cetactstage.");
                return;
            }
            anchor = ComputeAnchor(map);
            Log.Message($"[TactStaging] Map {map.Size}, staging anchor {anchor}.");

            Stage1_ReloadAbort(map);
            SaveAndReset("TACT-1-reload-abort");
            Stage2_ForcedDry(map);
            SaveAndReset("TACT-2-forced-dry");
            Stage3_Tiebreak(map);
            SaveAndReset("TACT-3-tiebreak");
            Stage4_AmmoTarget(map);
            SaveAndReset("TACT-4-ammo-target");
            Stage5_MeleeTarget(map);
            SaveAndReset("TACT-5-melee-target");

            Find.TickManager.Pause();
            Log.Message("[TactStaging] All TACT saves created.");
            Find.LetterStack.ReceiveLetter("TACT saves created",
                "Staged saves written: TACT-1-reload-abort, TACT-2-forced-dry, TACT-3-tiebreak.",
                LetterDefOf.PositiveEvent);
        }

        // Colonist with loaded rifle (+spares) and loaded pistol sidearm (+spares);
        // one melee raider far out (threat source, walks slowly, runner teleports it
        // back between phases).
        private void Stage1_ReloadAbort(Map map)
        {
            Pawn pawn = SpawnColonist(map, "Abort", new IntVec3(-4, 0, 0));
            Equip(pawn, "Gun_AssaultRifle", spareMags: 2);
            GiveSidearm(pawn, "Gun_Autopistol", spareMags: 2);
            SpawnMeleeRaider(map, pawn.Position, distance: 45);
        }

        // Colonist with loaded revolver (NO spares — sole .44 user) and loaded pistol
        // sidearm (+spares). Forced-weapon flag is set by the runner at load.
        private void Stage2_ForcedDry(Map map)
        {
            Pawn pawn = SpawnColonist(map, "Forcy", new IntVec3(0, 0, -4));
            Equip(pawn, "Gun_Revolver", spareMags: 0);
            GiveSidearm(pawn, "Gun_Autopistol", spareMags: 2);
        }

        // Colonist with TWO autopistols (equipped + inventory twin, same def = equal
        // DPS) and a big shared spare stack. Runner drains the equipped twin's mag to
        // create the depth difference.
        private void Stage3_Tiebreak(Map map)
        {
            Pawn pawn = SpawnColonist(map, "Tiedy", new IntVec3(4, 0, 0));
            Equip(pawn, "Gun_Autopistol", spareMags: 4);
            GiveSidearm(pawn, "Gun_Autopistol", spareMags: 0);
        }

        // Rifle (FMJ) + shotgun (buckshot) both loaded; heavily armored mech parked
        // far out. The runner parks it close and compares raw vs target-aware picks.
        private void Stage4_AmmoTarget(Map map)
        {
            Pawn pawn = SpawnColonist(map, "Ammy", new IntVec3(-4, 0, 4));
            Equip(pawn, "Gun_AssaultRifle", spareMags: 1);
            GiveSidearm(pawn, "Gun_PumpShotgun", spareMags: 1);
            SpawnMech(map, pawn.Position, distance: 60);
        }

        // Fast blade + blunt mace sidearms; armored mech parked far. The runner also
        // spawns an unarmored human threat for the flesh-target case.
        private void Stage5_MeleeTarget(Map map)
        {
            Pawn pawn = SpawnColonist(map, "Marcy", new IntVec3(4, 0, 4));
            GiveSidearm(pawn, "MeleeWeapon_Knife", spareMags: 0);
            GiveSidearm(pawn, "MeleeWeapon_Mace", spareMags: 0);
            SpawnMech(map, pawn.Position, distance: 60);
        }

        private void SpawnMech(Map map, IntVec3 around, int distance)
        {
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Mech_CentipedeBlaster")
                               ?? DefDatabase<PawnKindDef>.GetNamedSilentFail("Mech_CentipedeGunner")
                               ?? DefDatabase<PawnKindDef>.GetNamedSilentFail("Mech_Centipede")
                               ?? DefDatabase<PawnKindDef>.GetNamedSilentFail("Mech_Scyther");
            Faction mechs = Faction.OfMechanoids;
            if (kind == null || mechs == null)
            {
                Log.Warning("[TactStaging] No mech kind/faction; skipping mech.");
                return;
            }
            var request = new PawnGenerationRequest(kind, mechs, PawnGenerationContext.NonPlayer,
                          forceGenerateNewPawn: true, canGeneratePawnRelations: false);
            Pawn mech = PawnGenerator.GeneratePawn(request);
            GenSpawn.Spawn(mech, FindCell(map, around + new IntVec3(distance, 0, 0)), map);
            staged.Add(mech);
            LordMaker.MakeNewLord(mechs,
                new LordJob_AssaultColony(mechs, canKidnap: false, canTimeoutOrFlee: false, sappers: false,
                                          useAvoidGridSmart: false, canSteal: false), map, new List<Pawn> { mech });
        }

        // ---- helpers -------------------------------------------------------

        private void SaveAndReset(string name)
        {
            GameDataSaveLoader.SaveGame(name);
            foreach (Thing thing in staged)
            {
                if (thing != null && !thing.Destroyed)
                {
                    thing.Destroy(DestroyMode.Vanish);
                }
            }
            staged.Clear();
            Map map = Find.CurrentMap;
            if (map != null)
            {
                foreach (Lord lord in map.lordManager.lords.Where(l => l.ownedPawns.Count == 0).ToList())
                {
                    map.lordManager.RemoveLord(lord);
                }
            }
        }

        private static IntVec3 ComputeAnchor(Map map)
        {
            bool Valid(IntVec3 c) => c.Standable(map) && !c.Fogged(map);
            if (CellFinder.TryFindRandomCellNear(map.Center, map, 30, Valid, out IntVec3 cell))
            {
                return cell;
            }
            if (CellFinderLoose.TryGetRandomCellWith(Valid, map, 1000, out cell))
            {
                return cell;
            }
            foreach (IntVec3 c in map.AllCells)
            {
                if (c.Standable(map))
                {
                    return c;
                }
            }
            return map.Center;
        }

        private IntVec3 FindCell(Map map, IntVec3 near)
        {
            IntVec3 root = near.ClampInsideMap(map);
            if (CellFinder.TryFindRandomCellNear(root, map, 20, c => c.Standable(map) && !c.Fogged(map), out IntVec3 cell))
            {
                return cell;
            }
            return anchor;
        }

        private Pawn SpawnColonist(Map map, string nick, IntVec3 offset)
        {
            var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                          PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true,
                          canGeneratePawnRelations: false, colonistRelationChanceFactor: 0f);
            Pawn pawn = PawnGenerator.GeneratePawn(request);
            pawn.Name = new NameTriple("Test", nick, "TACT");
            pawn.equipment?.DestroyAllEquipment();
            pawn.inventory?.DestroyAll();
            SkillRecord shooting = pawn.skills?.GetSkill(SkillDefOf.Shooting);
            if (shooting != null)
            {
                shooting.Level = 12;
            }
            GenSpawn.Spawn(pawn, FindCell(map, anchor + offset), map);
            staged.Add(pawn);
            return pawn;
        }

        private ThingWithComps Make(string defName)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                Log.Warning("[TactStaging] Missing def: " + defName);
                return null;
            }
            return (ThingWithComps)ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
        }

        private ThingWithComps Equip(Pawn pawn, string defName, int spareMags)
        {
            ThingWithComps weapon = Make(defName);
            if (weapon == null)
            {
                return null;
            }
            pawn.equipment.AddEquipment(weapon);
            LoadWithAmmo(pawn, weapon, spareMags);
            return weapon;
        }

        private ThingWithComps GiveSidearm(Pawn pawn, string defName, int spareMags)
        {
            ThingWithComps weapon = Make(defName);
            if (weapon == null)
            {
                return null;
            }
            pawn.inventory.innerContainer.TryAdd(weapon, true);
            LoadWithAmmo(pawn, weapon, spareMags);
            CompSidearmMemory.GetMemoryCompForPawn(pawn)?.InformOfAddedSidearm(weapon);
            return weapon;
        }

        private void LoadWithAmmo(Pawn pawn, ThingWithComps weapon, int spareMags)
        {
            CompAmmoUser ammoUser = weapon.TryGetComp<CompAmmoUser>();
            if (ammoUser == null || !ammoUser.UseAmmo)
            {
                return;
            }
            ammoUser.ResetAmmoCount();
            if (spareMags <= 0)
            {
                return;
            }
            AmmoDef ammoDef = ammoUser.CurrentAmmo ?? ammoUser.SelectedAmmo;
            if (ammoDef == null)
            {
                return;
            }
            Thing spare = ThingMaker.MakeThing(ammoDef);
            spare.stackCount = Math.Max(1, ammoUser.MagSize) * spareMags;
            pawn.inventory.innerContainer.TryAdd(spare, true);
        }

        private void SpawnMeleeRaider(Map map, IntVec3 around, int distance)
        {
            Faction pirates = Find.FactionManager.FirstFactionOfDef(FactionDefOf.Pirate);
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Pirate_Gunner")
                               ?? DefDatabase<PawnKindDef>.GetNamedSilentFail("Pirate")
                               ?? PawnKindDefOf.Drifter;
            if (pirates == null)
            {
                Log.Warning("[TactStaging] No pirate faction; skipping raider.");
                return;
            }
            var request = new PawnGenerationRequest(kind, pirates, PawnGenerationContext.NonPlayer,
                          forceGenerateNewPawn: true, canGeneratePawnRelations: false);
            Pawn raider = PawnGenerator.GeneratePawn(request);
            raider.equipment?.DestroyAllEquipment();
            ThingWithComps club = Make("MeleeWeapon_Club");
            if (club != null)
            {
                raider.equipment.AddEquipment(club);
            }
            GenSpawn.Spawn(raider, FindCell(map, around + new IntVec3(distance, 0, 0)), map);
            staged.Add(raider);
            LordMaker.MakeNewLord(pirates,
                new LordJob_AssaultColony(pirates, canKidnap: false, canTimeoutOrFlee: false, sappers: false,
                                          useAvoidGridSmart: false, canSteal: false), map, new List<Pawn> { raider });
        }
    }
}

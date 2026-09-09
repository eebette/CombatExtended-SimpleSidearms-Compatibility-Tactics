# CombatExtended-SimpleSidearms Compatibility Module - Tactics

[![Combat Extended Compatible](Media/Badge_CE_compatible.png)](https://steamcommunity.com/sharedfiles/filedetails/?id=2890901044)
![CE + Simple Sidearms Compatibility Suite](Media/Badge_Suite.png)
![CE + Simple Sidearms Tactics Module](Media/Badge_Tactics.png)

RimWorld mod improving pawn decision-making related specifically to interactions and circumstances that surface as a
result of patching [Combat Extended](https://github.com/CombatExtended-Continued/CombatExtended)
and [Simple Sidearms](https://github.com/PeteTimesSix/SimpleSidearms) using this suite's
[CE + Simple Sidearms Compatibility patch](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Patch).

## Features

* Drafted pawns will abort a reload and switch to their weapon when threatened while reloading.
* Drafted pawns will reload their unequipped sidearms when idle.
* Drafted pawns will consider the enemy's armor type when deciding which melee weapon to use.
* Drafted pawns will consider the enemy's armor and the gun's loaded ammo type when deciding which gun to use.
* Drafted pawns will switch to their sidearm over a forced weapon that doesn't have available ammo.
* Drafted pawns now choose the gun with more available ammo when choosing between otherwise identical guns.

## Load order

> Harmony → Combat Extended → Simple Sidearms → CE+SS Compatibility → this mod.

## My other mods

### The CE + Simple Sidearms suite

Two optional modules sit on top of this patch and require it. This patch only fixes core incompatibilities; enhancements
in these instead.

| Module                                                                                                                                               | What it does                                                      |
|------------------------------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------|
| [![CE + Simple Sidearms Compatibility Patch](Media/Badge_Patch.png)](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Patch)   | Core compatibility patch for Combat Extended and Simple Sidearms. |
| [![CE + Simple Sidearms Loadouts Module](Media/Badge_Loadouts.png)](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Loadouts) | Syncs loadouts between Combat Extended and Simple Sidearms.       |

### Standalone

| Mod                                                                                                                                     | What it does                                                                                                                    |
|-----------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------|
| [![Better Attack Orders for Simple Sidearms](Media/Badge_BAO.png)](https://github.com/eebette/Better-Attack-Orders-for-Simple-Sidearms) | Adds sidearm attack orders to the right-click target menu.                                                                      |
| [![Loadout Quality for Combat Extended](Media/Badge_LQ.png)](https://github.com/eebette/Loadout-Quality-for-Combat-Extended)            | Pawns will upgrade their held guns when a higher-quality copy is available.                                                     |
| [![Universal Patch for More Materials](Media/Badge_UPMM.png)](https://github.com/eebette/Universal-Patch-for-More-Materials)            | Adds materials from [More Materials](https://steamcommunity.com/sharedfiles/filedetails/?id=3055040889) to non-vanilla recipes. |

## FAQ

**CE compatible?**

I'm not answering that.

**Can I add or remove it mid-save?**

Yep.

**Does it change balance?**

It makes the game easier in the sense that 2 core combat mods work better together.

**AI?**

This mod was engineered with the help of an AI Coding Assistant (Claude Code, Fable 5, Max effort). The amount of
researching and deep-diving the compatibility interfaces of mods that it patches would have been insurmountable without
it.

Development followed a standard process driven and scrutinized by me (the real human person writing this):
explore, design, build, test, fix, review, scrutinize, test again over many rounds.

I have manually reviewed and verified all code in this mod.

I ask that if you have unconstructive feedback regarding the usage of AI while developing this mod, that it remains
outside of this community space. Thank you.

## Building

`dotnet build Source/CESSCompatTactics/CESSCompatTactics.csproj -c Release`. References the CE and SS workshop DLLs
(`-p:RimWorldWorkshopDir=...` to override) and — unlike the other suite modules — the **built compatibility patch**
(`-p:CompatPatchDir=...` to override), because this module binds to its public surface (`CompatUtil`, the shared Harmony
id). So build the core patch first. CI cannot build this repo; releases are manual.

## Testing

Automated in-game acceptance tests, run with Combat Extended, Simple Sidearms, and the compatibility patch loaded
(the harness unpatches the sibling Loadouts module so scenarios stay isolated):

```bash
./test/run-tact-assert.sh tact1 TACT-1-reload-abort
```

Six scenarios (pass the name and its save to `run-tact-assert.sh`):

- **tact1** - reload-abort: swap off a reload to a loaded carried weapon when threatened.
- **tact2** - forced-dry fall-through: a forced weapon that is out of ammo falls back to normal selection.
- **tact3** - ammo-depth tiebreak: near-equal guns break to the deeper ammo reserve.
- **tact4** - target-aware ammo scoring: pick by loaded-ammo effectiveness against the target's armor.
- **tact5** - armor-aware melee: blunt vs armor, fast blades vs flesh.
- **tact6** - drafted sidearm top-off: refill empty sidearm magazines during a combat lull.

`run-tact-isolated.sh` runs every phase against a fresh save. Details and recorded passes: [`TESTPLAN.md`](TESTPLAN.md).

## License

[MIT](LICENSE) - code, build files, and docs.

The badge artwork is not: `About/Preview.png` and the `Media/Badge_*.png` set remix the rifle glyph from Combat
Extended's own compatibility badge, so they stay under CE's CC BY-NC-SA 4.0 (attribution, non-commercial, share-alike).
Details in [NOTICE](NOTICE).

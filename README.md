# CombatExtended-SimpleSidearms Compatibility Module - Tactics

[![Combat Extended Compatible](Media/Badge_CE_compatible.png)](https://steamcommunity.com/sharedfiles/filedetails/?id=2890901044)
![CE + Simple Sidearms Compatibility Suite](Media/Badge_Suite.png)
![CE + Simple Sidearms Tactics Module](Media/Badge_Tactics.png)

**Status: placeholder — no code yet.** This README is the complete project brief; an
agent picking this up cold should be able to work from it plus the sibling repos.

## Objective

Opt-in combat-time AI enhancements for the CombatExtended (CE) + SimpleSidearms (SS)
combination. Every feature here addresses a game state that only the *combination*
creates — situations neither upstream designer ever faced ("gaps"), as opposed to one
mod's behavior broken by the other ("bugs", which the core compat patch repairs).
Everything in this module is enhancement, not repair. That is why it is a separate
module, and why **every feature ships opt-in, default OFF**.

## Family

| Mod | Repo | Relationship |
|-----|------|--------------|
| CombatExtended-SimpleSidearms Compatibility Patch (core) | https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Patch | **Required dependency.** 11 repair axes. Reuse its public surface: `CompatUtil.IsCEGun / WeaponHasAmmoFor / CurrentProjectile / SSRemembers`, `HoldSync.EnsureHeld`, and its CE-aware scoring (P02 DPS, P03 ammo-aware selection). |
| … Compatibility Module - Loadouts | https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Loadouts | Sibling, **not** a dependency. |

- Display name: `CombatExtended-SimpleSidearms Compatibility Module - Tactics`
- packageId: `eebette.CESimpleSidearmsCompat.Tactics` (family third-segment convention)
- Dependencies (About.xml): Harmony, Combat Extended, Simple Sidearms, the core compat patch.
- RimWorld 1.6.

## Feature scope — six features, from the 2026-08 gap sweep

1. **Reload-abort when threatened.** While a pawn runs CE's reload job and a hostile
   is targetable, periodically (~30 ticks) call SS's
   `GettersFilters.findBestRangedWeapon(pawn, target)` (already target- and
   ammo-aware via the core patch); if a *different, loaded* carried weapon wins, end
   the reload job cleanly and equip it. Estimated 60–80 lines: one Harmony patch on
   CE's reload JobDriver tick. Rationale: manual gizmo click already aborts reload
   instantly (core axis 5), so the value is off-screen pawns.
2. **Attack-order-time weapon selection.** SS's only in-combat swap trigger is
   aim-warmup ticks, which requires an attack job *with the current weapon* — a pawn
   holding a shotgun with a sniper in inventory cannot even be ORDERED to fire at a
   distant target (float menu shows "Out of range" for the equipped weapon; no job →
   no warmup → swap logic never runs; deadlock). Fix shape: patch the float-menu
   ranged-attack option / attack order to consider all carried weapons and swap
   before the attack job starts. Note: vanilla SS deadlocks identically — see open
   question about upstreaming.
3. **Forced-weapon dry fall-through.** SS's `WeaponAssingment` ForcedWeapon /
   ForcedWeaponWhileDrafted branches run before best-ranged logic with zero ammo
   checks (an impossible state in vanilla — nothing runs dry). Under CE a pawn forced
   onto a dry gun keeps holding it. Fall through to normal selection when truly dry:
   no rounds in magazine AND `CompAmmoUser.HasAmmoOrMagazine == false` (i.e. nothing
   to reload from inventory either). Small fix, but it overrides explicit player
   intent — needs its own toggle.
4. **Ammo-depth tiebreak.** Core axis 3 made selection binary (has ammo / hasn't); a
   gun with 5 loose rounds ranks equal to one with 200 spare. Extend the scoring with
   carried-round depth as a tiebreak. New scoring policy — hence here, not the core.
5. **Ammo-aware joint weapon ranking (DESCOPED 2026-08-18 to the seam sliver).**
   When ranking carried weapons vs a target (the same pathways as features 1/2/4),
   score each candidate by its BEST CARRIED ammo variant for that target instead of
   its currently-loaded one, and guarded-set `SelectedAmmo` on the winner before the
   swap. ~80–120 lines riding the existing selection path.
   OUT OF SCOPE (owner's seam test): situational ammo selection for the equipped
   gun alone ("mech approaching → load EMP") — that is pure CE domain, zero SS
   involvement; parked as a standalone CE enhancement / CE upstream candidate
   (JobGiver_CheckReload already owns change-SelectedAmmo-then-reload machinery,
   optimized for availability; a target-aware version is the natural extension).
   **Verified 2026-08-18 — CE ships NO situational ammo AI**, so this is purely
   additive. Complete list of `SelectedAmmo` writers in CE: the player's
   "Reload with..." UI (`Command_Reload.SetAmmoType`, the only intent-bearing one);
   `JobGiver_CheckReload`'s availability fallback (out of stock → switch to any
   compatible carried ammo — scarcity, not tactics); mech/turret plumbing
   (`MechTakeAmmoCE`, autoloaders, `CompAmmosetSwitcher`). Design consequences:
   (a) CE stores no player-intent flag on SelectedAmmo (spawn default = first ammo
   type), so use the Loadouts module's guarded-write pattern — track what WE last
   set per weapon; overwrite only our own writes or factory state; (b) treat
   JobGiver_CheckReload's scarcity write as not-ours and back off; (c) like CE's
   fallback, never select a variant the pawn isn't carrying.
6. **Armor-aware melee choice.** SS's melee scoring averages damage/penetration;
   under CE's armor system the right pick is target-dependent (blunt vs high-pen
   sharp). Per-target melee selection — likely interacts with the core patch's P06
   (CQC melee axis, `doCQC` path).

Resolved elsewhere / rejected (do not re-add): ammo resupply for remembered sidearms
(became the Loadouts module); SS bulk-based carry sliders (SS-domain settings UI —
upstream suggestion territory, never our code); grenade automation (SS excludes
manual-use weapons BY DESIGN — filling that contradicts author intent); suppression-
reactive swapping (no player expectation); NPC ammo theming (polish).

## Technical context a cold agent needs

- **SS internals** (source: https://github.com/PeteTimesSix/SimpleSidearms):
  `GettersFilters.findBestRangedWeapon` (`isManualUse` = onlyManualCast filter,
  hardcoded skipEMP in `equipBestWeaponFromInventoryByPreference`),
  `WeaponAssingment` (forced branches, `doCQC`, `trySwapToMoreAccurateRangedWeapon`),
  `CompSidearmMemory` (RememberedWeapons as `ThingDefStuffDefPair`), the Stance_Warmup
  Harmony patch requiring `is Verb_Shoot` (core axis 7 handles CE verb types).
- **CE internals** (source: https://github.com/CombatExtended-Continued/CombatExtended):
  reload job driver, `CompAmmoUser` (`CurMagCount`, `MagSize`, `SelectedAmmo`,
  `CurrentAmmo`, `HasAmmoOrMagazine`), `CompInventory`, `Verb_MeleeAttackCE`
  (overrides TryCastShot), `Verb_ShootCEOneUse`.
- **Known third-party interaction:** MeleeAnimation's execution kills bypass CQC
  logic (documented known interaction in the core repo's test plan).

## Build (copy the sibling pattern exactly)

SDK-style net48 csproj as in the sibling repos' `Source/`:
- `Krafs.Rimworld.Ref 1.6.*` (publicized vanilla refs)
- `Lib.Harmony 2.3.3` with `ExcludeAssets=runtime`
- `Krafs.Publicizer` over the local Steam Workshop DLLs:
  - CE: `~/.local/share/Steam/steamapps/workshop/content/294100/2890901044/Assemblies/CombatExtended.dll`
  - SS: `~/.local/share/Steam/steamapps/workshop/content/294100/927155256/v1.6/Assemblies/SimpleSidearms.dll`
  - Core patch: its `Assemblies/CESimpleSidearmsCompat.dll` (build the sibling repo first).
- `Microsoft.NETFramework.ReferenceAssemblies`.
- **No CI is possible**: references live in local Steam dirs; CE is CC BY-NC-SA and SS
  has NO license, so neither can be vendored. Releases are manual local builds.

**Licensing constraint (binding):** never copy code from SS or any decompiled DLL —
behavioral reference only. Same regime as the sibling repos.

## Testing

Reuse the harness pattern from the siblings: isolated test profile
(`core repo/test/run-test.sh`, its `Config/ModsConfig.xml` lists the profile's mods)
plus a staging mod (GameComponent gated on a custom CLI arg via
`GenCommandLine.CommandLineArgPassed`, `-quicktest`, builds staged saves
programmatically). See `test/StagingMod/` in either sibling for working examples,
including the hard-won fixes: anchor computation for unstandable map centers,
LoadoutManager teardown, LordJob for hostile pawns, `canGeneratePawnRelations:false`.

## Design rules (binding, from the suite)

Use "if CE+SS were one mod" thinking ONLY as a lens for spotting seams — never as
license to redesign either mod. Conform to established upstream
opinions/systems/conventions; take ownership of as little as possible; make as few
opinionated decisions as possible. Three tests for any feature: (1) seam test — closes
a gap BETWEEN the mods; (2) switch test — if either mod has a control expressing the
intent, extend that control, never a parallel toggle or different default polarity;
(3) ownership test — stateless derivation over owned state, guarded writes over
stomps, additive UI over replaced UI, opt-in default-off unless an upstream
convention licenses the default. For THIS module: everything is by definition new
policy, so everything is opt-in default-off, full stop.

## Open questions (decide before/while building)

1. Feature 2 fixes a deadlock that exists in *vanilla* SS too — file it upstream as
   an SS issue/PR instead of (or before) patching here? Upstream-first matches the
   suite ethos. (SS has no license, which complicates PRs — issue with repro may be
   the right vehicle; see core repo issue #5 for the outreach tracking pattern.)
2. Settings surface: master enable + per-feature toggles (all default off) is the
   assumed shape — confirm. Thresholds needing definition: reload-abort threat
   distance, swap hysteresis (prevent A↔B oscillation when scores are close).
3. Reload-abort: after the threat clears, resume the aborted reload automatically or
   leave it to CE's normal loadout/reload flow?
4. Feature 5: drafted-only, or also undrafted defensive fire?
5. Feature 6: implement inside/alongside core P06's CQC path or as an independent
   selection patch?
6. Ship order proposal: 1+2 first (highest player-visible value), then 3+4 (small),
   then 5+6 (hardest). Confirm.
7. Versioning/save-compat: follow the suite policy being defined in core repo
   issue #4.

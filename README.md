# CombatExtended-SimpleSidearms Compatibility Module - Tactics

[![Combat Extended Compatible](Media/Badge_CE_compatible.png)](https://steamcommunity.com/sharedfiles/filedetails/?id=2890901044)
![CE + Simple Sidearms Compatibility Suite](Media/Badge_Suite.png)
![CE + Simple Sidearms Tactics Module](Media/Badge_Tactics.png)

**Status: ALL FIVE features implemented and machine-verified 2026-08-18
(15/15 automated phases green — see TESTPLAN.md). Remaining: owner feel-testing
+ the suite release train.** This README is the
project brief; an agent picking this up cold should work from it plus the
sibling repos.

Defaults chosen at scaffold (were open questions): settings = per-feature
toggles all OFF plus a tiebreak-margin slider (no master switch — three toggles
don't need one); reload-abort leaves the abandoned reload to CE's natural idle
reload flow (no resume bookkeeping); feature 1 is a 30-tick GameComponent scan
over currently-reloading pawns rather than a reload-driver Harmony patch
(fewer patched members, same behavior).

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

## Feature scope — five features, from the 2026-08 gap sweep
(numbering kept for history; #2 was descoped to a standalone mod)

1. **Reload-abort when threatened.** While a pawn runs CE's reload job and a hostile
   is targetable, periodically (~30 ticks) call SS's
   `GettersFilters.findBestRangedWeapon(pawn, target)` (already target- and
   ammo-aware via the core patch); if a *different, loaded* carried weapon wins, end
   the reload job cleanly and equip it. Estimated 60–80 lines: one Harmony patch on
   CE's reload JobDriver tick. Rationale: manual gizmo click already aborts reload
   instantly (core axis 5), so the value is off-screen pawns.
   SPEC REFINEMENT (2026-08-18, found by the tact1 harness): findBestRangedWeapon
   cannot drive the abort — with core axis 3 it counts reloadable-from-inventory
   weapons as viable, i.e. the gun being reloaded, so it always returns the
   primary. Mid-reload the comparison is "loaded THIS INSTANT": scan loaded
   secondaries directly (mag>0, or HasAmmo for magazine-less), score with
   StatCalculator.RangedDPS at target distance, equip the specific winner via
   equipSpecificWeaponFromInventory (equipBest would re-pick the reloadable
   primary and loop).
   Seam audit 2026-08-18: HOLDS — mid-reload is a CE-only state, arsenal-swap is
   SS-only capability; meaningless without both. GUARD: never abort a
   player-forced reload job (`job.playerForced`) — explicit orders are untouchable.
2. **Attack-order-time weapon selection.** SS's only in-combat swap trigger is
   aim-warmup ticks, which requires an attack job *with the current weapon* — a pawn
   holding a shotgun with a sniper in inventory cannot even be ORDERED to fire at a
   distant target (float menu shows "Out of range" for the equipped weapon; no job →
   no warmup → swap logic never runs; deadlock). Fix shape: patch the float-menu
   ranged-attack option / attack order to consider all carried weapons and swap
   before the attack job starts.
   **DESCOPED 2026-08-18 (owner's call, via the seam audit): now a STANDALONE
   MOD** — the deadlock exists in vanilla SS, so it is SS-domain repair, not a
   CE+SS seam. Moved to
   https://github.com/eebette/Better-Attack-Orders-for-Simple-Sidearms
   (Harmony+SS only; gains CE-awareness automatically via the core patch's
   Harmony patches when the suite is present — zero coupling). Upstream-first SS
   issue remains that repo's step one. Features 1/4/5 still reference
   attack-order-time as a selection pathway — when built, this module hooks the
   standalone mod's moment if present, or ships without it.
3. **Forced-weapon dry fall-through.** SS's `WeaponAssingment` ForcedWeapon /
   ForcedWeaponWhileDrafted branches run before best-ranged logic with zero ammo
   checks (an impossible state in vanilla — nothing runs dry). Under CE a pawn forced
   onto a dry gun keeps holding it. Fall through to normal selection when truly dry:
   no rounds in magazine AND `CompAmmoUser.HasAmmoOrMagazine == false` (i.e. nothing
   to reload from inventory either). Small fix, but it overrides explicit player
   intent — needs its own toggle.
   Seam audit 2026-08-18: HOLDS — forced = SS control, dry = CE-only state. GUARD:
   BYPASS, NEVER CLEAR — the ForcedWeapon flag is SS-owned player state; skip it in
   selection while truly dry, and it resumes the moment ammo exists again.
4. **Ammo-depth tiebreak.** Core axis 3 made selection binary (has ammo / hasn't); a
   gun with 5 loose rounds ranks equal to one with 200 spare. Extend the scoring with
   carried-round depth as a tiebreak. New scoring policy — hence here, not the core.
   Seam audit 2026-08-18: HOLDS — depth is a CE concept, ranking is SS's. GUARD:
   STRICT tiebreak only — applies when candidates are within an epsilon on the
   primary DPS ranking; as a general weighting it would redesign SS scoring.
5. **Target-aware ranking of loaded ammo (settled 2026-08-18 after two descopes).**
   When ranking carried weapons vs a target (same pathways as features 1/2/4), value
   each candidate's CURRENTLY-LOADED ammo against the actual target — penetration vs
   the target's armor, EMP effectiveness vs mechs — instead of the single generic DPS
   number. The core patch's P02 already scores by the loaded variant's damage, so
   this is a target-effectiveness term on top of the existing scoring path. NO ammo
   switching, NO SelectedAmmo writes (owner: scoring best-carried-but-not-loaded
   ammo is strictly worse — the pawn would switch guns and shoot the worse round).
   ~50–80 lines.
   OUT OF SCOPE (owner's seam test): situational ammo selection for the equipped
   gun ("mech approaching → load EMP") — pure CE domain, zero SS involvement; parked
   as a standalone CE enhancement / CE upstream candidate (JobGiver_CheckReload
   already owns change-SelectedAmmo-then-reload machinery, optimized for
   availability; a target-aware version is the natural extension).
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
   Seam audit 2026-08-18: HOLDS — inputs are CE `ToolCE` penetration stats and the
   cost of a wrong pick is CE's armor model. GUARDS: target from the provenance
   rules only (never invent melee target choice); extend P06's path, don't fork.

**Target provenance (settled 2026-08-18) — "actual target" is never invented here:**
(a) explicit player order target (attack orders; the moment now owned by the
standalone Better Attack Orders mod when present); (b) the stance/job focus target
(warmup swap, CQC attacker) — already flowing through SS pathways; (c) when no
target is in hand (reload-abort), vanilla `AttackTargetFinder.BestAttackTarget`
supplies it — and its non-null result IS the "threatened" trigger condition, so
trigger and target are one computation. Finder semantics are inherited wholesale
(distance/LOS/threat weighting, fleeing handling via its scan flags — no pursuit
policy of ours). Range is evaluated per CANDIDATE weapon at the target's distance
(SS's candidate filter + core P02 distance DPS already do this); if every carried
weapon is out of range, no swap. No target anywhere → features don't fire and
scoring stays in SS's existing target-less mode (`findBestRangedWeapon`'s target
parameter is already nullable with defined behavior).

**Explicit non-goal — target PRIORITIZATION.** Which enemy to fight first (e.g.
near pistol raider vs far sniper-range raider) is vanilla AI / player-micro /
dedicated-AI-mod territory (CAI 5000 et al.), never this module's. We select
weapons FOR the chosen target; we never choose or re-rank targets. The suite's
existing protections at each stage: proximity-weighted vanilla finder re-picks as
threats close; axis-7 warmup swap re-arms for the new target; axis-6 CQC covers
arrival in melee. The approach-window risk is vanilla combat, unchanged by us.

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

## Sizing (2026-08-18, post-descopes)

Feature 1 ~60–80 lines; 3 ~30–50; 4 ~40–60; 5 ~50–80; 6 ~100–150. Plus staging
scenarios + assert runner (the expensive part for combat-timing features — budget
as much as the features themselves). Roughly 1.5–2 sessions to feature-complete
with automated tests.

## Open questions (decide before/while building)

1. Settings surface: master enable + per-feature toggles (all default off) is the
   assumed shape — confirm. Thresholds needing definition: reload-abort swap
   hysteresis (prevent A↔B oscillation when scores are close); feature 4's
   tiebreak epsilon.
2. Reload-abort: after the threat clears, resume the aborted reload automatically or
   leave it to CE's normal loadout/reload flow?
3. Feature 6: implement inside/alongside core P06's CQC path or as an independent
   selection patch?
4. Ship order proposal: v0.1 = 1+3+4 (small, highest value), v0.2 = 5+6 (the two
   target-effectiveness scoring terms — natural pair sharing machinery). Confirm.
5. Versioning/save-compat: the suite policy is settled in the core repo's
   RELEASING.md (issue #4, closed) — inherit it; this module is stateless, so both
   add/remove-mid-save guarantees should hold trivially. Verify at release.

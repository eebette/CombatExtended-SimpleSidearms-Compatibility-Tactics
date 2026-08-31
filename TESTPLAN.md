# Test plan — Tactics module

Automated end-to-end suite on the shared harness pattern; the core compat repo's
`TESTPLAN.md` is the canonical machinery reference (phase model, deferred
mutate, preconditions, negatives, expected diagnostics, iso mode, A/B
discipline). This file records what is Tactics-specific.

## Commands

```
./test/run-tact-stage.sh                            # regenerate TACT saves (quit after letter)
./test/run-tact-assert.sh tact1 TACT-1-reload-abort # sequenced, one scenario
./test/run-tact-isolated.sh tact1 TACT-1-reload-abort   # every phase alone vs fresh save
./test/verify-regression.sh tact1 <git-ref>         # A/B a scenario against another ref
```

Saves and results live in the SHARED profile — the compat repo's
`test/SaveData/` — so the whole suite family runs one RimWorld install with one
mod list. Scenario→save map: tact1→TACT-1-reload-abort, tact2→TACT-2-forced-dry,
tact3→TACT-3-tiebreak, tact4→TACT-4-ammo-target, tact5→TACT-5-melee-target.

## Machinery (Tactics-specific)

- **Phase 0 census** in every scenario: reflection sweep of
  `Harmony.GetAllPatchedMethods()` for owner `eebette.CESimpleSidearmsCompat.Tactics`
  — ≥ 4 patched methods (F03 ×2, F04, F06; F01 is a GameComponent and has no
  census line) — plus a startup-error sweep of the baselined log.
- **Module isolation**: the runner disables the Loadouts module by reflection
  (`CESimpleSidearmsCompat.Loadouts.LoadoutsMod`) and asserts BOTH sentinels —
  packageId absent from the running mod list and no Harmony owner — erroring
  loudly on contamination instead of skipping quietly. The core compat patch
  stays LOADED: Tactics declares it a dependency, and its P02/P05/P12 behavior
  is part of the environment under test (see the ledger below).
- **Settings reset**: every scenario starts with all Tactics toggles OFF;
  each phase that needs a toggle enables it ITSELF (arrange or mutate), so
  isolated runs test what the label claims.

## Green pass — 2026-08-31 (v0.1 features under T1 doctrine)

19 phases (14 behavioral + 5 census), sequenced AND isolated:

- **tact1 (reload-abort, F01)**: feature-off reload completes; feature-on
  mid-reload swap to the loaded pistol with a hostile INSIDE the pistol's CE
  range; player-forced reload completes with the feature on and a viable swap
  in range (the forced gate is the only thing letting it finish).
- **tact2 (forced-dry, F03)**: feature-off forced branch holds a dry revolver;
  feature-on falls through to the loaded pistol with the ForcedWeapon flag
  intact; ammo back → forced revolver resumes from the fallen-through state.
- **tact3 (ammo-depth tiebreak, F04)**: equal-DPS twins resolve to the
  deeper-magazine twin; a clearly better rifle with 1 round still wins
  (the tie window stays subordinate to DPS).
- **tact4 (target-aware ammo scoring, F05 core)**: at 8 cells the buckshot
  shotgun raw-wins; against mech plate the multipliers flip the pick to the
  rifle. No SelectedAmmo writes anywhere.
- **tact5 (armor-aware melee, F06)**: fast blade vs flesh; blunt mace vs the
  mech via differentiated penetration floors.

## Ledger — what the harness caught

1. **v0.1's first real bug (F01 design)**: asking SS's `findBestRangedWeapon`
   mid-reload counts reloadable-from-inventory weapons as viable — i.e. the
   very gun being reloaded. Mid-reload the comparison must be "loaded THIS
   INSTANT": scan loaded secondaries directly, equip the specific winner.
2. **Uniform penetration floor erased blunt (F06)**: CE's own numbers say a
   mace can't crack centipede plate either; differentiated floors (sharp 0.10,
   blunt 0.40) reflect that under-penetrating sharp deflects while blunt
   transfers trauma through. Also fixed: `Raider()` matching any hostile had
   silently substituted the centipede for the "flesh" target.
3. **Core-patch P02 interplay (T1's headline)**: the tact1 swap phases parked
   the raider at 40 tiles and historically passed — only through SS's
   squared-distance range-gate bug, which made a 16-range autopistol look
   viable at 40. The core patch's corrected `RangedDPS` scores out-of-range
   weapons 0, so F01 now (correctly) declines the swap and finishes the
   reload when no secondary reaches the threat. Phases re-staged inside
   pistol range; the decline-at-distance behavior is a FEATURE, not a bug.
   Lesson: a downstream module's tests inherit every correctness fix the
   dependency ships — a "regression" here can be the dependency getting
   *righter*. Diagnose against the dependency's changelog before touching
   feature code.
4. **Stale-save ammo budgets**: TACT-1 stages exactly 60 rounds of 5.56 — two
   magazines, consumed by phases 1 and 3, so a phase-2 reload that wrongly
   COMPLETES starves phase 3 into a cascade failure. Deliberately left tight:
   the budget itself pins "phase 2 must abort, not complete".
5. **Isolated runs exposed hidden phase coupling**: tact2/tact3 later phases
   inherited forced flags, magazine drains, and feature toggles from earlier
   phases' mutates; tact1/tact5's final phases "passed" isolated with their
   feature OFF (vacuously — tact5's mace pick can even land via the core
   patch's P12 alone). Every phase now re-establishes its own state in
   `arrange` (idempotent when sequenced) behind prerequisites-only
   preconditions, and enables its own toggle.

## A/B spot-proofs

Every failing-capable scenario proven able to fail (scratch sed-toggles, then
reverted; script pattern in the compat repo's TESTPLAN):

- **F01 neutered** (component tick short-circuited): tact1
  `abort-swaps-to-loaded-pistol` red — the raider reaches the still-reloading
  colonist, who ends the phase in FleeAndCower with the rifle in hand — while
  the census stays GREEN, demonstrating the census alone cannot vouch for
  component features.
- **F03 Prepare→false**: tact2 census red (3 < 4 methods) AND
  `on-falls-through-to-pistol` red — one run, both detection layers.

## Harness ops notes

- The staging mod needs BOTH its Mods-dir symlink (`CESSTacticsTestStaging`) and
  its `ModsConfig.xml` entry; a boot with the entry but no folder makes RimWorld
  prune the entry, and a duplicated entry gets dropped entirely — both silently.
- Native-binary launches require windowed player prefs
  (`~/.config/unity3d/.../prefs`: Fullscreen mode 3, fixed resolution). Exclusive
  or native-res fullscreen intermittently crashes on Xwayland with an
  XF86VidMode BadValue when the desktop's mode list shifts (e.g. a Proton
  session is running). The owner's Steam launches use Proton and are unaffected.
- After any killed mid-load run, CHECK THE MOD LIST FIRST: RimWorld's crash
  guard silently resets the shared profile's ModsConfig.xml to Core-only.

Manual residue: feel-testing the abort cadence in real combat.

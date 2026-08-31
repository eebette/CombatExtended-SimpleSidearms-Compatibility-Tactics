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
  — ≥ 7 patched methods (F03 ×2, F04's selection scope ×3, F06, F07; F01 is a
  GameComponent and has no census line) — plus a startup-error sweep of the
  baselined log.
- **Module isolation**: the runner disables the Loadouts module by reflection
  (`CESimpleSidearmsCompat.Loadouts.LoadoutsMod`) and asserts BOTH sentinels —
  packageId absent from the running mod list and no Harmony owner — erroring
  loudly on contamination instead of skipping quietly. The core compat patch
  stays LOADED: Tactics declares it a dependency, and its P02/P05/P12 behavior
  is part of the environment under test (see the ledger below).
- **Settings reset**: every scenario starts with all Tactics toggles OFF;
  each phase that needs a toggle enables it ITSELF (arrange or mutate), so
  isolated runs test what the label claims.

## Green pass — 2026-08-31 (T2: CE-true target scoring; F07 + the F04 rework)

24 phases (18 behavioral + 6 census), sequenced AND isolated:

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
  shotgun raw-wins; a scyther's armor (which the rifle penetrates and buckshot
  does not) flips the pick to the rifle; centipede plate zeroes EVERY
  multiplier and the feature stands down — SS's raw pick survives untouched
  (the zero-defer branch). No SelectedAmmo writes anywhere.
- **tact5 (armor-aware melee, F06)**: knife beats mace vs flesh (the de-biased
  CE damage-per-second, the feature's headline flip); vs centipede plate every
  through-armor fraction is zero and F06 defers to SS's own P12-backed pick
  (the mace as least-bad).
- **tact6 (drafted sidearm top-off, F07 — shares TACT-1's save)**: feature-off
  a drafted pawn's empty sidearm stays empty through a quiet lull (invariant
  held across the window); feature-on the lull fills it through CE's own job
  flow; a hostile inside CE's safe distance blocks it — staged with a DOWNED
  raider on purpose, because CE's predicate counts downed hostiles and a downed
  raider cannot beat up the defenseless subject during the negative window.

## Ledger — what the harness caught

1. **v0.1's first real bug (F01 design)**: asking SS's `findBestRangedWeapon`
   mid-reload counts reloadable-from-inventory weapons as viable — i.e. the
   very gun being reloaded. Mid-reload the comparison must be "loaded THIS
   INSTANT": scan loaded secondaries directly, equip the specific winner.
2. **Uniform penetration floor erased blunt (F06, v0.1 — superseded)**: the
   first catch was a uniform floor flattening sharp and blunt into the same
   bottom; the differentiated floors that fixed it were themselves invented
   constants and are gone in T2 (see 5 — the deflect-to-blunt conversion now
   carries that asymmetry for real). Still standing from that round:
   `Raider()` matching any hostile had silently substituted the centipede for
   the "flesh" target.
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
5. **The T2 rewrite dropped every invented constant.** TargetScoring's first
   version scored matchups with a clamp(pen/armor) curve, hand-tuned floors
   (sharp 0.10 / blunt 0.40) and EMP constants (2.5x / 0.2x) — fiction. It now
   reproduces ArmorUtilityCE.TryPenetrateArmor in expectation form: damage
   through armor is dmg x clamp01(1 − armor/pen); an under-penetrating sharp
   hit deflects into a blunt hit of cbrt(bluntPen x 10000)/10 damage against
   blunt armor (GetDeflectDamageInfo); zero-pen damage passes whole; damage
   that cannot harm health (EMP stun) leaves BOTH sides of the ratio — a stun
   has no derivable damage exchange rate, so it is not scored and SS's own EMP
   mode filters keep governing EMP picks. Secondaries (ion rounds' ballistic
   core) are summed under CE's own penetration hand-off rules.
6. **F06's factorization matters.** SS's biased melee score is
   (dmg/biasedSpeed) x (1 + penetration) — the (1+pen) term is a GENERIC armor
   bonus paid against every target, so multiplying a target factor on top of it
   still let the mace beat the knife vs bare flesh. F06 divides the (1+pen)
   term out (StatCalculator.MeleePenetration, the same P12-backed input SS
   used, so the division is exact) and substitutes the actual through-armor
   fraction. Vs flesh that leaves pure CE dps — the knife's speed wins.
7. **All-zero means stand down, not re-rank.** Against centipede plate every
   candidate's multiplier is genuinely zero under CE's model (a 5.6 MPa mace
   head vs 45 MPa plate does nothing — the old floors manufactured a winner).
   Both F04 and F06 now defer to SS's own pick when no candidate scores
   positive: re-ranking zeros is noise. Pinned by tact4's
   `on-hopeless-armor-defers-to-raw` and tact5's `on-vs-armor-picks-blunt`.
8. **The knife-vs-flesh red unmasked a CORE-PATCH gap (now P13).** SS's melee
   damage/speed inputs read vanilla accessors that multiply each tool by the
   attacker's part efficiency in the tool's linkedBodyPartsGroup — and CE's
   groups are weapon anatomy (Blade/Point), which no human has. Every CE blade
   scored as its handle. Fixed in the core patch (axis 13), pinned there by
   `ce-melee-damage-signal`; this suite's tact5 covers the consuming side.
9. **F04's first shape was the transcription the rules ban (reworked).** Its
   postfix re-enumerated candidates through a hand-copied filter chain that had
   already drifted from SS's real one — it missed the biocode check, the
   VFE-shield and Tacticowl exclusions, and the per-weapon min/max range
   window, so the re-rank could crown a weapon SS itself refused. The rework
   composes two proven core-patch patterns instead: a call-lifetime SCOPE
   opened by a prefix on findBestRangedWeapon (P01's pattern, closed by a
   finalizer), and postfixes on the scoring entry points SS calls inside it
   (RangedDPS / RangedDPSAverage) that adjust the outgoing score by the target
   factor and RECORD every (weapon, raw, adjusted) pair. SS's own loop,
   filters, and comparison pick natively; the tiebreak and the all-zero defer
   read the records. Nothing enumerates twice; a new SS filter is inherited
   automatically. P02's cache is untouched (it stores in its prefix, before
   the postfix rewrites the one outgoing value).
10. **The N() convention is the INVARIANT, not the trip.** A negative check's
   eval returns true while the world stays good; the first false trips the
   phase. tact6's first draft returned the trip condition and the phase failed
   with the world perfectly fine — the helper's doc comment now states the
   convention so the next phase author doesn't rediscover it.
11. **Isolated runs exposed hidden phase coupling**: tact2/tact3 later phases
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
- **Core P13 Prepare→false** (cross-repo): compat census red (22 < 23),
  `ce-melee-damage-signal` red, AND tact5's knife-vs-flesh red — the module's
  phases detect a regression in the dependency they consume.
- **F04 zero-defer removed** (pre-rework note, kept for history): came back
  GREEN — with the branch gone, MaxBy over all-zero scores returned the first
  candidate, coincidentally the raw pick. Under the rework the defer lives in
  the selection postfix reading recorded pairs; the scope A/B below covers it.
- **F04 selection scope Prepare→false** (the rework's leg): census red (6 < 7)
  in BOTH scenarios and tact3's deeper-twin red — the deterministic behavioral
  proof. tact4's flip phase varies under the scratch (observed red once, and
  once a chaos-latched green: the phase drags red, the live scyther charges,
  and mid-brawl weapon churn can transiently flap the pick to the rifle, which
  the latching positive catches). The scenario still fails through the census
  both times; the defer phase's A-leg green is the OFF reason itself (no
  multipliers → SS's raw shotgun pick is exactly what it expects).
- **F07 Prepare→false**: census red (6 < 7) AND tact6's lull top-off red — the
  sidearm stays empty through the lull with the patch gone.
- **F06 de-bias removed**: tact5 `on-vs-flesh-picks-blade` red (the generic
  (1+pen) bonus hands flesh back to the mace).

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

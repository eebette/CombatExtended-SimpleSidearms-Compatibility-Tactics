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

## Green pass — 2026-09-02 (T5: second confirmation round, converged)

T5 (section below) found zero HIGHs — the T4 core is clean — and closed a
ring of edge seams around the T4-2 marker (install fail-open, gizmo
re-click, save/load stamps, under-barrel switches), the F04↔P03 postfix
ordering, and two TargetScoring model-fidelity gaps. 34 phases sequenced AND
isolated green; the T5-C A-leg ran red. Census is now ≥14 patched methods
(the two `CompUnderBarrel` switch markers).

## Green pass — 2026-09-02 (T4: confirmation round fixed and pinned)

34 phases (28 behavioral + 6 census), sequenced AND isolated — full board
green 2026-09-02 after the T4 confirmation round (section below) added
`a-ran-dry-auto-reload-is-abortable` (tact1) and
`abort-declines-vs-hopeless-armor` (tact4) and both HIGH A/B legs ran red.
Census is now ≥12 patched methods (`SyncedTryStartReload` marker).

## Green pass — 2026-09-01 (T3: adversarial round fixed and pinned)

32 phases (26 behavioral + 6 census), sequenced AND isolated — full board
green 2026-09-01 (the final sweeps survived a Vulkan driver wedge, a reboot,
a crash-guard modlist wipe from racing Steam's startup, and one
self-inflicted double-instance collision; ops notes below). The T3
adversarial round (three independent reviewers over the full source +
decompiles) found the headline target-aware features DEAD OR SUPPRESSED in
real play behind a green suite — every fix below is pinned by a phase driven
through the REAL entry point (doCQC, warmup auto-switch, CE's own top-off),
not a direct API call:

- **tact1 (reload-abort, F01)**: feature-off reload completes; feature-on
  mid-reload swap to the loaded pistol with a hostile INSIDE the pistol's CE
  range — now staged past a loaded BIOCODED rifle the winner scan must skip
  (T3-5); player-forced reload completes with the feature on and a viable
  swap in range; and CE's own undrafted backpack top-off completes with a
  hostile visible in the old trigger band while the primary never moves
  (T3-1 — unfixed, F01 killed the top-off and juggled weapons forever).
- **tact2 (forced-dry, F03)**: feature-off forced branch holds a dry revolver;
  feature-on falls through to the loaded pistol with the ForcedWeapon flag
  intact; ammo back → forced revolver resumes from the fallen-through state;
  a REAL adjacent swing (doCQC through CE's melee verb) draws the knife past
  the truly-dry forced gun with the flag surviving (T3-6); and while the
  forced gun's refill job is in flight the forced branch waits instead of
  re-equipping it at zero rounds (T3-11).
- **tact3 (ammo-depth tiebreak, F04)**: equal-DPS twins resolve to the
  deeper-magazine twin; a clearly better rifle with 1 round still wins
  (the tie window stays subordinate to DPS).
- **tact4 (target-aware ammo scoring, F05 core)**: at 8 cells the buckshot
  shotgun raw-wins; a scyther's armor flips the pick to the rifle; centipede
  plate zeroes EVERY multiplier and the feature stands down; the stand-down
  never resurrects a DRY gun (T3-4 — a drained raw-best shotgun loses to the
  loaded rifle); and the REAL warmup auto-switch draws the rifle mid-aim
  against the scyther (T3-3 — the old adjusted-vs-raw comparison never
  swapped outside the harness). No SelectedAmmo writes anywhere.
- **tact5 (armor-aware melee, F06)**: knife beats mace vs flesh and the mace
  stands vs centipede plate — both now driven through SS's preference tree
  with the target (the scope's real entry), because a bare findBestMeleeWeapon
  call is exactly the dead wiring T3-2 fixed; plus the full chain: a REAL
  adjacent swing triggers doCQC and the knife comes out of the pawn's pocket.
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

12. **T3 (2026-09-01), the adversarial round's own ledger.** Three independent
   attackers (state/lifecycle, cross-patch interplay, player-perspective)
   converged on the same meta-finding: the suite tested SS's selection APIs
   in shapes the game never calls, which let two headline features hide dead
   or suppressed behind green phases. Fixed and pinned:
   - **T3-1** F01 identified "reloading" with "the equipped gun is empty" —
     CE's undrafted top-offs and F07's drafted top-offs falsified it; the
     abort now touches only reloads of the gun in hand.
   - **T3-2** F06's target parameter was dead wiring (SS never forwards the
     CQC target into findBestMeleeWeapon); reworked to the F04 scope pattern
     on the preference method — which also fixed the cancelled speed bias
     (averageSpeed:0) and the copied-filter drift in one move.
   - **T3-3** trySwapToMoreAccurateRangedWeapon compared the challenger's
     ADJUSTED score to the incumbent's RAW score (scored after the scope
     closed) — the promote direction could never fire; the scope now spans
     the whole caller.
   - **T3-4** the all-hopeless defer resurrected DRY guns (records carry
     paper scores; P03's correction had already run); the defer now filters
     to guns with rounds.
   - **T3-5** F01's winner scan lacked SS's usability rule (biocode/bond/
     role) and ignored the forced flag — both added, filtered at candidacy.
   - **T3-6** the fall-through never covered the melee-attacked reflex (SS's
     forced check sits a call above the patched method); a second hide with
     the same always-restore discipline covers tryCQCWeaponSwapToMelee.
   - **T3-11** a refill-in-flight made the forced gun look "not dry" and the
     forced branch re-equipped it at 0 rounds, killing its own refill; the
     dryness test now counts an in-flight reload of that gun as still-dry.
   - Accepted + documented: **T3-7** (a drafted melee-primary pawn topping
     off a sidearm eats a charger's approach plus one hit before reacting —
     CE's own drafted-reload risk model extended; proactive melee draw filed
     as issue #4/F08 candidate) and **T3-9** (CE's reload driver kills ANY
     reload on a primary change, backpack ones included — the compat P05
     comment now states it; the killed top-off re-issues next lull, no rounds
     lost). **T3-8** (compat): AskSS now refuses, loudly, when its halting
     prefix failed to install — upstream drift can no longer turn the
     "hypothetical" preference question into real equips. **T3-10**: F01
     serves every loaded map, not just the watched one.
   - Iso-vs-sequenced weapon state: the hopeless-warmup phase equips the
     RIFLE unconditionally — isolated runs load with the save's shotgun in
     hand, whose ~16 range never reaches the 45-cell target (the pawn stood
     Mobile forever); sequenced only worked because the warmup phase had
     already swapped. DamageUntilDowned often CANNOT down a mech (ends alive
     at ~8% hp) — distance outside the blaster's reach is the real shield.
     ParkWithLOS walks a 16-bearing ring because ParkPawnNear places due
     EAST only, and a whole east line can be LOS-blocked from some anchors.
   - Staging lessons the round forced: CQC phases drive the swing THROUGH
     THE REAL VERB (raider.meleeVerbs.TryMeleeAttack each poll — a free
     raider re-targets other colonists, and even a forced attack job loses
     the swing race to the deadline ~50% of isolated runs); SS's InDistress
     swap throws the replaced weapon on the GROUND, so later phases recover
     it; SS's DefaultRanged branch re-equips preferred guns with no dry
     check of its own — phases pinning the FORCED branch must clear
     DefaultRangedWeapon; a wandering mech drifts its dry challenger out of
     the range window and turns an A-leg vacuous (re-park via poll); and
     the warmup phase stages in mutate behind world-is-ticking, because
     tick-0 arranges lie (core suite lesson, relearned).

## Convergence round (2026-09-01, post-T3 adversarial re-pass, no diff bias)

Two fresh attackers over the CURRENT state — one full-repo, one mechanical
re-pass on the seams the T3 fixes introduced. Both independently found C1.

- **C1 (Critical)**: the all-hopeless defer wrote the winner's RAW score into
  the returned tuple, while trySwap's incumbent (in-scope) scored ~0 — a
  raw-vs-zero comparison that "swapped" to the already-equipped gun every
  warmup, reset the attack job, and froze the pawn mid-aim forever against
  hopeless armor, flooding the log with SS's already-equipped warning. Fix:
  the defer returns the ADJUSTED (zero) score — the preference path reads
  only the weapon, and trySwap then stands down, which is the defer's own
  philosophy applied to the swap decision. Pinned by
  `warmup-vs-hopeless-armor-still-fires` (a REAL drafted attack on a downed
  centipede; the A-leg goes red through the diagnostics machinery alone —
  the warning flood is on no allowlist).
- **C2**: pair-level dryness judged by the first carried instance — a drained
  twin spoke for a loaded one (hiding a forced gun SS could equip) and the
  refill-in-flight clause compared the wrong copy. Fix: aggregate over every
  carried instance. Pinned by `a-loaded-twin-keeps-the-forced-branch-alive`
  (which twin SS then draws is its own MarketValue tie — the pin is that the
  pair is not hidden).
- **C3 (owner ruling: no mirrored filters)**: F01's winner scan was still a
  hand-copied filter chain (missing VFE-shield and Tacticowl exclusions —
  the drift disease half-cured). Fix: DELETE the enumeration; the winner now
  comes from SS's own findBestRangedWeapon with a call-scoped
  loaded-this-instant filter on GetCarriedWeapons (P03's own pattern) — SS's
  full chain and F04's target-aware scoring inherited, census 11. Pinned by
  a staged DRY-with-spares decoy rifle the scope must hide (A-leg equips the
  empty decoy).
- **C4**: AmmoDepth read Props.ammoSet and raw container sums — now
  CurAmmoSet through CE's own AmmoCountOfDef (the dependency's documented
  rule for variable-ammo guns).
- **C5 (owner ruling: pass-through)**: an unmodelable weapon (no CE
  projectile/tools) was divided by the generic (1+pen) bonus while handed
  factor 1 — an unpatched mod weapon became the automatic "armor answer" and
  suppressed the defer. Fix: Try-variants report modeled=false; unmodelable
  weapons pass through COMPLETELY untouched and sit out of all-hopeless
  reasoning — the same mixing SS does with the features off. Pinned by a
  vanilla-tools staging weapon (CESSTest_VanillaClub, staging-mod def)
  riding the hopeless-armor phase.
- **TargetScoring rework (owner rulings: A + C)**: the per-hit arithmetic is
  now CE'S OWN CODE — the real private TryPenetrateArmor invoked with
  armor:null (every side effect sits behind its `armor != null` block; the
  null path is pure) via a cached delegate, with the T2 model retained as a
  loud named FALLBACK. The deflect-to-blunt conversion (one cbrt line) and
  the composition order stay modeled-with-citation. IL FINGERPRINTS
  (FNV-1a over opcode+operand of the upstream body, checked at load) guard
  TryPenetrateArmor, GetDeflectDamageInfo, and F07's DoReloadCheck: any
  upstream reshape turns silent drift into a loud re-verify error — and the
  census phase's startup sweep turns that error into a red suite.
- Also: Bootstrap counts Prepare-false classes as skipped, not applied; F01
  reads live verb range (attachments/verb swaps) with a def fallback; the
  component guards a null Settings.
- Staging lessons: weapon defs are mostly NOT stuffable — MakeThing with a
  stuff throws (revolvers, the test club); the armor phase's vanilla club
  raw-beats the knife vs flesh even in stock SS, so the swing phase destroys
  it; downing an armed mech races its in-flight burst and can kill it
  outright — park armed mechs OUT OF RANGE instead, and stage the downed
  one with a respawn retry; a 45-cell firing line can be LOS-blocked — the
  hopeless phase steps its target closer until a shot happens; tact6's
  raider must park outside the RIFLE's range or the drafted pawn's auto-fire
  refreshes the reload cooldown forever.

## T5 confirmation round (2026-09-02, post-T4 adversarial re-pass)

Two attackers (fresh full-repo + mechanical seam re-pass on the six T4 fix
seams) over the committed T4 state. Verdict: the T4 core fixes are sound —
both attackers independently traced T4-1's currency, T4-2's marker mechanics,
T4-3's fallback alignment, the fingerprints, Bootstrap, and the F01 gate
order CLEAN. Zero HIGHs. What they found is a ring of edge seams around the
T4-2 marker plus two model-fidelity gaps:

- **T5-A (Medium, found independently by both)**: the marker's `Installed`
  flag was set in Prepare — BEFORE Harmony applies the prefix — and
  Bootstrap's catch never reset it, so a co-loaded mod whose broken patch on
  `SyncedTryStartReload` fails the patch merge left `Installed=true` with no
  prefix installed: empty stamps, every player-forced reload classified as
  CE-auto — the exact inversion of the documented conservative degrade. Fix:
  `[HarmonyCleanup] Exception Cleanup(Exception ex)` resets the flag on an
  application failure and RETURNS the exception so Bootstrap's per-class
  accounting still logs it.
- **T5-C (Medium)**: re-clicking the reload gizmo mid-reload re-stamps while
  CE's `TryStartReload` early-outs on the already-running job — the stamp
  then postdates `startTick`, and the exact-equality join stripped the very
  protection the click expressed. Fix: at-or-after join (`tick >=
  jobStartTick`) — a stamp can only postdate a running reload's start if the
  player ordered a reload DURING it, which blesses that job (including an
  in-flight auto reload the player re-clicks — deliberate semantics).
  Verified live before fixing: forensics `stamp=330 startBefore=270
  startAfter=270` — the orphaned-stamp mechanism exactly as reported.
- **T5-B (Low)**: marker stamps are session-state (never scribed, keyed on
  object identity and a tick clock that both reset across loads) — after
  save/load a mid-flight gizmo reload lost its stamp and became abortable,
  and stale cross-game entries could sit unprunable behind the negative
  tick-delta test. Fix: `PlayerReloadMarker.Reset()` from
  `ReloadAbortComponent`'s ctor (one per new-or-loaded game). Cost accepted
  and documented: a reload mid-flight at save time degrades to flag-only
  protection for that one job.
- **T5-D (Low)**: F04's findBestRangedWeapon postfix composed AFTER the core
  patch's P03 dry-pick re-run — when SS's pick was truly dry, P03's
  overwrite (whose inner call is already fully F04-processed) got
  re-processed by the outer F04 postfix against the outer records with the
  floor re-anchored at the already-moved score, drifting the tie window
  toward (1−ε)². Fix: `[HarmonyBefore(CESimpleSidearmsCompat.Bootstrap
  .HarmonyId)]` on the postfix — P03's overwrite now runs last and discards
  the outer application; single-application by construction.
- **T5-E (Low, intent-classification)**: CE's under-barrel mode switch
  (`CompUnderBarrel.SwitchToUB`/`SwithToB` — upstream's own spelling) is a
  player command ending in `TryStartReload()`, indistinguishable from the
  ran-dry auto reload under the flag+marker scheme — the abort could
  override an explicit "use the launcher now". Fix: both switch entries get
  the same marker prefix (census 14). Their guards never touch
  `Installed` — if CE reshapes CompUnderBarrel, only switch-reload
  protection is lost, and the guard names exactly that.
- **T5-F (Low, model fidelity)**: TargetScoring's composition omitted two
  elements of CE's real `GetAfterArmorDamage` flow: (a) the partial-pen
  BONUS blunt hit every sharp packet that penetrates with damage loss also
  lands (lost-pen fraction → cbrt conversion → real blunt arithmetic), and
  (b) the deflect conversion's `amount/damageAmountBase` scaling for
  projectiles (quality-scaled primaries; small-amount secondaries were
  overstated — the secondary call site now chance-weights the RESULT, since
  the cbrt term is amount-independent and chance-weighting the input left
  full deflect damage in the expectation regardless of chance). Both terms
  now composed (fallback mirrored in expectation form);
  `GetAfterArmorDamage` added to the fingerprint set
  (`d65d743e005da6c9`).

## A/B — the T5 fix set (2026-09-02)

- **T5-C**: A-leg (`>=` reverted to `==`) red at
  `player-forced-reload-untouchable` — `primary=Gun_Autopistol`, the abort
  overrode the player's re-clicked reload; B-leg green. Two vacuous A-legs
  first: the phase's pistol was EMPTY (phase 2's firefight) and then GONE
  (any weapon switch bulk-drops it while phase 2's staging rifles overfill
  the pack) — the pin now purges the decoys post-switch and
  recovers-and-loads the pistol, and the re-click rides the phase's poll
  with marker forensics on the check detail.
- **T5-A/B/D/E**: no dedicated legs — T5-A/B are Harmony-lifecycle and
  save-lifecycle seams covered by the doctrine (Cleanup semantics are
  Harmony's documented contract; Reset is ctor-driven), T5-D is a patch-
  ordering attribute whose effect the census + tact4 armor phases exercise,
  T5-E is presence-pinned by census 14 (no under-barrel weapon staged in the
  suite; behavior follows T4-2's proven marker join).
- **T5-F**: numeric-only scoring change; the tact4/tact5 armor boards
  re-passed green (composition intact); guarded by the new
  GetAfterArmorDamage fingerprint.

## T4 confirmation round (2026-09-02, post-convergence adversarial re-pass)

Two attackers (one fresh full-repo, one mechanical seam re-pass) over the
committed convergence state, tasked with confirming the fix set had converged.
It had not: two HIGHs, both introduced by the convergence fixes themselves and
both hidden behind synthetic staging.

- **T4-1 (High)**: the ammo-depth tiebreak's MOVE branch wrote `score(best)`
  — which returns the RAW score when the all-hopeless defer is active — so a
  deeper twin inside the tie window re-armed C1's raw-vs-zero warmup compare
  one branch below C1's own fix. Consequence in play: a phantom same-def
  INSTANCE swap that resets the attack job (one bounce, then depths invert —
  a stutter, not C1's freeze; both features must be ON, defer + tiebreak).
  Fix: the returned tuple stays in the defer's currency
  (`deferred ? best.adjusted : score(best)`). Pinned inside
  `warmup-vs-hopeless-armor-still-fires`: equipped rifle shallowed to 5
  rounds + ONE full inventory twin (spares are a shared pool — only
  magazines differentiate depth; the picked weapon among equal raws is the
  first ENUMERATED, the primary — so the twin wins the depth tiebreak every
  time), `defer-move-stays-adjusted` asserts the moved pick still scores ≈0,
  and `no-phantom-swap-mid-warmup` (N) latches primary INSTANCE identity
  from attack start to first shot.
- **T4-2 (High)**: F01's player-respect gate read `job.playerForced` — but
  CE's `TryStartReload` stamps `playerForced = true` on the ran-dry AUTO
  reload too (both `Verb_ShootCE` call sites), so the abort skipped the
  flagship scenario (mid-firefight ran-dry reload with a loaded sidearm) and
  every suite pin passed because the tests staged reloads with a synthetic
  `playerForced: false`. Only `JobGiver_CheckReload` lull top-offs leave the
  flag false. Fix: a marker prefix on `SyncedTryStartReload` (the one entry
  only the player's gizmo reaches — census 12) stamps wielder+tick;
  `IsPlayerOrderedReload` = playerForced AND marker-stamped-this-job (job
  start is synchronous inside the synced call, so startTick equality is the
  join); no marker installed → conservative (every forced job is the
  player's). Pinned by `player-forced-reload-untouchable` driving the REAL
  gizmo entry via reflection and the new `a-ran-dry-auto-reload-is-abortable`
  driving the REAL auto entry (`TryStartReload`), with the mutate snapshot
  proving `job=ReloadWeapon forced=True` before the abort kills it.
- **T4-3 (Low)**: the modeled FALLBACK deflected zero-pen sharp attacks
  differently from CE (CE checks the sharp deflect verdict `armor > pen`
  FIRST; "pen==0 passes whole" is blunt-only). Fallback-only path; aligned.
- **T4-4 (Low)**: fingerprint operand tokens used culture-sensitive
  ToString — a comma-decimal locale would shift every float operand and
  permanently red the suite. Both copies (Tactics + compat's P07 guard) now
  format via InvariantCulture; baked hashes unchanged on this machine.
- **T4-5 (Low)**: TargetScoring's two fingerprints verified at first combat
  use, not load — `RunClassConstructor` in the mod ctor moves the drift
  signal to boot, where the census phase sees it.
- **T4-6 (Low)**: Bootstrap decoded each class's HarmonyPatch attributes
  OUTSIDE the per-class try — one upstream type-level drift crashed the
  whole loop instead of costing one class. Probe moved inside.

Staging lessons (T4's crop): the runner defers `mutate` until every
precondition holds — a precondition asserting POST-mutate state deadlocks the
phase invalid (assert arrange validity in P, capture mutate-time truth in a
snapshot local, assert it in C); informationals do not evaluate before
preconditions pass (ride diagnostics on the P's detail string); the disarmed
raider's fists and a live 9%-hp blaster centipede both DOWN the subject
across close-park phases — `Stun()` pins hostiles in place (Pawn.ThreatDisabled
has no stun test, so a stunned pawn stays a valid AttackTargetFinder threat)
with `HealInjuries()` + `FireAtWill=false` for determinism; `Carried()` order
is churn-dependent and phase 2's biocoded twin is equippable by CE's
`TrySwitchToWeapon` (biocode blocks SS selection, not direct equip) — pick
instances by predicate (`UsableRifle`), never by enumeration position;
`GetCarriedWeapons` walks inventory in REVERSED add order; weapon switches
bulk-drop the pistol from an overfull pack (purge staging leftovers before
switching); `TryStartReload` silently no-ops on non-equipped instances
(IsEquippedGun) and returns null-job when the backpack has no compatible
rounds — stage both; a feature-INTERSECTION bug needs BOTH toggles on in the
pinning phase (T4-1 was invisible until `ammoDepthTiebreak` was enabled
alongside the defer).

Ops: a crash-guard "Recovered from incompatible or corrupted mods" wipes the
TEST profile's `ModsConfig.xml` to Core (launch racing Steam startup) — the
game then wedges at early boot with a 60-line Player.log; restore from
`Config/ModsConfig.xml.known-good` (kept beside it) or the save's own
`<modIds>`. `pgrep -f`/`pkill -f` self-match the wrapping shell's command
line — guard game instances with `-x RimWorldLinux` only.

## A/B — the T4 fix set (2026-09-02, both HIGH legs run and red)

- **T4-1** (tiebreak currency): A-leg red twice over —
  `no-phantom-swap-mid-warmup` latches the phantom instance swap
  (`primary=...43661 staged=...38430`, same def, different thing) and
  `a-shot-actually-fires` dies in Stance_Warmup. Three vacuous A-legs on the
  way are the lesson above: a single twin = nothing to move (the picked
  weapon IS the twin under includeEquipped:false), equal mags = equal depth
  (shared spare pool), tiebreak setting off = MOVE unreachable.
- **T4-2** (playerForced gate): A-leg red at
  `a-ran-dry-auto-reload-is-abortable` (`primary=Gun_AssaultRifle` — abort
  skipped, reload completed) with `player-forced-reload-untouchable` still
  green — the marker separates the two entries, the flag alone cannot.
- **T4-3..T4-6**: load-time machinery and fallback-only paths — covered by
  the census phase (fingerprints now verify at boot; a mismatch reds the
  suite) and by inspection against the CE decompile; no dedicated legs.

## A/B — the T3 fix set (2026-09-01, every leg run and red)

Each fix scratch-reverted (sed), its pinning phase proven RED, restored, the
scenario proven green. Lessons from legs that came back green the first time:

- **T3-1** (job-identity gate): red only once the phase staged a LOADED second
  sidearm — without a livelock winner the old code's scan came up empty and
  "kept reloading" by luck. A-leg signature: primary swapped to the revolver,
  top-off dead at 0/7.
- **T3-2** (melee scope): all three tact5 target phases red; census stays
  green ON PURPOSE — F03 still patches the same method, so the count holds
  and only behavior detects this class going dark.
- **T3-3** (trySwap scope): census red (9 < 10) + the real-warmup phase red.
- **T3-4** (defer dry-filter): red only after a re-parking poll pinned the
  centipede inside the dry shotgun's own range window — past ~16 cells SS's
  range filter drops the shotgun before scoring, no record exists, and the
  leg passed vacuously (forensics: raw=-1 at dist 17).
- **T3-5** (usability filter): the staged biocoded rifle gets crowned, the
  equip is refused, the swap phase red.
- **T3-6** (CQC coverage): census red + knife-draw phase red.
- **T3-11** (refill-in-flight): red only through the MELEE-override preference
  pass — a plain Combat-mode pass is blocked mid-reload by the core patch's
  P05 guard, and the first version of the leg accidentally pinned P05 instead.
- **T3-8/T3-9/T3-10**: compat-side guard, comment correction, and the
  all-maps loop — covered by the compat suite's green re-cert (cetest2/3) and
  the doctrine's Prepare-guard convention; no dedicated legs.

## A/B spot-proofs (T1/T2 rounds, kept for history)

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
- A launch racing Steam's post-reboot startup trips the same crash-guard
  modlist wipe. A reboot also clears /tmp — scratchpad A/B and validation
  scripts die with it; anything needed across reboots belongs in the repo.
- Confirm a background battery is DEAD before launching another
  (pgrep run-tact/RimWorld): a lingering sweep shares SaveData and the log,
  and every interleaved result on both sides is garbage.

Manual residue: feel-testing the abort cadence in real combat.

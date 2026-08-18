# Test plan — Tactics module (v0.1 features)

Automated end-to-end via the suite's harness pattern:

```
./test/run-tact-stage.sh                       # regenerate TACT saves (quit after letter)
./test/run-tact-assert.sh tact1 TACT-1-reload-abort
./test/run-tact-assert.sh tact2 TACT-2-forced-dry
./test/run-tact-assert.sh tact3 TACT-3-tiebreak
```

Results land as `test-results-tact*.json` in the shared profile
(compat patch repo, `test/SaveData/`). Every scenario opens with a
**default-off negative control** proving the feature is inert, then enables it.

```
./test/run-tact-assert.sh tact4 TACT-4-ammo-target
./test/run-tact-assert.sh tact5 TACT-5-melee-target
```

Full green pass recorded 2026-08-18, all five scenarios (15/15 phases):

- **tact1 (reload-abort)**: feature-off reload completes untouched; feature-on
  mid-reload swap to the loaded pistol with a hostile in range; player-forced
  reload completes untouched with the feature ON and a threat present.
- **tact2 (forced-dry)**: feature-off forced branch holds a dry revolver;
  feature-on falls through to the loaded pistol with the ForcedWeapon flag
  intact; giving ammo back resumes the forced revolver — flag never cleared
  at any point.
- **tact3 (tiebreak)**: twins at equal DPS resolve to the deeper-ammo twin;
  a clearly-better rifle with 1 round wins regardless (epsilon subordination).
- **tact4 (target-aware ammo scoring)**: at 8 cells the buckshot shotgun raw-wins
  (41.9 dps); against 20mm mech plate the multipliers (rifle 0.30 vs shotgun
  floored) flip the pick to the rifle. No SelectedAmmo writes anywhere.
- **tact5 (armor-aware melee)**: fast blade wins vs flesh; blunt mace wins vs the
  mech (scores 0.92 vs 3.16) via the differentiated penetration floors.

Finding the harness caught (v0.1's first real bug): the original F1 design asked
SS's `findBestRangedWeapon` — which, with the core patch's axis 3, counts
reloadable-from-inventory weapons as viable, i.e. the very gun being reloaded.
The abort never fired, and the follow-up equip would have re-picked the same gun.
Fix: mid-reload the comparison is "loaded THIS INSTANT" — scan loaded secondaries
directly and equip the specific winner (`equipSpecificWeaponFromInventory`).

Second harness catch (features 5/6): a uniform penetration floor erased blunt's
entire reason to exist — CE's own numbers say a mace can't crack centipede plate
either, so both melee weapons floored equally and raw speed picked the knife.
CE's armor MECHANICS differ by damage type (under-penetrating sharp deflects,
blunt transfers trauma through), so the floors differ: sharp 0.10, blunt 0.40.
Also fixed: the harness's Raider() helper matched any hostile including mechs,
which had silently substituted the centipede for the "flesh" target.

## Harness ops notes

- The staging mod needs BOTH its Mods-dir symlink (`CESSTacticsTestStaging`) and
  its `ModsConfig.xml` entry; a boot with the entry but no folder makes RimWorld
  prune the entry, and a duplicated entry gets dropped entirely — both silently.
- Native-binary launches require windowed player prefs
  (`~/.config/unity3d/.../prefs`: Fullscreen mode 3, fixed resolution). Exclusive
  or native-res fullscreen intermittently crashes on Xwayland with an
  XF86VidMode BadValue when the desktop's mode list shifts (e.g. a Proton
  session is running). The owner's Steam launches use Proton and are unaffected.

Manual residue: feel-testing the abort cadence in real combat; features 5/6 when
implemented.

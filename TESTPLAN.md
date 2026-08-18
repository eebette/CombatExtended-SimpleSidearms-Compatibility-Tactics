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

Full green pass recorded 2026-08-18 (9/9 phases):

- **tact1 (reload-abort)**: feature-off reload completes untouched; feature-on
  mid-reload swap to the loaded pistol with a hostile in range; player-forced
  reload completes untouched with the feature ON and a threat present.
- **tact2 (forced-dry)**: feature-off forced branch holds a dry revolver;
  feature-on falls through to the loaded pistol with the ForcedWeapon flag
  intact; giving ammo back resumes the forced revolver — flag never cleared
  at any point.
- **tact3 (tiebreak)**: twins at equal DPS resolve to the deeper-ammo twin;
  a clearly-better rifle with 1 round wins regardless (epsilon subordination).

Finding the harness caught (v0.1's first real bug): the original F1 design asked
SS's `findBestRangedWeapon` — which, with the core patch's axis 3, counts
reloadable-from-inventory weapons as viable, i.e. the very gun being reloaded.
The abort never fired, and the follow-up equip would have re-picked the same gun.
Fix: mid-reload the comparison is "loaded THIS INSTANT" — scan loaded secondaries
directly and equip the specific winner (`equipSpecificWeaponFromInventory`).

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

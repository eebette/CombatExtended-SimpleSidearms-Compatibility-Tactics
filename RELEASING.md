# Releasing

Same no-CI reality as the suite siblings (see the core patch's RELEASING.md):
manual local builds, `Assemblies/CESSCompatTactics.dll` committed.

## Release checklist

1. **Build the core patch first** (compile reference), then:

   ```bash
   dotnet build Source/CESSCompatTactics/CESSCompatTactics.csproj -c Release
   ```

2. **Automated test pass** — regenerate saves, then all five scenarios
   (see TESTPLAN.md); every `test-results-tact*.json` must report
   `"passed": true`.

3. **Commit the DLL** with the source it was built from.

4. **Record versions**: CE, Simple Sidearms, and the core patch version tested
   against (this module reuses the patch's corrected scoring surfaces).

5. **Demo GIF** (Workshop page requirement): stage the demo scene, owner records
   the clip — see "Demo scene" below.

6. **Tag and publish** per the suite runbook (core repo `docs/SUITE_RELEASE.md`).

## Versioning & save compatibility

Semver; ships with the suite train. This module scribes NOTHING into saves
(settings only, stored in the mod config): safe to add mid-save, safe to remove
mid-save with zero footprint. The forced-dry feature bypasses but never persists
changes to SS state. Breaking either guarantee = major bump.

## Demo scene (for the Workshop GIF)

Use the staged test saves — already cinematic enough:

- **TACT-1-reload-abort** (headline): colonist reloading, melee raider closing;
  with the feature on the pawn snaps to the loaded pistol instead of finishing
  the reload. Record at speed 1, zoomed to the pawn.
- Alternate: **TACT-5-melee-target** (armor-aware melee: mace vs centipede,
  knife vs raider).

Prep: `./test/run-tact-stage.sh`, load via the core repo's `run-test.sh`
profile, dev mode OFF for the recording; defaults are already ship-on. Attach
the clip to the Workshop description and embed in README (host in `Media/` —
raw GitHub links animate in Steam descriptions).

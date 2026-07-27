# ChaseView — where things stand

Checkpoint so a fresh session can pick up without re-deriving anything.
Last updated: 2026-07-27, after v1.1.0 shipped.

## Shipped

| | |
|---|---|
| Release | `v1.1.0` → https://github.com/Oliepolie/ChaseView/releases/tag/v1.1.0 |
| Asset | `ChaseView-1.1.0.zip` (23503 bytes), sha256 `aae68ea57f93cfe6046ccc954fa8ff7e35f0c364dcb541d14da24299130c8863` |
| Tag | `v1.1.0` → commit `b9c6590`. `v1.0.0` still points at `659d5d4` and was never moved. |
| Game | built and measured against Nuclear Option **0.34.0**. 1.0.0 does NOT run on it. |
| NOMNOM PR | [KopterBuzz/NOMNOM#221](https://github.com/KopterBuzz/NOMNOM/pull/221) — open, mergeable, now proposing 1.1.0. Branch `Oliepolie/NOMNOM:add-chaseview`. |
| Verified | asset re-downloaded anonymously, sha256 matches both manifests, and the published DLL greps clean for `PerfProbe`/`GForceTest`/`ReportSeconds` |

Superseded: `v1.0.0` (`ChaseView-1.0.0.zip`, sha256 `80ffef90…`, commit `659d5d4`) — left in place, but
broken on 0.34.0 and no longer offered by the manifest.

Registry is **NOMNOM** (`kopterbuzz.github.io/NOMNOM/manifest/manifest.json`). Submission = one JSON
file in `modManifests/` via PR. `autoUpdateArtifacts: "True"` means **future releases are picked up
automatically — no second PR needed.**

Note the two manifest formats are different and easily confused: `release/meta.json` is the marker
NOMM drops into an installed plugin folder; `release/ChaseView.json` is the registry submission.

## Features

`ChaseCamera` · `WeaponPanel` · `DamageDiagram` · `TurretAimInChase` · `PerfProbe` (excluded from
release builds by a `Compile Remove` in the csproj — comment it out to build a measuring copy).

Verified 2026-07-27 that `Features/WeaponPanel` and `Features/DamageDiagram` each compile with the
other excluded, so the independence rule holds mechanically and not just by inspection.

Everything is `Parity.Local` **except `TurretAimInChase`**, which sends `Aircraft.SetTurretVector`
(a ServerRpc). It is the same message vanilla sends from the cockpit, for your own aircraft, at
vanilla's throttle — but it *is* a send, and the mod must never claim "client-side only".

## Decisions that are settled — do not re-litigate

Each has a `#tag` in the source; grep for it to find the reasoning at the site.

| Tag | Decision |
|---|---|
| `#orbit-reverted` | Free-look **pans in place**. Orbiting the camera around the aircraft was built, flown and rejected — it swung the forward view away exactly when needed. |
| `#mouselook-mirrors-cockpit` | Free-look copies `CameraCockpitState` exactly: accumulate a *target*, lerp the applied angle by `min(2·dt / viewSmoothing, 1)`. That lerp is where the smoothness lives and it smooths recentring for free. |
| `#aim-at-reticle` | The pivot aims at the reticle's world anchor (`cockpit.forward * 4000f`), not a fixed down-tilt. "Centred" therefore means the reticle is at screen centre on any airframe. Replaced `LookDownAngle`, which was removed. |
| `#center-recentres-first` | Vanilla's `Center` *leaves* chase for orbit. Off-centre it now recentres; once centred a second press exits as before. |
| `#mouselook-not-exclusive` | Mouse look composes with TrackIR. An earlier version bailed whenever TrackIR was enabled, killing it silently on any machine with `UseTrackIR=1`. |
| `#cockpit-only-restore` | Three places re-enable the flight HUD and all are gated on cockpit: `DynamicMap.Minimize`, `GameplayUI.ResumeGame`, `CameraCockpitState.EnterState`. The first two are patched through one shared restore. |
| `#diagram-one-switch` | One `ShowDamageDiagram` toggle. Two separate toggles could express "always show, vanilla colours", which renders a solid yellow aircraft — vanilla's undamaged colour is `g=1` over red, invisible only because its alpha is 0. |
| `#diagram-restore` | Originals are captured before the first write and restored on disable. Nothing in the game ever rewrites R or B, so our colours would otherwise persist forever. |
| `#scale-sentinel` | `_scale` starts at `-1f`. At exactly 1920×1080 the vanilla canvas scale is 1.0, so an initial `1f` made the first `SyncScale` early-out and the panel never got positioned. |
| `#perf-treewalk` / `#perf-throttle` | No whole-hierarchy searches or string allocations per frame. |
| `#turret-parallax` / `#turret-reticle` | Turrets aim at the *point* the camera looks at, not parallel to it; the crosshair projects that same convergence point rather than 10 km down the barrel. |
| `#gforce-not-a-cheat` | The G-force greyout is re-applied in chase. Vanilla runs the whole physiology camera-independently but gates only the *visuals* on the cockpit, so an external view flew the run-up to G-LOC with clean picture and clean audio. **No config toggle** — a checkbox that removes a penalty is the cheat. Lives in the ChaseCamera folder so it cannot be dropped without dropping the camera. **Flown and confirmed 2026-07-27.** |

## Game update 2026-07-27 (Steam buildid 24403978) — BREAKS SHIPPED 1.0.0

The game rewrote its assemblies at 00:31 local, mid-session. **`0.33.4` → `0.34.0`**; both manifests
now say `gameVersion: "0.34"`. That field matters more than it looks: NOMNOM's `Update-ModArtifact.ps1`
copies `gameVersion` from `artifacts[0]` onto every artifact it auto-appends, so leaving it stale
would mislabel every future release too.

**Other mods this update broke** (seen in the same log, neither ours): `anomie.rearmstatushud` throws
`MissingMethodException: Aircraft.remove_OnRearm` from `Update()` — 2864 times in one session, from
the main menu onward — because 0.34.0 refactored `IRearmable`/`OnRearm`; and `NOX` NPEs inside its
own `NOM.UpdateCheck`. There is also a burst of `Graphic.Rebuild` NPEs at aircraft spawn whose stack
is `Aircraft.SetupLocalPlayerAndUI → DynamicMap.Maximize → FlightHud.EnableCanvas → Text.OnDisable`,
containing no ChaseView frame and no method ChaseView patches. Worth re-checking if anyone reports
HUD trouble, but it is not ours.

**What broke.** Head tracking was generalised behind `IHeadTracker` / `HeadTrackerManager` (TrackIR
today; a `TobiiHeadTrackerComponent` exists but `ActiveTracker` does not yet return it).
`TrackIRComponent.GetTrackIROffset` is **gone** — renamed to `GetHeadTrackerOffset`, not deprecated.
ChaseView 1.0.0 calls the old name from `UpdateState_Post`, so on this build that postfix throws and
its whole job — roll follow, mouse look, TrackIR, velocity align — stops. Fixed by going through
`HeadTrackerManager.GetOffset`, which is what the cockpit now does and picks up any future tracker
for free. See `#headtracker-manager`.

**How compatibility was checked, and how to repeat it.** Two passes, because they catch different
things:

1. *Compile against the new DLLs.* The compiler mechanically verifies every type, member and
   signature the mod references. This is what caught the rename. It does **not** check
   string-based lookups — `AccessTools.Method(typeof(StatusDisplay), "Update")`,
   `typeof(Turret), "FixedUpdate"`, and the `"compass"` field — so grep those and confirm by hand.
   All three were verified present on this build.
2. *Diff the decompiled classes we depend on behaviourally*, which the compiler cannot see.
   Result this time: `GLOC` **byte-identical** (so `#gforce-not-a-cheat` is unaffected);
   `FlightHud`'s `cockpit.forward * 4000f` reticle anchor unchanged (`#aim-at-reticle`,
   `#reticle-clearance` hold); `Turret.GetDirection` identical and `FixedUpdate`'s three changes all
   in the AI path, not manual aim (`#turret-parallax` holds); `GameplayUI.ResumeGame` and
   `DynamicMap.Minimize` still gated on cockpit and still the only restore points, so
   `#cockpit-only-restore` still covers everything. The rest was layer-mask constants, a Doppler
   refactor, and a new `ThemeManager`.

**Noted, not acted on:** the game now has a UI theme system (`ThemeManager.Active.ColorTheme`).
WeaponPanel's colours are hardcoded and will not follow a user's theme. Cosmetic, not a break.

## Open

- **The head-tracking path itself is compiled but unexercised.** The flight that verified 0.34.0 ran
  with `PlayerSettings.useTrackIR = False` (the update appears to have reset it — it was on before),
  so `HeadTrackerManager.GetOffset` was never *called*. It was however **resolved**: Mono resolves a
  method body's tokens when it JITs, and `UpdateState_Post` ran fine — mouse look and roll follow
  both worked — which is exactly what a missing method would have prevented. So the break is fixed;
  only the offset composition in chase is untested. Re-check if you turn head tracking back on.
- **1.1.0 shipped with two changes never flown**: the `DamageDiagram` split out of `WeaponPanel`, and
  the `#cache-null-manager` fix. Risk was judged low because `AlwaysShow` and `ShowWeaponList` both
  default off, so a fresh install exercises neither path — but they are untested, not verified. First
  thing to check if anything is reported.
- **Both manifests are STALE — they say `1.0.1` with the hash of a build that will never be
  published.** Regenerate `release/meta.json` and `release/ChaseView.json` at package time: version
  and fileName `1.1.0`, the `v1.1.0` download URL, and the sha256 of the actual zip. `gameVersion`
  is already correct at `0.34`.
- **HUD readability — SETTLED, do not reopen.** Shipped answer is `HudTint = 0.1` with
  `HudTintVignette` **off**; `HudScale` tuned to ~1.08. Everything else was tried and removed.
  **`HudShadow` is gone** — see `#additive-cannot-darken`: the HUD font shader is
  `TextMeshPro/Distance Field Additive`, blending is `dst + src`, so **black contributes nothing**
  and no dark effect can render on the symbology at any setting. Three separate flights measured
  that before the shader name got checked; check the shader FIRST next time.
  `HudOpacity`/`HudBrightness` remain but do little for the same reason (alpha under additive scales
  how much is *added*).
- **Draw-the-text-twice-in-dark is possible but unbuilt.** The probe confirmed all three
  alpha-blended TMP shaders are present (`Distance Field`, `Mobile/Distance Field`, `Overlay`), so a
  dark duplicate layer would render. Not built: those ~26 labels change every frame, so it would
  double TMP mesh regeneration on the hottest UI path. Backing-plate quads would be the cheap
  version if this is ever wanted.
- Olie's README should probably say the G-force effects apply in chase. **His prose, so his call** —
  ask before editing.
- **`TurretAimInChase` has never been tested on a real two-machine connection.** Hosting is a listen
  server and does not exercise the client leg. Stated in the README.
- **Performance — SETTLED 2026-07-27, uncapped and vSync off. The old worry was an artefact.**

  | mode | frames | mean | fps | >16 ms |
  |---|---|---|---|---|
  | **chase** | 23 940 | **8.24 ms** | 121 | **1.09 %** |
  | **cockpit** | 3 713 | **8.59 ms** | 116 | **6.30 %** |
  | free | 3 773 | 11.49 ms | 87 | 35.86 % |
  | selection | 967 | 7.89 ms | 127 | 0.62 % |

  **Chase is cheaper than the cockpit** — lower mean and six times fewer long frames over a
  24 000-frame sample. The earlier "chase spikes ~1% of frames" reading came from measuring against
  a 90 fps cap that pinned every mean; with the cap and vSync off it inverts. The cockpit is the
  expensive view, which fits — it renders the cockpit interior *and* drives a second camera
  (`cockpitCamRender`). `free` is by far the worst mode and ChaseView does not touch it.

  Not captured: the `Bypass=True` half, so the mod's *isolated* cost is still unmeasured. Left as
  optional — chase already beats cockpit, so our overhead is bounded well below anything that
  matters. Redo with one 30 s toggle if a number is ever wanted.
- **`TurretAimInChase` has never been tested on a real two-machine connection.** Hosting is a listen
  server and does not exercise the client leg. Stated in the README.
- **Performance — the pass is next, and the harness is ready.** Chase spikes ~1% of frames past 16 ms
  vs ~0% elsewhere, but that was measured against a 90 fps cap that pinned every mean, and the
  `PerfProbe / Bypass` A/B was never run. Everything below is verified as of 2026-07-27:

  1. **Uncapping takes TWO settings, not one.** Graphics options → Frame Rate Limit → unlimited
     (`GraphicsHelper.SetFPSLimit` → `GameManager.TargetFrameRate`, `-1` = uncapped) **and VSync
     OFF** (`GraphicsHelper.SetVSync` → `QualitySettings.vSyncCount`). Miss the second and the
     monitor refresh pins every mean exactly as the 90 fps cap did — this is the trap that wasted
     the last measurement.
  2. **PerfProbe IS COMPILED IN RIGHT NOW** (2026-07-27) and `[PerfProbe] Enabled = true` is already
     written into the live config. **The `<Compile Remove>` line in the csproj must be restored
     before any release build** — `grep -n PerfProbe src/ChaseView.csproj` should show it
     uncommented. `Diag.Bypass` has no other writer, so without PerfProbe there is no A/B at all.
  3. **Method:** same sortie, same scene — cockpit vs chase gives the upper bound, then chase with
     `Bypass` on vs off isolates our share of it.

  New per-frame work added since the last measurement, in rough order of suspicion:
  `HudContrast.LateUpdate` (walks ~50 cached Graphics), `ScreenLockedReadouts.Update`,
  `HudContrast.Update`, `GForceEffects` (FixedUpdate, four lerps). The colour loop *should* be nearly
  free on an already-opaque HUD because it writes nothing when the transform is a no-op, and a
  skipped write means no `SetVerticesDirty` and no canvas rebuild — but that is reasoning, not a
  measurement, and it is exactly what the probe is for.
- **Licensed MIT** (2026-07-27, `LICENSE` at the repo root). Genuinely open source, not merely
  source-available: forks, patches and redistribution are all permitted with attribution. Clean to
  apply because nothing third-party is vendored or shipped — every source file is first-party, the
  game/BepInEx/Harmony references are all `Private=false` so none are copied, and the release zip
  contains exactly one file, our own DLL.

  The zip itself does not carry a copy of LICENSE. Normal for a plugin DLL, and not worth re-cutting
  1.1.0 for since that would change the asset hash and invalidate both manifests and the open PR —
  but worth adding to the staging folder for the next release.
- Olie's README says *"There is no targeting view, unless you switch back to your cockpit view."*
  There is — `Switch View` again from chase reaches TV, since the cycle is cockpit → chase → orbit → TV.
  His words, his call.

## Traps hit here, worth not repeating

- **Force-moving a tag that a published release points at deletes the release.** Cost the release
  object and its asset once. Next version: new tag, new release, never move.
- **`config.Bind` never overwrites a value already on disk.** Changing a shipped default reaches
  nobody who has run the mod — including you. Every default change needs the `.cfg` edited by hand or
  a migration.
- **Index-based source slicing damaged `ChaseCamera.cs` twice.** Both times the compiler or git caught
  it. Prefer anchored string replacement with an assert, and keep the working tree committed.
- **PowerShell's `Out-File -Encoding utf8` writes a BOM**, which breaks JSON parsers.

## Sibling project

`../TrueQoL` — same scaffold, keeps the non-camera QoL: `Scoreboard`, `MapOptionsMemory`,
`HudOnPause`, `ShowCopilots`, plus a temporary `HudProbe`. Unreleased, own GUID. Four vetted salvage
candidates from QoLie are queued in its `docs/FEATURES.md`.

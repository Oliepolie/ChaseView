# ChaseView — where things stand

Checkpoint so a fresh session can pick up without re-deriving anything.
Last updated: 2026-07-26, after v1.0.0 shipped.

## Shipped

| | |
|---|---|
| Release | `v1.0.0` → https://github.com/Oliepolie/ChaseView/releases/tag/v1.0.0 |
| Asset | `ChaseView-1.0.0.zip`, sha256 `80ffef90d5769f658c0ca5153d81d57ac0be11f17bf146948447f4afbe220a5f` |
| Tag | `v1.0.0` → commit `659d5d4` (contains the source it was built from) |
| NOMNOM PR | [KopterBuzz/NOMNOM#221](https://github.com/KopterBuzz/NOMNOM/pull/221) — open, awaiting first-time-contributor workflow approval |
| Verified | asset downloaded anonymously, hash matches both manifests, DLL inside == the DLL flown |

Registry is **NOMNOM** (`kopterbuzz.github.io/NOMNOM/manifest/manifest.json`). Submission = one JSON
file in `modManifests/` via PR. `autoUpdateArtifacts: "True"` means **future releases are picked up
automatically — no second PR needed.**

Note the two manifest formats are different and easily confused: `release/meta.json` is the marker
NOMM drops into an installed plugin folder; `release/ChaseView.json` is the registry submission.

## Features

`ChaseCamera` · `WeaponPanel` · `TurretAimInChase` · `PerfProbe` (excluded from release builds by a
`Compile Remove` in the csproj — comment it out to build a measuring copy).

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
- **1.0.1 is a compatibility release, not just a feature one.** 1.0.0 is broken on 0.34.0, so it
  should ship promptly. Packaged and manifests updated; publishing is the remaining step.
- Olie's README should probably say the G-force effects apply in chase. **His prose, so his call** —
  ask before editing.
- **`TurretAimInChase` has never been tested on a real two-machine connection.** Hosting is a listen
  server and does not exercise the client leg. Stated in the README.
- **Performance**: chase spikes ~1% of frames past 16 ms vs ~0% elsewhere, but that was measured
  against a 90 fps cap that pinned every mean. The `PerfProbe / Bypass` A/B was never run. To settle
  it: raise `FrameRateLimit` first (a cap hides everything), then compare bypass on vs off in the
  same place.
- The repo has **no LICENSE** — source-available, not open-source. Fine for NOMNOM (their clause is
  about visibility and non-obfuscation) and normal for the ecosystem, but nobody may legally fork it.
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

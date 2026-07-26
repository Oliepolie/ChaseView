# ChaseView

A proper third-person chase camera for **Nuclear Option** — with the HUD.

## What it does

Nuclear Option already has a chase camera. It just won't show you the HUD from it, won't let head
tracking look around from it, and won't let you aim a turret from it. ChaseView fixes all three.

- **HUD and minimap in chase view.** The game builds this and then only exposes it inside the mission
  editor. ChaseView unlocks it rather than reimplementing it, so vanilla's sensible behaviour — HUD
  from the tail and belly views, hidden from the wingtip cameras — is kept, with a toggle if you want
  it everywhere.

- **Readouts that stay put.** Vanilla pins the *entire* HUD to a point 4 km off your nose, so from
  outside the aircraft your speed, altitude, heading and mission time slide around and roll with the
  airframe. ChaseView moves the informational readouts to fixed screen positions. The reticle, pitch
  ladder and waterline deliberately stay nose-locked — those are aim and attitude, and screen-locking
  them would be lying to you.

- **Auto framing.** Distance and height are derived from each aircraft's real dimensions and your FOV,
  so one setting frames a light fighter and a heavy bomber the same way. You pick how much of the
  screen the aircraft should fill and how many degrees above it to sit; the rest follows.

- **An Ace Combat style weapon readout.** Every station at once with its short code and ammo, a marker
  on the selected one, countermeasures, and a damage percentage — in the bottom-right corner.

- **Optionally, an always-on damage diagram.** The game's own top-down aircraft silhouette, recoloured
  green through yellow to red and kept on screen instead of vanishing ten seconds after you're hit.
  **Off by default** — `ShowDamageDiagram`.

- **Turret aiming that works.** Vanilla only updates a manually-aimed turret in the cockpit, so in any
  external view the turret holds a fixed compass bearing and ends up pointing the wrong way as you
  turn. ChaseView aims it at the point you're actually looking at, and puts the crosshair there too.

- **TrackIR free-look**, which vanilla applies in the cockpit only.

## Getting there

Press **`Switch View`**. ChaseView slots chase into the cycle right after the cockpit:

```
cockpit  →  CHASE  →  orbit  →  TV  →  cockpit
```

Vanilla skips chase entirely — it cycles cockpit → orbit → TV, and chase has exactly one entry point,
the `Center` button from orbit. That still works. Numpad `0`–`9` pick camera positions once you're
there, and only the default Back view is auto-framed; the others stay vanilla.

Set `ChaseCamera / InViewCycle` to false to leave the cycle alone.

## Multiplayer

Everything is client-side rendering and nothing needs to match between players — with one exception,
stated plainly: **`TurretAimInChase` sends.** It sends exactly the message the cockpit path already
sends, for your own aircraft, at vanilla's own throttle, so it is indistinguishable on the wire from
someone flying in cockpit view. It is a separate feature you can switch off if you would rather ship
nothing at all. It has not yet been verified on a two-machine test.

Everything else is `Local`. Joining a server that has never heard of ChaseView works exactly as it
does without it.

## Configuration

`BepInEx/config/com.olie.chaseview.cfg`, or in-game if you have `BepInEx.ConfigurationManager`.
Every feature has its own `Enabled` toggle. The ones worth knowing:

| Setting | Default | |
|---|---|---|
| `ChaseCamera / InViewCycle` | `true` | Put chase in the Switch View cycle |
| `WeaponPanel / ShowWeaponList` | `true` | The weapon readout, separate from the diagram |
| `ChaseCamera / ScreenFill` | `0.35` | How much of the screen height the aircraft spans |
| `ChaseCamera / Elevation` | `8°` | Degrees above the centreline the camera sits |
| `ChaseCamera / LookDownAngle` | `6°` | Tilt down the flight path, so you sit above the tail |
| `ChaseCamera / ReticleClearance` | `1.0` | Keeps the airframe out of the sightline to the reticle |
| `ChaseCamera / RollFollow` | `1.0` | `1` welds the camera to the airframe, `0` keeps the horizon level |
| `ChaseCamera / VelocityAlign` | `0.0` | Aim toward the velocity vector — the high-AOA knob |
| `ChaseCamera / ScreenLockReadouts` | `true` | Stop the readouts chasing the nose |
| `WeaponPanel / ShowDamageDiagram` | `false` | Keep the damage diagram up, coloured green → red |
| `WeaponPanel / DiagramOpacity` | `0.45` | How solid the diagram is when undamaged |
| `WeaponPanel / HideVanillaWeaponPanel` | `false` | Hide the stock top-right weapon blocks |
| `ChaseCamera / Momentum` | `0.0` | Sit behind the flight path rather than the nose |
| `TurretAimInChase / Enabled` | `true` | The only feature that transmits |

The stock weapon panel and the new one are independent: `ShowWeaponList` and
`HideVanillaWeaponPanel` let you run either, both, or neither.

Everything else is under **Advanced settings** in ConfigurationManager — tuning knobs you should not
need to touch.

`RollFollow` at `0.3`–`0.6` is worth trying — vanilla welds the camera to the airframe so the horizon
spins; damping it is closer to how War Thunder feels.

## Install

Requires **BepInEx 5 (Mono, x64)** — Nuclear Option is Mono, not Il2Cpp.

Drop the `ChaseView` folder into `<game>/BepInEx/plugins/` so you end up with:

```
<game>/BepInEx/plugins/ChaseView/ChaseView.dll
```

## Known limitations

- **`TurretAimInChase` has not been tested on a real two-machine connection.** Single-player and
  hosting are a listen server, which does not exercise the client path.
- The aircraft diagram is per-airframe and some airframes ship none; on those the silhouette and the
  damage percentage are simply absent.
- Free-look **pans** rather than orbiting. Orbiting was built and rejected — it swung the forward view
  away exactly when you needed it.

## Build from source

```bash
dotnet build -c Release
```

Adjust `<GameDir>` in `src/ChaseView.csproj` if your install is elsewhere.


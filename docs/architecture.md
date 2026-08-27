# Architecture

> ⚠️ **Superseded where it disagrees with `docs/pilot-architecture.md`.**
> This was written before the three design artifacts were ported. It remains useful as a
> narrative walkthrough of *why* each stage exists, but its specifics are out of date in
> at least three places: it describes an EMA filter (the design uses median → one-euro),
> a linearized least-squares solve (the design uses Gauss-Newton), and it says Android
> BLE scanning needs Location permission (the Eddystone identity choice specifically
> avoids that). Read `pilot-architecture.md`, `build-sheet.md` and `how-it-works.md` first.

Five stages, each with one job. Every stage is replaceable without touching the ones
above or below it — that is the point of the seams.

## 1. Beacon reading

BLE beacons broadcast advertising packets. The phone's radio reports an **RSSI** for
each packet: received signal strength in dBm, typically −40 (very close) to −95 (far
or blocked). A BLE plugin surfaces those to C#.

Every reading carries the beacon's identity (UUID + major/minor, or MAC on Android),
the RSSI, the beacon's advertised transmit power at 1 m, and a timestamp.

The plugin is behind `IBeaconScanner` so that:
- the concrete plugin can be swapped without a rewrite,
- iOS and Android differences stay in one place,
- the Editor can run a simulated scanner and the whole positioning stack can be
  developed and tested without walking the hallway.

## 2. RSSI → distance

RSSI is noisy. Bodies, carts, doors and metal all attenuate it, and consecutive
readings from a stationary phone can swing 10 dB. Two steps tame it:

**Filter.** An exponential moving average per beacon, plus rejection of readings older
than a staleness window. A stronger Kalman filter is a later upgrade; start simple and
measure.

**Path-loss model.** `distance = 10 ^ ((txPower − rssi) / (10 · n))`, where `txPower`
is the calibrated RSSI at 1 m and `n` is the environmental path-loss exponent — around
2.0 in free space, higher (2.5–4) in a corridor with walls and people. `n` **must be
calibrated on the actual floor**, not assumed. Pace out known distances, log RSSI, fit.

Expect ~2–5 m of error per beacon. That is fine: the geometry stage removes most of it.

## 3. Trilateration → map position

Three or more distance estimates with known beacon positions give a position. With
noisy distances the circles never meet at a point, so this is a **least-squares
multilateration**, not textbook trilateration: linearize against a reference beacon and
solve the resulting overdetermined system.

Rules that matter in practice:
- Use the N strongest beacons (3–5), not all of them. Distant beacons contribute mostly noise.
- Reject geometrically degenerate sets — beacons nearly collinear along the corridor
  give a good position along the hallway and a garbage one across it.
- The result is 2D. Floor level comes from which beacon set is visible, not from math.
- Clamp the solved position to the walkable region. A visitor is in the hallway, not
  inside a wall.
- Smooth the position over time, and gate on plausible walking speed (~1.4 m/s).

Output: an (x, y) in **map coordinates** — meters, origin at the floor's reference
corner, axes along the hallway.

## 4. Map, coordinate system, and pathing

The floor is modeled as a **1:1 scaled virtual replica** — hallway geometry, corners,
turn points, room doorways. It exists for two reasons: the NavMesh bakes against it,
and it defines where "room 214's door" is in space.

`MapCoordinateSystem` is the only place that converts map coordinates to Unity world
coordinates (and back). Everything upstream speaks map coordinates; everything
downstream speaks Unity world.

`PathfindingEngine` asks Unity's NavMesh for a corner-to-corner path from the user's
position to the destination doorway. The raw NavMesh path hugs corners tightly; it is
smoothed and offset toward the corridor centerline so the drawn line does not tell
someone to walk into a doorframe.

## 5. AR rendering

AR Foundation runs the camera and the tracked session. Plane detection finds the floor.
The path is drawn as a ribbon along that plane and attached to a **world anchor** so
that it stays put as the phone moves — the line belongs to the building, not the
screen.

Two coordinate systems have to be reconciled: AR's session space (origin wherever the
session started, drifting slowly) and map space (fixed to the building). The link is an
alignment — position and heading — established at the entrance QR scan and corrected
whenever confidence in the beacon position is high. Drift correction has to be gentle;
snapping the line is more alarming to a user than being half a meter off.

## What the QR code does

The entrance QR encodes the floor/building identifier and the known map coordinate and
heading of the sign it is printed on. That gives the app a trustworthy origin at the
moment the session starts, which is worth more than any amount of beacon math — the
positioning stack then only has to hold that fix, not find one from scratch.

## Failure behavior

The app must degrade honestly. When too few beacons are visible, or the position
solution is low-confidence, or AR tracking is lost, the UI says so and falls back to
"head toward X" rather than drawing a confident line to the wrong place. A wayfinder
that lies is worse than a sign.

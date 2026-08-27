# Floor map data

All hand-measured floor data lives here first. It goes into the `FloorMap` asset from
here — never hardcoded into a script.

## Coordinate system

Relative X-Y grid, **meters**, paced out by hand. No GPS.

- Origin: the floor's chosen reference corner (record which one, and photograph it).
- +X: along the main hallway.
- +Y: across the hallway.
- Z / floor level is not a coordinate — it is which floor's `FloorMap` is loaded.

Pacing is roughly ±0.3 m per measurement and error accumulates along a corridor. Where
precision matters — beacon anchors especially — re-measure with a tape or a laser
measure rather than trusting a paced number.

> **The numbers are not in any of the three design artifacts** — all three describe the
> survey as an input you supply ("type the right-side coordinates you already paced").
> They live in your own notes. This file is where they land, and step 1 of the bring-up
> order in `docs/build-sheet.md` is entering them.

## Rooms — right side (measured)

12 rooms on the right side of the floor have paced X coordinates. **Transcribe them
into the table below**, then into the `FloorMap` asset.

| Room | X (m) | Y (m) | Door faces | Notes |
|---|---|---|---|---|
| | | | | |

Y for a right-side room is the hallway width minus the wall offset — which needs the
hallway width below before the table can be completed.

## Rooms — left side (MISSING)

6 rooms. Not yet measured. Needed: X for each, and the same door-facing note.

## Hallway geometry (MISSING)

Needed before the NavMesh can be built:

- **Hallway width.** One number unlocks every room's Y coordinate.
- **Corner points.** Where the corridor changes direction — X-Y for each.
- **Turn points.** Junctions and branch openings.
- Any width changes, alcoves, or permanent obstructions (carts, nurse stations,
  equipment that is always there).

## Beacon anchors (TO DO)

~30 BlueCharm BC011 units. For each deployed beacon record:

| ID (UUID / major / minor) | X (m) | Y (m) | Height (m) | Mount | Installed |
|---|---|---|---|---|---|
| | | | | | |

Placement notes worth deciding before mounting anything:

- Height matters — mount consistently (ceiling or high wall) so the 2D distance
  assumption stays reasonable.
- Avoid placing beacons in a straight line down one wall only. Collinear beacons make
  the across-corridor position unsolvable. Stagger them across both walls.
- Spacing drives both accuracy and battery. The vendor inquiry covers range, placement,
  accuracy, and battery-appropriate models at ~30 units — fold the answer in here.
- Record the advertised TX power at 1 m per unit; the path-loss model needs it.

## Calibration (TO DO)

Path-loss exponent `n` for this corridor, measured on the actual floor: log RSSI at
known distances (1, 2, 4, 8, 12 m), fit, record the value and the date. Re-check after
any change to what's in the hallway.

| Date | Beacon | Measured n | Notes |
|---|---|---|---|
| | | | |

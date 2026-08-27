# Open questions

Things not yet decided. Each one is cheap to answer now and expensive to answer after
code is built around a guess.

## Resolved — do not re-litigate

These were open and are now settled by `docs/pilot-architecture.md` and
`docs/build-sheet.md`. Recorded so they do not get reopened by accident.

| Was | Now |
|---|---|
| Which BLE plugin? | **Shatalmic**, from the Asset Store, behind `IBeaconScanner`, gated on `WAYFINDING_BLE_PLUGIN` |
| How are beacons identified? | **Eddystone-UID instance ID.** Not MAC, not peripheral UUID, not iBeacon — see §4 of the architecture doc |
| How is AR aligned to map space? | Hard alignment at the **QR scan**, which carries position *and* heading. Compass is unusable indoors — 30–90° out in a steel building |
| Drift correction policy? | Continuous, **15 cm/s** translation and **3°/s** yaw. Never snap. Above 8 m disagreement, stop correcting and ask for a rescan |
| Filter design? | Rolling **median** → **one-euro** → path loss to metres |
| Trilateration method? | Weighted centroid seed, **Gauss-Newton** refinement, plus a geometry-quality score |
| Non-AR fallback? | Out of scope for the pilot. Named as a known limitation, not built |
| Is there a backend? | **No.** No networking library in the project at all |

## Still open

### Survey

- **Where is the origin corner?** Pick it, photograph it, write it down. Every measured
  number depends on it.
- **Hallway width** — one measurement that unblocks every room's Y coordinate.
- **Left-side rooms, corners, turn points** — 6 rooms plus corridor geometry, unmeasured.
- **Room numbering reality.** `RoomNode` has an `aliases` field for a reason. Check for
  duplicates across buildings, suffixed bays (214A/214B), and rooms visitors are *told*
  to go to that differ from where they end up.

### Hardware

- **Beacon spacing and mounting height.** Pending the vendor's answer. Affects whether
  30 units cover the floor and how long the batteries last at the 511 ms interval.
- **What are they mounted to?** A beacon taped to a steel door frame is not the beacon it
  was on the desk. Surface material changes measured TX power, which is why every unit
  gets surveyed individually rather than assumed.
- **Where does the QR sticker go?** It needs a spot with exactly one comfortable way to
  stand and scan — that is what makes the heading trustworthy.

### Product

- **Accessibility.** A wayfinder that only works if you can hold a phone at arm's length
  and see a thin line excludes some of the people who most need directions. Explicitly
  out of pilot scope; worth naming as a known limitation with a direction.
- **Who owns the IP?** Unresolved, and it gets harder to resolve the more is built.
  Worth settling before the project is valuable enough to argue about.

### Engineering

- **Unity version.** Architecture doc says 6.3; the laptop has 6000.5.9f1. Pick one.
- **Project name.** Architecture doc says `HospitalWayfinding/`; the folder is `HHC Ting`.
- **HDRP → URP.** Not a question so much as a prerequisite, but it is unstarted. See
  `docs/setup-and-packages.md`.

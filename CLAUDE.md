# CLAUDE.md — Hospital AR Wayfinder

Context for Claude Code working in this repo. Read this first, then
`docs/pilot-architecture.md`, which is the **design of record**.

## What this is

An indoor AR wayfinding app for a hospital. A patient or visitor scans a QR code at
the building entrance, types in a room number, and the phone camera shows a **racing
line** — a path overlay fixed in real-world space — that leads them to the room.

Origin: giving directions to lost patients and visitors 5–10 times a day across three
large buildings with inadequate signage.

Pilot scope: **one floor, 18 rooms, 30 beacons, one QR code, one app.** Android first.
Roughly $300 of hardware.

**Confidentiality:** this project is under wraps until further approvals land. IP
ownership between the developer and the hospital is unresolved — that is why the repo
is private and carries **no license file**. Do not add one, and do not put hospital
names, floor plans, or room-level detail anywhere that could become public.

## The three rules that shape everything

1. **Nothing leaves the phone.** No server, no database, no queue, no account, no
   analytics, no logs. The app runs in airplane mode. This is not a feature that is
   switched off — the project contains **no networking library at all**, and a reviewer
   can verify that from the dependency list. Do not add one. The moment location data
   crosses the device boundary this stops being a small pilot and becomes a PHI
   conversation with legal, IT security and a BAA attached.
2. **Beacons are identified by Eddystone-UID instance ID.** Not MAC (iOS does not expose
   it), not CoreBluetooth peripheral UUID (differs per phone), not iBeacon major/minor
   (CoreBluetooth filters it, and reaching it via CoreLocation forces a **Location
   permission prompt**). Eddystone service data works identically on both platforms and
   keeps the app to **Camera + Bluetooth permissions only**. That permission list is
   load-bearing for the privacy pitch — do not do anything that adds Location.
3. **Degrade honestly.** Every failure path resolves to "finding your position" or no
   arrow at all, never to a confident line pointing the wrong way. A visitor forgives
   "finding you"; they do not forgive being sent down the wrong corridor. See §8 of the
   architecture doc for the full table of thresholds.

## Architecture

```
BLE scan (Eddystone UID + TLM, 2 Hz)  ->  RSSI readings
     |
     v
RssiFilter: median -> one-euro -> metres
     |
     v
Trilateration (Gauss-Newton, no Unity dependency)  ->  PositionFix @ 8 Hz
     |
     v
FloorMap  ->  snap into the walkable corridor
     |
     v
NavMesh path (recompute every 3 s)  +  turn instructions
     |
     v
ARWorldAligner + ARPathRenderer  ->  the ribbon, anchored to the building
```

Positioning uses a hand-surveyed relative X-Y grid in meters. No GPS, no wifi
positioning, no vendor positioning SDK, no dependence on hospital IT.

**Unity + AR Foundation**, not native Swift/ARKit — visitors carry both iPhones and
Android phones, and an iOS-only app is useless at the front door. Android ships first
because the build path from the AWS VM does not need a Mac.

Key runtime rates (full table in the architecture doc): solve at 8 Hz, recompute the
path every 3 s, rebuild the ribbon every 0.25 m of movement, correct drift at 15 cm/s
and yaw at 3°/s. These are tuned, not arbitrary — getting them wrong is what makes a
working app feel broken.

## Build order

1. Beacon-to-virtual-space linking (scanner → filter → trilateration → map position).
2. Map + coordinate system + NavMesh pathing.
3. AR rendering of the path.
4. UI **last**.

Do not jump ahead to polish the UI. If positioning is not solid, nothing above it works.

## Environment

- Unity **6000.5.9f1** is what is installed locally. The architecture doc specifies
  **Unity 6.3** — reconcile before the AWS image is built so the VM and the laptop agree.
- AR Foundation **6.x**. Target: Android, IL2CPP, ARM64, ~60 MB signed APK.
- The laptop cannot run Unity. Development happens on an **AWS VM** over VS Code SSH
  plus a DCV desktop session, stopped between sessions.
- Tag every build that goes on a phone: `pilot-0.3-floor4-2026-09-14` — version, floor, date.

## ⚠️ Two things that do not match yet

**The project is on the HDRP template.** HDRP does not support mobile AR; AR Foundation
needs URP. Nothing AR-related will work until this changes. See
`docs/setup-and-packages.md`. The architecture doc assumes a clean URP project.

**`Assets/Scripts/` currently holds a 15-file scaffold that predates the architecture
doc** and does not match it — different names (`SimulatedBeaconScanner` vs
`MockBeaconScanner`, `MapCoordinateSystem` vs `ARWorldAligner`), a `Wayfinder`
namespace instead of `Wayfinding`, no `EddystoneFrame`, no asmdefs, and extra types
(`BeaconScannerFactory`, `BeaconRegistry`, `DestinationResolver`, `WayfinderBootstrap`)
that the design of record does not have. Treat §6 of `docs/pilot-architecture.md` as
correct and this scaffold as provisional. Do not build on the scaffold's names.

## Conventions

- Namespace root: `Wayfinding` (`Wayfinding.Positioning`, `Wayfinding.Navigation`, ...).
- Two assembly definitions: `Wayfinding.Runtime.asmdef` (allowUnsafeCode, for AR CPU
  images) and `Wayfinding.Editor.asmdef`. Editor code stays in a folder named `Editor`
  so it never reaches the phone build.
- Layer boundaries are folder boundaries: `Data/`, `Positioning/`, `Navigation/`,
  `AR/`, `UI/`, `Tools/`.
- Positioning math stays free of `MonoBehaviour` so it can be unit-tested off-device —
  `Trilateration`, `RssiFilter` and `EddystoneFrame` have no Unity dependency.
- Coordinates are **map coordinates**: meters, origin at the floor's reference corner,
  +X and +Y along the hallway axes.
- The survey is a **build input**, not runtime data. It lives in the `FloorMap`
  ScriptableObject (`Assets/Resources/Floor4.asset`), sourced from
  `docs/floor-map-data.md`. Never hardcode beacon or room coordinates in a script.

## Where things are

| Path | What |
|---|---|
| `docs/pilot-architecture.md` | **Design of record.** Topology, data lifetimes, beacon config, rates, failure modes, privacy posture |
| `docs/build-sheet.md` | The 20-script manifest, the 7-step bring-up order, and five traps that cost a day each |
| `docs/how-it-works.md` | Three views: the visitor's journey, the system, and what fires when |
| `docs/architecture.md` | Earlier narrative walkthrough. **Superseded** — wrong on the filter, the solver, and Android permissions |
| `docs/floor-map-data.md` | Measured coordinates and what's still missing |
| `docs/setup-and-packages.md` | Packages, plugins, HDRP→URP, AWS build env |
| `docs/pilot-plan.md` | Scope, hardware, approvals, demo definition |
| `docs/open-questions.md` | Unresolved decisions |
| `Assets/Scripts/` | Provisional scaffold — see the warning above |

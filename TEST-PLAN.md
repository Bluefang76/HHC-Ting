# TEST PLAN — three tiers, in order

Each tier removes one unknown. Tier 1 needs nothing but this laptop and proves positioning works
at all. Tier 2 adds AR without needing a device. Tier 3 is the only one that needs real hardware —
by the time you get there, the only things left to debug are the ones that genuinely require a
phone and thirty beacons on a wall.

Do not skip ahead. Almost every bug in this app is reproducible at whichever tier introduces it,
where the debugger works — go to the corridor to confirm, not to discover.

---

## Tier 1 — Editor, `MockBeaconScanner`, no hardware, no AR

**What this proves:** the entire positioning pipeline — `EddystoneFrame` → `RssiFilter` →
`Trilateration` → `BeaconManager` — end to end, against ground truth.

### Setup

1. Run `Wayfinding ▸ Create Test Floor Map (4-beacon square)` if you haven't already.
2. New empty GameObject in a scene. Add `BeaconManager`, `MockBeaconScanner`, `DebugHud`. Point all
   three `floorMap` fields at the generated test asset. `DebugHud.mockScanner` → the
   `MockBeaconScanner` on the same object.
3. Press Play.

### What you should see

- `DebugHud`'s `SCANNER` line reads `Scanning`.
- The `BEACON` table fills in with four rows, `RSSI`/`DIST`/`W` columns populated, none stuck on
  `not heard`.
- Within a couple of seconds, `FIX` shows a position and `conf` above roughly 0.5.
- With `mockScanner` wired, a `TRUE` line appears with an `ERROR` figure. Watch it — it should
  settle down to roughly a metre as the filters warm up, and it should **not** flip between two
  very different values while the simulated walker moves smoothly. A flipping error, on this test
  map, means something is wrong in the code (this geometry was deliberately built non-collinear
  precisely so it shouldn't happen) — see causes below.
- Turn on `showMiniMap`: four green beacon dots around the corridor rectangle, a white ground-truth
  dot, an orange fix dot tracking near it.

### If it doesn't work

- **`FIX none — need 3 beacons in range`, forever.** `MockBeaconScanner.floorMap` and
  `BeaconManager.floorMap` are pointed at different assets (or one is unset) — `BeaconManager`
  only accepts readings for beacons it finds in *its own* `FindBeacon` lookup. Check both fields.
- **Beacons appear in the table but `DIST` is wildly wrong or the fix never stabilizes.** Check
  `advertisementsPerSecond` and `medianWindow` haven't been changed from their defaults (2 Hz / 5)
  on a fresh setup — a very small median window with `packetLossChance` still at its default 0.15
  can starve the filter of samples.
- **Nothing at all — the HUD just says "No BeaconManager assigned."** `DebugHud.beaconManager` is
  unset. This is the most common "I forgot a step" failure at this tier.

---

## Tier 2 — Editor, XR Simulation, AR without a device

**What this proves:** `ARWorldAligner`, `ARPathRenderer`, and the QR-anchor alignment math, all
without leaving the Editor. This is addition **C** — not in the original design, added because you
have no build machine yet and want the AR layer to be more than theoretical before your first
device build.

### Setup

1. Confirm `Project Settings ▸ XR Plug-in Management ▸ [Standalone tab] ▸ XR Simulation` is ticked
   (see `SETUP.md` §2). If AR Foundation put a "Simulation Environments" option under
   `Window ▸ XR ▸ AR Foundation` instead, use that to pick or build a simple flat-floor environment
   — a plain room with a visible ground plane is enough; you're testing alignment math, not
   realism.
2. Build the full scene from `SETUP.md` §4, all Inspector references wired, `floorMap` pointed at
   the test asset from Tier 1.
3. On `QrAnchorResolver`, set `editorAutoResolveCode` to the test map's QR code (`TEST-ENTRANCE`,
   if you used the generated test map as-is). This drives the whole alignment flow without needing
   ZXing or a camera image to actually decode — `ResolveCode` runs directly a moment after Play.
4. Press Play with the Game view visible (XR Simulation renders into it).

### What you should see

- A moment after entering Play, `ARWorldAligner.IsAligned` goes true (visible on `DebugHud`'s `AR`
  line) — `editorAutoResolveCode` triggered `QrAnchorResolver.AnchorResolved`, which
  `ARWorldAligner` is subscribed to.
- Once you've also got `MockBeaconScanner` driving a walker and `NavigationSession.SetDestination`
  called (a temporary button, same as Tier 1's manifest describes), the ribbon should appear in the
  simulated environment, flat on the ground, roughly 2 m ahead of the simulated camera position —
  not at its feet (`trimAheadMeters`, by design).
- Moving the simulated camera (WASD in the XR Simulation view, or however this AR Foundation
  version exposes it) should **not** make the line visibly slide relative to the simulated floor.
  If beacon drift correction is doing anything at all in a static test, you likely won't see it
  move at all in a short session — that's correct; it's supposed to be invisible.
- **This is also where addition A earns its keep.** Turn on `DebugHud.showManualAlignment`. Type a
  different X/Y/heading and press the button — `IsAligned` should flip briefly, the floor root
  should visibly reposition/rotate, and the racing line should still land in a sane place relative
  to the new alignment. If this works, you've exercised the entire AR alignment path without ever
  needing a QR code to physically exist.

### If it doesn't work

- **`IsAligned` never goes true.** `editorAutoResolveCode` doesn't match any `QrAnchor.code` in the
  FloorMap you're using — it's case-sensitive-by-trim, not fuzzy. Copy the exact string from the
  asset.
- **Aligned, but nothing draws.** Almost always a missing `NavigationSession.SetDestination` call —
  there's no path yet, so `ARPathRenderer` has nothing to build a ribbon from. Confirm
  `FloorGeometryBuilder.IsReady` is true first (needs AI Navigation installed and the NavMesh to
  have baked without error — check the console for "No valid hallway segments").
- **The Game view shows nothing at all, or a black screen, no environment.** XR Simulation has no
  active environment selected, or the "Standalone" XR Plug-in Management tab doesn't have XR
  Simulation ticked. This is a Project Settings problem, not a script problem — nothing in
  `Assets/Scripts` can cause this.

---

## Tier 3 — On-device, real beacons

**What this proves:** everything above, against reality, which is noisier than either simulation.

### Setup

1. Mount all beacons (or however many you currently have — the four-beacon test geometry from
   Tier 1 works equally well as a real four-beacon desk/room setup if that's what you have).
   Alternate sides if it's more than a couple — see the collinear-beacons warning in
   `Trilateration.cs` and `docs/build-sheet.md`.
2. `WAYFINDING_BLE_PLUGIN` and `WAYFINDING_ZXING` set on both platform tabs, both bridge regions
   filled in (`SETUP.md` §3).
3. Build to the device. Grant Camera and Bluetooth permissions when asked.
4. Open `DebugHud` on the phone (bind it to something reachable — a hidden gesture, or just leave
   it visible for now).

### Step order

1. **Confirm you can hear every beacon.** Walk near each one, watch it appear in the `BEACON`
   table with a live `RSSI`. Any that never appear: dead/unactivated battery (check the physical
   unit), wrong instance ID typed into the `FloorMap` (check against what a generic BLE scanner app
   sees), or genuinely out of range.
2. **Calibrate.** Run `BeaconSurveyTool` against every beacon — stand exactly 1 m away, hold
   Sample for the full duration, don't let your body block the signal. This is the single
   highest-value step in the whole pilot; skipping it doesn't break anything visibly, it just makes
   every distance wrong by a consistent, hard-to-diagnose amount.
3. **Expect the error to get worse before it gets better.** The simulator in Tier 1 has no metal
   carts, no propped-open fire doors, no six people standing in the corridor. Seeing the error
   climb when you switch from `MockBeaconScanner` to `BleBeaconScanner` is normal, not a regression
   — it should fall back down as calibration progresses.
4. **Scan the real QR code and watch the line.** The real test: does it stay stuck to the floor as
   you walk past it? A sliding line is an alignment problem (`ARWorldAligner`), not a positioning
   one (`BeaconManager`) — check `DebugHud`'s drift figure; if it's climbing steadily, correction
   isn't keeping up, and if it's low but the line still slides, AR tracking itself is struggling
   (plausible in a visually repetitive corridor — see `docs/pilot-architecture.md` on why that's
   expected).
5. **Hand the phone to someone who has never seen it, say nothing, watch where they hesitate.**
   This is the test that actually matters, and it's the one you can't do yourself — you already
   know where the rooms are.

### If it doesn't work

- **Position wanders far more than Tier 1, and confidence stays low even after calibration.**
  Check `GeometryQuality` on `DebugHud` (add it to the report if it's not already surfaced) — a
  low score means the beacons currently in range are too close to collinear at that point on the
  route, which is a mounting problem, not a code problem.
- **The app never leaves "Finding your position."** Fewer than 3 beacons in range at your current
  spot, or confidence sitting under 0.25 continuously — this is `NavigationSession` and
  `BeaconManager` behaving correctly (degrading honestly, per the design's own rule), the actual
  fix is beacon coverage.
- **Scan the QR code, nothing happens.** Check `WAYFINDING_ZXING` is set on the device's actual
  platform tab (not just the Editor) and that the QR code's encoded string exactly matches a
  `QrAnchor.code` in the `FloorMap` currently assigned on-device — easy to mismatch if you tested
  against the 4-beacon test map in Tiers 1–2 but built with the real survey's `FloorMap` for this
  tier.

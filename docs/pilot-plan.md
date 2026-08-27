# Pilot plan

## Scope

One floor, one building — the developer's own floor. A demo, not a deployment. The
goal is to put a working thing in a decision-maker's hands, on a hallway they know, so
the concept stops being a description.

Success for the demo: a person who has never seen the app scans the QR code at the end
of the hall, types a room number, and walks to the right door following the line
without being told what to do.

## Hardware

- ~30 BlueCharm BC011 BLE beacons — **ordered**.
- Budget pitched: roughly **$300**.
- Test phones: at least one Android (ARCore) and one iPhone (ARKit). Android is the
  cheaper build path from the cloud VM; iOS needs a Mac for the `.ipa`.

## Approvals

| Item | Status |
|---|---|
| Pitch email to Assistant Director — low-cost pilot on one floor, asking permission and support | Sent |
| Meeting with Assistant Director | Secured |
| Beacon vendor inquiry — range, placement, accuracy, battery-appropriate models at ~30 units | Sent |
| Permission to mount beacons on the floor | Not yet |
| IP ownership between developer and hospital | **Unresolved** |
| Pitch to high-ranking official | After the working demo |

Keep the project quiet until further approvals are secured.

## Milestones

1. **Positioning works at a desk.** Simulated scanner → filter → trilateration →
   plausible position. No hardware, no AR, runs in the Editor.
2. **Positioning works in the hallway.** Real beacons mounted, path-loss exponent
   calibrated, position tracks a walking person within a couple of meters.
3. **Map and path.** 1:1 replica of the floor, NavMesh baked, correct route computed
   from any hallway position to any of the 18 rooms.
4. **AR line.** Path drawn on the floor plane, anchored, holds still while walking the
   corridor end to end.
5. **QR entry + room input.** The actual user flow, ugly but complete.
6. **UI.** Last. Make it presentable for the demo.

## Risks worth naming in the meeting before someone else does

- **Accuracy.** BLE trilateration in a corridor is good to a few meters, not
  centimeters. The design compensates (QR origin fix, clamping to the walkable region,
  path snapping) but the honest claim is "guides you to the right door," not
  "centimeter-accurate."
- **Beacon maintenance.** Batteries die. 30 units on one floor is manageable; a
  campus-wide rollout needs an ownership answer for who replaces them.
- **Anything patient-facing.** No PHI is involved — the app knows room numbers, not
  who is in them — and it is worth stating that plainly and early, because it is the
  first question a compliance-minded reader will have.
- **Phone diversity.** Not every visitor phone supports ARKit/ARCore. A non-AR fallback
  (2D map with a dot) is the graceful answer, and it is worth having a sentence ready
  for it even if it is not built for the pilot.
- **IP.** Unresolved, and it gets harder to resolve the more is built. Worth settling
  before the project is valuable enough to argue about.

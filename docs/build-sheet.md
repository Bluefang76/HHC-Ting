# Racing Line Build Sheet

*Indoor wayfinding · pilot floor*

Twenty scripts, the order to bring them up in, and the five things that cost a day each
if nobody says them first.

Unity 6 · AR Foundation 6.x · 30 BC011 beacons · 20 scripts + 2 asmdef · iOS + Android ·
on-device only

> Ported from the "Racing Line Build Sheet" artifact.
> See **Reconciliation** at the bottom — this and `docs/pilot-architecture.md` disagree
> in two places. `pilot-architecture.md` is confirmed as the more recent document and is
> the design of record; where this sheet conflicts with it, this sheet is out of date.

---

## Flow — RSSI to a line on the floor

Each stage hands the next one a cleaner abstraction. Nothing downstream of
`BeaconManager` knows a radio exists.

```
IBeaconScanner      Raw advertisements, dBm
      ↓
RssiFilter          Median, then one-euro, then metres
      ↓
Trilateration       Gauss-Newton over 3+ anchors
      ↓
BeaconManager       One position fix, with confidence
      ↓
NavigationSession   NavMesh route, turn by turn
      ↓
ARPathRenderer      Ribbon mesh on the real floor
```

---

## Files — the manifest

★ marks the five files originally sketched. The rest are what those five need in order
to run.

### Data — `Assets/Scripts/Data/`

| File | What it is |
|---|---|
| `BeaconDefinition.cs` | One beacon: MAC and UUID, floor position, mount height, surveyed TX power, environment factor. |
| `RoomNode.cs` | One destination: room number, aliases, the door, and the standing point outside it that the path actually targets. |
| `HallwaySegment.cs` | One straight run of corridor — centre line plus width. The only geometry primitive in the project. |
| `FloorMap.cs` ★ | The ScriptableObject holding the whole survey. Owns the coordinate frame, the lookups, and a validator that catches survey typos before a demo does. |

### Positioning — `Assets/Scripts/Positioning/`

| File | What it is |
|---|---|
| `IBeaconScanner.cs` | The seam. Five members. Nothing above this line knows which BLE plugin you bought. |
| `BleBeaconScanner.cs` | The real radio, iOS and Android. Permissions, scan cycling, background handling. Plugin calls confined to one marked region. |
| `MockBeaconScanner.cs` | A simulated walker generating realistic RSSI with noise, packet loss and corner attenuation — plus ground truth to measure against. |
| `RssiFilter.cs` | Rolling median kills spikes, one-euro filter smooths adaptively, path loss converts dBm to metres. This is what stops the line trembling. |
| `Trilateration.cs` | Pure maths, no Unity. Weighted centroid seed, Gauss-Newton refinement, and a geometry-quality score that tells you when your beacon layout is the problem. |
| `BeaconManager.cs` ★ | The conductor. Chooses anchors, solves, rejects impossible movement, snaps to walkable floor, publishes one event. |

### Navigation — `Assets/Scripts/Navigation/`

| File | What it is |
|---|---|
| `FloorGeometryBuilder.cs` | Turns hallway coordinates into an actual triangle mesh and bakes NavMesh onto it. Without this every path query returns "no route" and the reason is not obvious. |
| `PathfindingEngine.cs` ★ | Stateless NavMesh wrapper. Snaps endpoints, rounds corners, resamples, and reports distance measured along the path rather than straight-line. |
| `NavigationSession.cs` | The state machine: routing, guiding, off-route, arrived. Throttles recomputes so the line does not shimmer, and turns geometry into "Turn left in 8 m". |

### AR — `Assets/Scripts/AR/`

| File | What it is |
|---|---|
| `QrAnchorResolver.cs` | Decodes the entrance code into a known position and heading. Decoder library confined to one marked region. |
| `ARWorldAligner.cs` | Reconciles three coordinate systems: hard alignment at the QR scan, then slow drift correction from beacons, plus floor-height tracking from detected planes. |
| `ARPathRenderer.cs` ★ | Builds the ribbon mesh, trims it ahead of the visitor, fades both ends, and scrolls the texture toward the destination. |

### UI, tools, editor — `Assets/Scripts/UI/` · `Tools/` · `Assets/Editor/`

| File | What it is |
|---|---|
| `UIController.cs` ★ | Five screens: scan, enter room, confirm, guide, arrived — plus Bluetooth error states written for a patient, not a developer. |
| `DebugHud.cs` | Live RSSI per beacon, solved position, alignment drift, and a plan-view mini-map. You will look at this more than at the app. |
| `BeaconSurveyTool.cs` | Stand 1 m away, hold Sample, and the measured TX power is written back into the FloorMap. About a minute per beacon. |
| `FloorMapEditor.cs` | Plan view that redraws as you type your paced coordinates, a validator, and three cleanup operations including endpoint welding. |

---

## Order — bring it up in this sequence

Each step is verifiable on its own. You never debug two unknowns at once — and the
first four need no hardware at all.

**1 · Survey the floor.** Type the right-side coordinates you already paced, then walk
the left side, the corners, and the corridor width. Watch the plan view redraw as you type.
*Verify:* a transposed digit looks like a corridor pointing into the car park.

**2 · Watch the dot move.** `BeaconManager` plus `MockBeaconScanner` plus `DebugHud`.
Tune the filters until the error between true and solved position settles around a metre.
*Verify:* error in metres, live on the HUD — this is the whole positioning problem,
solved at your desk.

**3 · Bake the floor.** Add `FloorGeometryBuilder`, turn on `visualizeFloor`. If corners
have gaps, run **Weld hallway endpoints** in the FloorMap inspector.
*Verify:* a grey slab in the shape of your hallways.

**4 · Draw a path.** `PathfindingEngine` and `NavigationSession`. Call
`SetDestination("412")` from a temporary button.
*Verify:* points running down the corridor and round the corner in the scene view.

**5 · Put it on the floor.** `ARWorldAligner` and `ARPathRenderer`, with
`editorAutoResolveCode` standing in for the camera. First device build.
*Verify:* the line stays still while you walk.

**6 · Real beacons.** Mount all thirty, then calibrate every one with
`BeaconSurveyTool`. Expect the error to rise when you switch off the mock — reality is
noisier — then fall back as you calibrate.
*Verify:* surveyed TX power on all 30, exported to JSON.

**7 · The UI.** Last, as planned. By now you know what the screens need to say.
*Verify:* someone who has never seen it can reach 412 without asking you anything.

---

## Traps — five things worth knowing first

### Do not mount the beacons in a straight line down one wall

A corridor makes this the natural choice and it is the worst one available.
Trilateration from collinear anchors is well determined along the line and nearly
undetermined across it — the solution can mirror to either side with almost the same
error, so the position flips wall to wall while the visitor stands still.

Alternate walls as you go down the corridor. The solver reports this as low geometry
quality, but no code recovers what the layout threw away.

### Survey the TX power — do not guess it

Distance goes as `10^((TxPower − RSSI) / 10n)`, so a 3 dB error in TX power is roughly
**30% error in every distance, forever**. At 10 m that is 3 m injected into every solve
from a number you typed.

Same model, same box, same firmware, and two beacons still land several dB apart
depending on battery, orientation, and what they are stuck to. A beacon taped to a steel
door frame is not the beacon it was on your desk.

### The QR code is not a nicety

Beacons give you a point, and a point has no direction. Heading could come from the
compass, except a steel-framed building full of motors puts indoor compass readings
**30–90° out** routinely — and a wrong heading draws the line down the wrong side of the
corridor no matter how good the position is.

The scan gives position and facing at once. Post the code where there is exactly one
comfortable way to stand and scan it.

### Start the line ahead of the visitor, not at their feet

Positioning is good to a metre or two, so a line drawn to where the app thinks their
feet are will visibly not start at their feet, and every error in the system becomes
something they can see. Starting it **two metres out** hides that behind the phone and
reads as intentional design.

### Correct drift slowly

AR tracking is smooth and slowly wrong. Beacons are jumpy and never drift. Reconciling
them at **15 cm/s** is invisible to the visitor and still outruns the drift; a hard snap
whenever they disagree makes the path jump, which reads as broken even when the average
position is better.

---

## Setup — packages and defines

| Package | Source | Needed for |
|---|---|---|
| AR Foundation | Unity Registry | The AR layer |
| ARKit XR Plugin | Unity Registry | iOS backend |
| ARCore XR Plugin | Unity Registry | Android backend |
| AI Navigation | Unity Registry | **Required.** NavMeshSurface and runtime baking |
| TextMeshPro | Unity Registry | UI text |
| A BLE plugin | Asset Store | Only `BleBeaconScanner` touches it |
| ZXing.Net | GitHub | QR decoding |

Two scripting define symbols switch on the hardware paths, in **both** the iOS and
Android tabs: `WAYFINDING_BLE_PLUGIN` and `WAYFINDING_ZXING`.

Without them everything still compiles and runs against the mock — that is deliberate,
so the project builds today, before the beacons and plugin licences arrive.

---

## Pitch — the privacy answer, stated plainly

The beacons transmit. They do not receive, they do not listen, and they cannot detect a
phone. Positioning happens entirely on the visitor's device: the app hears public
advertisements and does the arithmetic locally. Nothing about where anyone walked is
transmitted, stored, or retrievable — not by the app, not by the beacons, not by the
hospital.

That is a design choice worth protecting deliberately. "Does this track patients?" is
the first objection this idea will meet, and right now the answer is a clean **no**. The
moment a position is sent anywhere, it becomes *it depends*, and the conversation with
legal and IT security becomes a different conversation entirely.

---

## Reconciliation with `docs/pilot-architecture.md`

The two documents disagree in two places. **The architecture doc is the more recent of
the two — confirmed by the author — and wins both.**

**Beacon identity.** This sheet describes `BeaconDefinition.cs` as holding "MAC and
UUID". The architecture doc (§4) explicitly rejects both — MAC is not exposed on iOS,
and the CoreBluetooth peripheral UUID differs on every phone — and settles on the
**Eddystone-UID instance ID**. `BeaconDefinition` should carry the instance ID.

**Script count.** This sheet says 20 scripts; the architecture doc says 21 and adds
`Positioning/EddystoneFrame.cs` (365 lines, UID + TLM parser). That file is the
consequence of the identity decision above. Correspondingly `IBeaconScanner` grows from
**five members to six**.

Everything else — the flow, the build order, the traps, the packages, the defines — is
consistent between the two.

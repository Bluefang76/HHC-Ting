# Wayfinder Pilot Architecture

*Hospital indoor wayfinding · pilot · one floor*

A system design for something that is, deliberately, only one system: an app on a
phone. What exists, where every byte lives, how a build is made, and how it fails.

> Ported from the "Wayfinder Pilot Architecture" artifact. This document is the
> **design of record** — where it disagrees with anything else in this repo, it wins.

---

## 1 · Scope

One floor. Eighteen rooms. Thirty beacons. One QR code. One app, running on the
visitor's own phone, with nothing behind it.

| In scope | Explicitly out |
|---|---|
| Single-floor routing between 18 rooms | Multi-floor, elevators, stairs, between-building routes |
| Android first; the code is cross-platform | App Store or Play Store distribution |
| Beacon health from the beacons' own broadcasts | Any server, database, queue, account or analytics |
| Visual guidance — a line on the floor | Voice guidance and accessibility modes |
| Room lookup by number | Search by department, clinician, or appointment |

### The design constraint that shapes everything else

No position data leaves the phone, because there is nowhere for it to go. That is not
a limitation the pilot works around — it is the property that makes the pilot
approvable. The moment a byte of location crosses the device boundary, this stops
being a small request and becomes a PHI conversation with legal, IT security and a
business associate agreement attached.

---

## 2 · Deployment topology

Three kinds of thing exist in the world. Two of them are dumb, and one of them is the app.

**THE FLOOR — FIXED HARDWARE**

- 30 × BC011 beacons — Eddystone UID + TLM · 2 Hz · 0 dBm · *transmit only, cannot detect a phone*
- 1 × QR sticker — the only source of heading; an opaque code, not a URL
- No power, no wiring, no network, no configuration after mounting

**THE PHONE — EVERYTHING RUNS HERE**

| Stage | What it does |
|---|---|
| Sense | BLE scan + QR decode |
| Locate | filter → trilaterate → 8 Hz fix |
| Route | NavMesh path + turn instructions |
| Render | AR alignment + the racing line |

FloorMap survey — compiled into the app. Radio, one way. Camera, once.

**OFF-DEVICE — nothing**

No server · no database · no queue · no account · no analytics · no logs. The app
works with the phone in airplane mode. No traffic.

The struck-through arrow is the design. There is no outbound path — not disabled, not
configured off, **absent**. A reviewer can verify that by reading the dependency list:
the project contains no networking library, because there is nothing to send.

---

## 3 · Where every byte lives

"Where is the location information stored?" has two answers, because there are two
different things called location information here — and only one of them is stored at all.

| Data | Lives in | Size | Lifetime | Leaves the device |
|---|---|---|---|---|
| FloorMap asset | The app bundle. Compiled in, read-only, identical on every install. | ~6 KB | the build | never |
| RssiFilter windows | RAM. Five recent signal readings per beacon, overwriting. | ~2 KB | ~2.5 s | never |
| PositionFix | RAM. One struct, overwritten by the next solve. | 32 B | 125 ms | never |
| Current path | RAM. A list of points, replaced on recompute. | ~4 KB | ~3 s | never |
| Alignment transform | RAM. Position and rotation of the floor root. | 28 B | the session | never |
| Beacon telemetry | RAM. Latest battery voltage per beacon, from their own broadcasts. | ~1 KB | the session | never |
| `beacon_survey.json` | App-private storage — your phone, during calibration only. | ~3 KB | until deleted | manual export by you |

### What that means in plain terms

The map is stored. The person is not. The map — beacon 7 is at (24.5, 1.2) — ships
inside the app like a font or an image, a few kilobytes, the same on every device,
never written to. The visitor's position is computed eight times a second into a
variable that the next solve overwrites. Close the app and it is gone; there was never
a copy.

The one file written to disk holds radio calibration constants, on your own phone, from
a tool that never runs on the visitor path. Naming it is what makes the claim airtight
rather than approximately true.

### The tradeoff, stated honestly

Because the map is compiled into the build, changing a room number means shipping a new
app. That is fine for one floor and one pilot. It stops being fine at three buildings
with rooms shuffling monthly — and that, not scale or traffic, is the thing that would
eventually justify a backend. Note what it would be: a **content service**. Not a
location service.

---

## 4 · Beacon identity and configuration

Beacons are identified by their **Eddystone-UID instance ID** — six bytes that travel
inside the broadcast. That choice is forced by iOS, and it is worth being explicit about
why, because it is only discoverable after thirty units are on a wall.

| Identity scheme | Android | iOS | Verdict |
|---|---|---|---|
| MAC address | Works | Not exposed at all | Android-only |
| CoreBluetooth peripheral UUID | n/a | Different on every phone | Works on exactly one device |
| iBeacon major / minor | Works | Filtered out of CoreBluetooth; needs CoreLocation | Forces a Location permission prompt |
| **Eddystone-UID instance** | **Works** | **Works — service data is not filtered** | **One identity, one code path, Bluetooth permission only** |

### What to set on every unit, in the KBeacon app

| Setting | Value | Why |
|---|---|---|
| Eddystone-UID | on | The identity frame |
| Eddystone-TLM | on | Battery voltage — fleet health with no server |
| iBeacon | off | Nothing reads it, and it halves the rate of the frames that matter |
| Namespace | same on all 30 | Separates your deployment from anyone else's |
| Instance ID | `000000000001` … | Unique per beacon. Number them in mounting order and write it on the unit before it goes up |
| Advertising interval | 511 ms | 2 Hz. Roughly 8 months of battery instead of 16 at the default — and 8 months outlasts the pilot |
| TX power | 0 dBm | Then measure the real value at 1 m per unit with the survey tool |

A TLM frame carries battery but no identity — it is tied to a beacon only by arriving
from the same radio as that beacon's UID frame. The scanner correlates them by transport
address, which is useless as a global identity but perfectly good within one scanning
session.

---

## 5 · Runtime budget

Four systems on four different clocks. Getting these rates wrong is what makes a working
app feel broken — recompute the path on every position fix and the line shimmers;
rebuild the ribbon every frame and the battery dies.

| Runs | Rate | Why that rate |
|---|---|---|
| Beacon advertisement | 2 Hz each | Fixed in hardware. Faster costs battery life |
| RSSI filtering | per reading | Cheap; runs whenever a packet lands |
| Position solve | 8 Hz | Beacons only advertise at 2 Hz — solving much faster re-solves the same data |
| Path recompute | every 3 s | Any faster and each recompute shifts the line slightly. Off-route triggers it early |
| Ribbon rebuild | every 0.25 m | Tied to movement, not frames. Standing still costs nothing |
| Drift correction | 15 cm/s | Below the threshold of noticing, above the rate AR drifts |
| Yaw correction | 3°/s | Harder to observe, more visible when wrong — so slower |
| QR decode | 4 Hz | Only while the scan screen is up. Quarter-size greyscale frames |
| AR render | 30–60 fps | Whatever the device gives |

---

## 6 · Project tree

21 scripts, 2 assembly definitions, about 7,800 lines including comments. Layer
boundaries are folder boundaries, and the two assembly definitions keep editor code out
of the phone build.

```
HospitalWayfinding/
├── Assets/
│   ├── Scripts/
│   │   ├── Wayfinding.Runtime.asmdef        runtime assembly · allowUnsafeCode for CPU images
│   │   │
│   │   ├── Data/                            the survey — no behaviour, just facts
│   │   │   ├── BeaconDefinition.cs          147 · instance ID, position, calibration
│   │   │   ├── RoomNode.cs                  112 · door point + the walkable approach point
│   │   │   ├── HallwaySegment.cs            108 · centre line + width. The only geometry primitive
│   │   │   └── FloorMap.cs                  479 · the ScriptableObject. Coordinate frame + lookups
│   │   │
│   │   ├── Positioning/                     radio noise in, a point on the floor out
│   │   │   ├── IBeaconScanner.cs            160 · the plugin seam. Six members
│   │   │   ├── EddystoneFrame.cs            365 · UID + TLM parser. Pure bytes, unit-testable
│   │   │   ├── BleBeaconScanner.cs          511 · the real radio, iOS + Android
│   │   │   ├── MockBeaconScanner.cs         482 · synthetic radio with ground truth
│   │   │   ├── RssiFilter.cs                284 · median → one-euro → metres
│   │   │   ├── Trilateration.cs             332 · Gauss-Newton. No Unity dependency
│   │   │   └── BeaconManager.cs             624 · the conductor. Publishes PositionFix
│   │   │
│   │   ├── Navigation/                      a position and a destination in, a route out
│   │   │   ├── FloorGeometryBuilder.cs      378 · corridors → triangles → NavMesh bake
│   │   │   ├── PathfindingEngine.cs         405 · stateless. Snap, smooth, resample
│   │   │   └── NavigationSession.cs         619 · the state machine + turn instructions
│   │   │
│   │   ├── AR/                              make it stick to the real floor
│   │   │   ├── QrAnchorResolver.cs          260 · the only source of heading
│   │   │   ├── ARWorldAligner.cs            426 · reconciles AR space with floor space
│   │   │   └── ARPathRenderer.cs            444 · the ribbon mesh
│   │   │
│   │   ├── UI/
│   │   │   ├── UIController.cs              568 · five screens + Bluetooth error states
│   │   │   └── DebugHud.cs                  363 · IMGUI. RSSI, fix, drift, battery, mini-map
│   │   │
│   │   └── Tools/
│   │       └── BeaconSurveyTool.cs          309 · measures TX power at 1 m, writes it back
│   │
│   ├── Editor/                              MUST stay in a folder named Editor
│   │   ├── Wayfinding.Editor.asmdef         editor-only assembly
│   │   └── FloorMapEditor.cs                419 · plan view, validator, weld tool
│   │
│   ├── Plugins/                             you add these
│   │   ├── zxing.dll                        netstandard2.0 build only
│   │   └── (BLE plugin)                     Shatalmic, imported from the Asset Store
│   │
│   ├── Resources/
│   │   └── Floor4.asset                     your FloorMap instance — the survey lives here
│   │
│   ├── Materials/
│   │   └── RacingLine.mat                   unlit, transparent, vertex colour
│   │
│   └── Scenes/
│       └── Wayfinder.unity                  one scene. XR Origin, FloorRoot, Systems, Canvas
│
├── README.md                                wiring, packages, build order
├── SCRIPT_LIST.md                           the manifest
└── .gitignore                               Unity template. Library/ must not be committed
```

*Underlined in the original = the five files originally sketched
(`BeaconManager`, `PathfindingEngine`, `FloorMap`, `ARPathRenderer`, `UIController`).
Numbers are lines including comments.*

---

## 7 · Build and release

A "release" for this pilot is one signed APK and a phone to put it on. The interesting
part is not the pipeline — it is what counts as an input to it.

**Build pipeline**

Your laptop (VS Code over SSH + a DCV desktop session) → AWS VM (Unity 6.3 · Android
SDK, stopped between sessions) → Signed APK (IL2CPP · ARM64, ~60 MB) → Test phone
(sideload, or Firebase App Distribution) → A visitor walking to room 412.

**Build inputs**

- Private GitHub repo — 21 scripts · no licence file until IP ownership is settled
- FloorMap survey — 30 beacons · 18 rooms · corridors, compiled into the APK

> A room moves → edit the survey → new build → reinstall.
> Acceptable at one floor · not at three buildings.

No build server, no CI, no artifact registry, no release channel — one person, one
machine, one APK. Add CI when a second person can build.

The survey is a **build input**, not runtime data. That single fact is the honest limit
of this architecture, and it is worth naming in front of leadership before someone else
does — along with the fact that fixing it later is a content service, which is a much
smaller and less alarming thing than a location service.

### Versioning

Tag every build that goes on a phone: `pilot-0.3-floor4-2026-09-14`. Version, floor,
date. When a beacon gets moved or a room number changes, the tag is how you know which
phone is running which survey — and during a pilot, "which version is that phone on" is
a question you will ask more than you expect.

---

## 8 · Failure modes

Every one of these degrades to something honest rather than something confidently wrong.
That is the design rule: a visitor forgives "finding you", and does not forgive being
sent down the wrong corridor.

| When | Detected by | The app does |
|---|---|---|
| Fewer than 3 beacons in range | `BeaconManager` | Publishes nothing. UI keeps the last instruction and says "finding your position" |
| Position confidence below 0.25 | `Trilateration` | Stops showing a direction. No arrow is better than a wrong arrow |
| A fix implies 8 m/s of movement | `BeaconManager` | Clamps to walking pace and eases toward it instead of jumping |
| Fix lands inside a wall | `FloorMap` | Snaps to the nearest corridor centre line |
| More than 4 m off route for 2 s | `NavigationSession` | Recomputes silently. No scolding |
| AR and beacons disagree by > 8 m | `ARWorldAligner` | Stops correcting and asks for a QR rescan rather than dragging the world |
| Beacon battery below 2.5 V | Eddystone TLM | Warns in the log and flags it on the debug overlay. Replace it |
| Bluetooth off or denied | `BleBeaconScanner` | A plain-English screen explaining what to switch on, and that nothing is collected |
| Room number not found | `FloorMap` | Suggests the appointment letter, or the front desk. Never a dead end |
| No route between two valid points | `PathfindingEngine` | Names the likely cause in the log — usually two corridors that should meet and don't |

---

## 9 · Security and privacy posture

Written to be handed to whoever asks. Every line is checkable against the code rather
than being a promise about intent.

| Question | Answer |
|---|---|
| Does it track patients? | No. Positions are computed on the visitor's phone and overwritten eight times a second. There is no server to send them to. |
| Does it handle PHI? | No. It handles a room number the visitor types in, and discards it when they arrive. |
| Can the beacons detect a phone? | No. They transmit and do not receive. Functionally they are lighthouses. |
| What permissions does it request? | Camera and Bluetooth. **Not location** — that is a direct consequence of the Eddystone choice in section 4. |
| What network access does it need? | None. It runs in airplane mode. |
| What is stored on the device? | The floor map, compiled into the app. Nothing about the visitor. |
| Third-party code? | Two libraries: a BLE plugin and a QR decoder. Neither has network access in this app. |
| What if a phone is lost? | Nothing is exposed. The app holds no user data. |
| Interference with medical equipment? | BLE at 0 dBm — about one milliwatt — in the unlicensed 2.4 GHz ISM band, with adaptive frequency hopping designed for coexistence. Same class of emitter as a wireless keyboard. |
| How is it removed? | Thirty adhesive-mounted units come down in an hour and leave nothing behind. |

---

## 10 · What "the pilot worked" means

Worth agreeing before you start, because otherwise the answer becomes whoever's
impression on the day.

| Measure | Target | How you know |
|---|---|---|
| Positioning error | < 2.5 m | Debug overlay, walked end to end |
| Coverage | 3+ beacons everywhere | Beacon count never drops below 3 on the route |
| The line holds still | no visible slide | Walk past it and watch. This one is a judgement call and that is fine |
| Arrival | within 3.5 m | Ten runs to five different rooms |
| A stranger can use it | no questions asked | Someone who has never seen it reaches 412 unaided |
| Setup effort | documented | Hours to survey, mount and calibrate — the number leadership needs to multiply |

That last row is the one that matters for the decision that comes after the pilot. A
demo answers "does it work". Only the setup number answers "should we do this in eleven
more buildings", and it is the number nobody thinks to record until they need it.

---

*Wayfinder pilot architecture · one floor · 21 scripts · no backend · Unity 6 + AR Foundation 6.x*

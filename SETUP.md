# SETUP — from zero to a running project

Adapted from `WayfindingDependenciesandSetup.pdf` and the zip's own `README.md`, corrected for
where this project actually stands today: **no AWS VM yet** — you're getting Unity running
directly on this Windows laptop — and the project was created from the **HDRP** template, which
has to change before any AR Foundation work can start. Assumes you have never shipped an iOS
build before.

---

## 0 · Two decisions to make before you install anything

### 0.1 — Which Unity version

`CLAUDE.md` already flags this as unresolved: **6000.5.9f1 is what's installed on this laptop**;
`pilot-architecture.md`, `build-sheet.md`, and `WayfindingDependenciesandSetup.pdf` all specify
**Unity 6.3 LTS**, and the PDF gives a concrete reason — 6.0 LTS support ends October 2026, two
months from writing, while 6.3 LTS runs into December 2027. I'm not picking this for you; it's
exactly the kind of call the docs told me to flag rather than guess. If you install 6.3 LTS
through Unity Hub, everything below applies unchanged. If you stay on what's already installed,
substitute that version number everywhere and re-check the AR Foundation / AI Navigation /
XR Plug-in Management package versions against what's actually compatible with it — the versions
below are what the PDF verified in August 2026 against 6.3.

### 0.2 — HDRP → URP: convert this project, or start a new one

The project at `C:\Users\jgsab\HHC Ting` was created from Unity's HDRP 3D template. AR Foundation
requires URP (or Built-in) — HDRP does not run on mobile AR passthrough at all. Two ways through
this, and I didn't pick one for you because I can't verify either outcome without a compiler:

- **Convert in place.** Unity 6 ships a Render Pipeline Converter (`Window ▸ Rendering ▸ Render
  Pipeline Converter`). Point it at HDRP → URP. This project looks like an untouched template
  (`OutdoorsScene.unity`, the default `HDRP Balanced/High Fidelity/Performant` settings, the
  default `Readme.asset`) — nothing Wayfinding-specific depends on HDRP, so there's very little
  for the converter to get wrong. Delete `Assets/Settings/HDRP*` and `OutdoorsScene.unity`
  afterward; none of it is used.
- **Start a fresh URP 3D project** and copy `Assets/Scripts/`, `Assets/Editor/`, and this repo's
  `docs/` folder across. Slightly more setup work, zero conversion risk. Given how little this
  project currently has beyond template boilerplate and the scripts you just got, this is
  probably the less fiddly option — but it's your call.

Either way, confirm **before** touching packages below: `Edit ▸ Project Settings ▸ Graphics` should
show a **Universal Render Pipeline Asset**, not an HDRP one.

---

## 1 · Drop the scripts in

Already done by the reconciliation — `Assets/Scripts/` and `Assets/Editor/` are populated. If you
went the "fresh project" route in 0.2, copy those two folders (plus `docs/`, `CLAUDE.md`,
`CHANGES.md`, this file, and `TEST-PLAN.md`) into the new project now.

`Assets/Editor/` must stay a folder literally named `Editor` — Unity treats that name specially.
Move `FloorMapEditor.cs` or `TestFloorMapMenu.cs` out of it and they'll try to compile into your
phone build and fail.

At this point the console will be full of errors. Expected — the packages aren't in yet.

---

## 2 · Packages

`Window ▸ Package Manager ▸ Unity Registry`. Install in this order:

| Package | Why |
|---|---|
| AR Foundation | The AR layer — camera passthrough, plane detection, world tracking, CPU image access |
| Apple ARKit XR Plugin | iOS backend behind AR Foundation |
| Google ARCore XR Plugin | Android backend behind AR Foundation |
| **AI Navigation** (`com.unity.ai.navigation`) | `NavMeshSurface` + runtime baking. `FloorGeometryBuilder.cs` will not compile without it — this is the one people forget |
| TextMeshPro | UI text. Ships with Unity 6; import its Essential Resources once (`Window ▸ TextMeshPro ▸ Import TMP Essential Resources`) |

`XR Plug-in Management` is pulled in automatically by AR Foundation — you don't install it
separately, but you do need to *configure* it, next.

**Installing a package does not enable it.** `Project Settings ▸ XR Plug-in Management`:

- **iOS** tab → tick **ARKit**.
- **Android** tab → tick **ARCore**.
- **Windows, Mac, Linux** tab (or whatever this Editor calls the standalone tab) → tick **XR
  Simulation**. This is addition **C** — it's what lets `ARCameraManager`/`ARPlaneManager` produce
  a fake but plausible camera feed and detected planes inside the Editor Game view, with no
  device attached. If you don't see an "XR Simulation" checkbox here, it means your AR Foundation
  version bundles it differently — check `Window ▸ XR ▸ AR Foundation` for a "Simulation
  Environments" entry instead; the exact menu path has moved between AR Foundation point releases
  and I can't confirm which one applies to whatever version Package Manager resolves for you.

**Verification for this step:** the console clears, and `Create ▸ Wayfinding ▸ Floor Map` appears
in the Project window's right-click menu. If you still see errors naming `NavMeshSurface` or
`Unity.AI.Navigation`, AI Navigation didn't install; errors naming `ARCameraManager` mean AR
Foundation didn't.

---

## 3 · The two gated dependencies

Both are confined to one file each, behind a scripting define, so the project compiles and runs
against `MockBeaconScanner` whether or not either of these is installed. Do this whenever the
hardware/licence actually arrives — not before.

### 3.1 — BLE plugin

Any Unity BLE plugin works; `BleBeaconScanner.cs` is the only file that touches it. The docs were
written against Shatalmic's *Bluetooth LE for iOS, tvOS and Android* ($20, Asset Store).

1. Buy/import it (`Window ▸ Package Manager ▸ My Assets` if from the Asset Store).
2. `Project Settings ▸ Player ▸ Other Settings ▸ Scripting Define Symbols` → add
   `WAYFINDING_BLE_PLUGIN` to **both** the iOS and Android tabs. This field is per-platform;
   setting it on one tab only is a documented half-hour lost.
3. Open `BleBeaconScanner.cs`, find the `PLUGIN BRIDGE` region at the bottom, and fill in the four
   method bodies. The Shatalmic-shaped example is already there as a comment.

Three specific traps, already called out in the file's comments — worth repeating because they
produce a scan that silently returns nothing:
- Use the callback that fires on **every advertisement**, not the one that fires once per newly
  discovered device.
- `rssiOnly` must be `false` — the beacon identity lives inside the payload bytes.
- Do **not** filter by service UUID at the radio level. iOS applies service-data filters
  inconsistently; scan wide and let `EddystoneFrame`/`FloorMap` discard what isn't yours.

### 3.2 — QR decoder (ZXing.Net)

1. Download `zxing.Net` 0.16.11 from NuGet (a `.nupkg` is a zip — rename and unzip it).
2. Take the `netstandard2.0/zxing.dll` build and drop it into `Assets/Plugins/`. The other target
   frameworks in that package won't load under IL2CPP.
3. Add `WAYFINDING_ZXING` to Scripting Define Symbols on both platform tabs.
4. Open `QrAnchorResolver.cs`, find the `DECODER BRIDGE` region, and uncomment/fill in
   `DecodeLuminance` — the exact code is already there as a comment.

Until both of these are wired up, `MockBeaconScanner` and `QrAnchorResolver.editorAutoResolveCode`
carry the whole app. That's deliberate — it's how you build and test everything below before a
beacon or a plugin licence exists.

---

## 4 · Scene setup

```
XR Origin
├── Camera Offset
│   └── Main Camera                    ← ARCameraManager, ARCameraBackground
├── ARSession
├── ARPlaneManager, ARRaycastManager   (on XR Origin)
│
FloorRoot                              ← empty; ARWorldAligner moves this
├── FloorGeometry                      ← FloorGeometryBuilder (+ MeshFilter/Renderer, auto NavMeshSurface)
└── PathRenderer                       ← ARPathRenderer (+ MeshFilter/Renderer). Must be a CHILD of FloorRoot.
│
Systems
├── BeaconManager                      ← BeaconManager + MockBeaconScanner + BleBeaconScanner
├── PathfindingEngine
├── NavigationSession
├── QrAnchorResolver
├── ARWorldAligner
└── DebugHud                           (disable before demos)
│
Canvas
└── UIController + the six screen roots (scan, enter room, confirm, guiding, arrived, error)
```

**Inspector wiring, in order:**

1. Every script with a `floorMap` field → your `FloorMap` asset (or the generated test one — see
   §6 below).
2. `BeaconManager.scannerComponent` → `BleBeaconScanner`. Leave `preferMockInEditor` on — the
   Editor will use `MockBeaconScanner` automatically when it's present, regardless of this field.
3. `MockBeaconScanner.floorMap` → the same asset.
4. `FloorGeometryBuilder.floorMap` → the asset. This component lives under `FloorRoot`.
5. `PathfindingEngine` → `floorMap`, `floorRoot` = `FloorRoot`, `geometryBuilder` = `FloorGeometry`.
6. `NavigationSession` → `beaconManager`, `pathfindingEngine`, `floorMap`, `headingSource` = Main
   Camera.
7. `QrAnchorResolver` → `floorMap`, `cameraManager` = Main Camera's `ARCameraManager`.
8. `ARWorldAligner` → `floorRoot`, `arCamera` = Main Camera, `floorMap`, `beaconManager`,
   `qrResolver`, `planeManager`.
9. `ARPathRenderer` → `navigationSession`, `arCamera` = Main Camera.
10. `UIController` → everything, plus the six screen GameObjects.
11. `DebugHud` → `beaconManager`, `navigationSession`, `worldAligner`, `floorMap`, and
    `mockScanner` while testing. **To use addition A** (the manual alignment panel): tick
    `showManualAlignment` — `worldAligner` is already assigned from the rest of this list, no
    second reference needed.

**The racing line material.** URP ▸ Unlit, Surface Type Transparent, vertex colour enabled (the
fades ride on vertex alpha). Unlit matters — a lit material picks up the scene's virtual lighting,
which has nothing to do with the hospital's fluorescent tubes, and that mismatch is what makes AR
overlays read as pasted-on rather than painted-on.

---

## 5 · Platform player settings

**Android**

| Setting | Value |
|---|---|
| Scripting backend | IL2CPP |
| Target architectures | ARM64 |
| Minimum API level | 26+ (Project Validation will raise this if ARCore needs more — use its Fix button) |
| Target API level | 36 (Android 16) if you'll ever distribute through Play; harmless to set now regardless |
| ARCore support | Required |

Manifest permissions:

```xml
<uses-permission android:name="android.permission.CAMERA" />

<!-- Android 12 and up -->
<uses-permission android:name="android.permission.BLUETOOTH_SCAN"
                  android:usesPermissionFlags="neverForLocation" />
<uses-permission android:name="android.permission.BLUETOOTH_CONNECT" />

<!-- Android 11 and below only -->
<uses-permission android:name="android.permission.ACCESS_FINE_LOCATION"
                  android:maxSdkVersion="30" />
```

`neverForLocation` matters more than it looks — without it the app is formally declaring that it
derives physical location from Bluetooth scanning, which is not true and invites exactly the
question CLAUDE.md says this project exists to have a clean answer to.

**iOS**

| Setting | Value |
|---|---|
| Target minimum iOS | 13.0+ |
| Architecture | ARM64 |
| Requires ARKit support | Enabled |
| Scripting backend | IL2CPP (only option on iOS) |

Info.plist keys (Player Settings, or edit the generated Xcode project directly):

- `NSCameraUsageDescription` — *"Used to show the route ahead of you through your camera."*
- `NSBluetoothAlwaysUsageDescription` — *"Used to work out where you are inside the building. The
  app does not connect to anything and does not collect information about you."*

Write both for a patient standing in a lobby, not a reviewer — Apple reads them too, and a vague
string is a common rejection reason.

---

## 6 · Get positioning working with no hardware at all

You said you have a few real beacons but want good anchor geometry rather than a collinear
corridor to start with. `Wayfinding ▸ Create Test Floor Map (4-beacon square)` (menu bar) does
exactly this — generates and saves a small `FloorMap` asset: one 10 m corridor, four beacons at
the corners of a rectangle straddling it, one test room, one QR anchor. Point `BeaconManager`,
`MockBeaconScanner`, and `DebugHud` at it and press Play — see `TEST-PLAN.md` Tier 1.

When you're ready to survey the real floor: `Assets ▸ Create ▸ Wayfinding ▸ Floor Map`, and work
through `docs/floor-map-data.md` with the `FloorMapEditor` inspector's plan view open so a
transposed digit is visible as a picture rather than buried in a list of numbers.

---

## 7 · Getting a build onto a phone

### 7.1 — Android

Straightforward, and needs nothing beyond this laptop:

1. On the phone: `Settings ▸ About ▸ tap Build number 7×` → Developer options → USB debugging on.
2. `File ▸ Build Settings ▸ Android ▸ Build` (or **Build and Run** over USB for faster iteration).
3. Sideload the APK.

### 7.2 — iOS, with no Mac yet

Unity produces an Xcode project, not an installable app — turning that into something on your
iPhone has always needed a Mac, somewhere. Since you said you don't have one immediately:

**Unity Build Automation** (formerly Unity Cloud Build) builds and signs iOS in the cloud with no
Mac of your own. The free tier covers roughly 100 Mac build minutes/month, comfortably enough for
a pilot. You still need an **Apple Developer account** ($99/year) for the signing certificate and
provisioning profile — there's no way around that requirement regardless of which machine does the
building. This is the recommended path given your constraints.

The alternative — borrowing a Mac for the (rare) occasions you need one, or an EC2 Mac instance
(which carries a 24-hour minimum allocation per Apple's licence terms, poor value for occasional
use) — are both viable later but add friction now.

**Practical sequencing point, not in the original docs but worth stating given your setup:**
Android needs nothing beyond this laptop. iOS needs an Apple Developer account either way. If the
first goal is "see the racing line move," it's worth getting an Android build working first even
though iOS is your actual target device — every script here is already cross-platform, so nothing
is lost by sequencing it that way, and it separates "is the app working" from "is the iOS pipeline
working" instead of debugging both at once.

---

## 8 · What's still open after this document

- The HDRP→URP decision in §0.2 — unresolved, on purpose.
- The Unity version reconciliation in §0.1 — unresolved, on purpose.
- The scene itself doesn't exist yet — §4 is a spec, not a built scene.
- `docs/floor-map-data.md` says what survey data is still missing for the real floor.
- Nothing here was verified by a compiler. Treat every menu path and setting name as "this is what
  the source docs said as of August 2026" rather than "this is confirmed against the Editor you'll
  actually be looking at."

# Setup and packages

## Current project state

- Unity **6000.5.9f1**, project folder `HHC Ting`.
- Created from the **HDRP** template — `com.unity.render-pipelines.high-definition`
  17.5.0, HDRP settings assets under `Assets/Settings/`, an `OutdoorsScene` sample, and
  `Assets/TutorialInfo/`.
- **No AR packages installed yet.** No AR Foundation, no ARKit XR Plugin, no ARCore XR
  Plugin, no AI Navigation, no TextMeshPro, no BLE plugin, no ZXing.

Two mismatches against the design of record, both worth settling early:

- The architecture doc specifies **Unity 6.3**; installed is **6000.5.9f1**. Pick one
  before the AWS image is built, so the VM and the laptop agree.
- The architecture doc's tree is rooted at `HospitalWayfinding/`; the actual folder is
  `HHC Ting`. Cosmetic, but rename one or the other before the repo goes up.

## ⚠️ HDRP must go first

HDRP does not support mobile AR. AR Foundation's camera background, and the ARKit and
ARCore providers, target URP or built-in. Nothing AR-related will work until the render
pipeline changes.

Cleanest path, given the project is effectively empty of real content:

1. Create a fresh Unity project from the **Universal 3D (URP)** template and move the
   repo files into it, **or** remove the HDRP package and its settings assets and
   install URP, then assign a URP asset in Graphics/Quality settings.
2. Delete the template leftovers: `Assets/OutdoorsScene.unity`, `Assets/TutorialInfo/`,
   `Assets/Settings/HDRP*.asset`, `Assets/Settings/HDRPDefaultResources/`,
   `Assets/Readme.asset`.
3. Confirm the Graphics settings point at a URP asset and the project still opens clean.

Starting from a URP project is less work than converting. Do it before writing AR code.

## Packages

| Package | Source | Needed for |
|---|---|---|
| Universal RP | Unity Registry | Render pipeline AR Foundation supports |
| AR Foundation (6.x) | Unity Registry | The AR layer |
| ARKit XR Plugin | Unity Registry | iOS backend |
| ARCore XR Plugin | Unity Registry | Android backend |
| AI Navigation | Unity Registry | **Required.** NavMeshSurface and runtime baking |
| TextMeshPro | Unity Registry | UI text |
| A BLE plugin — **Shatalmic** | Asset Store | Only `BleBeaconScanner` touches it |
| ZXing.Net (`zxing.dll`, netstandard2.0) | GitHub | QR decoding — only `QrAnchorResolver` touches it |

`com.unity.modules.ai` is already in the manifest — that is the base NavMesh API, but
`NavMeshSurface` and runtime baking come from the **AI Navigation** package.

Third-party code is exactly two libraries, and **neither has network access in this
app** — that claim is part of the privacy posture, so keep it true.

## Scripting define symbols

Set in **both** the iOS and Android tabs:

- `WAYFINDING_BLE_PLUGIN`
- `WAYFINDING_ZXING`

Without them everything still compiles and runs against `MockBeaconScanner` — that is
deliberate, so the project builds today, before the beacons and plugin licences arrive.
Plugin and decoder calls stay confined to one marked region in `BleBeaconScanner` and
`QrAnchorResolver` respectively.

## Permissions

**Camera and Bluetooth. Not Location.**

That is a direct consequence of identifying beacons by Eddystone-UID instance ID rather
than iBeacon major/minor — reaching iBeacon data on iOS requires CoreLocation, which
forces a Location permission prompt. The short permission list is load-bearing for the
privacy pitch. Do not do anything that adds Location.

- **iOS:** camera usage description (required — the app is rejected without it),
  Bluetooth usage description, ARKit requires Metal, minimum iOS version per AR
  Foundation 6.x.
- **Android:** ARCore requires ARM64 + IL2CPP, minimum API level per ARCore,
  `BLUETOOTH_SCAN` declared with `neverForLocation` so the Location permission is not
  pulled in.

## Build and remote environment

The laptop cannot run Unity. Development happens on an **AWS VM** over VS Code SSH plus
a DCV desktop session, stopped between sessions.

- Output: signed APK, **IL2CPP, ARM64, ~60 MB**. Sideload, or Firebase App Distribution.
- Android ships first — it builds entirely on the VM. iOS still needs a Mac for the
  `.ipa`, so it is not on the pilot path.
- No CI, no artifact registry, no release channel. One person, one machine, one APK.
  Add CI when a second person can build.
- Tag every build that goes on a phone: `pilot-0.3-floor4-2026-09-14` — version, floor,
  date. During a pilot, "which version is that phone on" gets asked more than expected.
- Keep `Library/` off the repo (already in `.gitignore`) but on fast local disk on the VM.

## Assembly definitions

- `Assets/Scripts/Wayfinding.Runtime.asmdef` — `allowUnsafeCode` for AR CPU images.
- `Assets/Editor/Wayfinding.Editor.asmdef` — editor-only. The folder **must** be named
  `Editor` so `FloorMapEditor` and friends never reach the phone build.

# CHANGES — reconciliation, phase 2

What happened, in order: Phase 1 (read-only) confirmed the 15 files that were in `Assets/Scripts/`
were the provisional scaffold `CLAUDE.md` already warned about — wrong namespace (`Wayfinder`, not
`Wayfinding`), wrong folder names, missing files, and a `BeaconDefinition` that stored a MAC/UUID
instead of an Eddystone-UID instance ID. Before rewriting all 21 files from the design doc by hand,
I checked `Wayfinding-Scripts-v2.zip` (already open earlier in this session) against its two
companion PDFs (`WayfindingDependenciesandSetup.pdf`, and the six-episode `Untitled document.pdf`)
and against all four design docs. It turned out to be a complete, already-correct 21-script
implementation of the same design — every rate, threshold, and algorithm in it (8 Hz solve,
median-5 → one-euro filtering, Gauss-Newton seeded from a weighted centroid with a 5 m step clamp,
3 s recompute / 4 m off-route / 2 s grace, 15 cm/s drift + 3°/s yaw correction, the 2 m line
lead-in, the six-screen UI flow) matches the docs exactly. I found **zero contradictions** between
that codebase and the design of record — a genuinely unusual result, and I read every one of the
21 files plus both asmdefs before concluding that, not just the ones the PDFs called out by name.

So Phase 2 became: adopt that codebase as the real `Assets/Scripts/`, delete the throwaway
scaffold, and layer your three requested additions and a small validator improvement on top.

---

## Removed — the provisional scaffold (Wayfinder namespace, predates the design docs)

All 15 files, confirmed identical to what `CLAUDE.md` described:

- `Assets/Scripts/Core/WayfinderBootstrap.cs`
- `Assets/Scripts/Beacons/` — `BeaconReading.cs`, `IBeaconScanner.cs`, `BeaconScannerFactory.cs`,
  `SimulatedBeaconScanner.cs`, `RssiFilter.cs`, `Trilateration.cs`, `BeaconRegistry.cs`,
  `BeaconManager.cs` (whole folder)
- `Assets/Scripts/Mapping/` — `FloorMap.cs`, `MapCoordinateSystem.cs` (whole folder)
- `Assets/Scripts/Navigation/DestinationResolver.cs`, `Assets/Scripts/Navigation/PathfindingEngine.cs`
- `Assets/Scripts/AR/ARPathRenderer.cs`
- `Assets/Scripts/UI/UIController.cs`

## Added — the audited codebase (Wayfinding namespace, matches the design of record)

Copied verbatim from the zip, no edits, grouped by layer:

- **Data** — `BeaconDefinition.cs`, `RoomNode.cs`, `HallwaySegment.cs`
- **Positioning** — `IBeaconScanner.cs`, `EddystoneFrame.cs`, `BleBeaconScanner.cs`,
  `MockBeaconScanner.cs`, `RssiFilter.cs`, `Trilateration.cs`, `BeaconManager.cs`
- **Navigation** — `FloorGeometryBuilder.cs`, `PathfindingEngine.cs`, `NavigationSession.cs`
- **AR** — `QrAnchorResolver.cs`, `ARPathRenderer.cs`
- **UI / Tools** — `UIController.cs`, `BeaconSurveyTool.cs`
- **Editor** — `FloorMapEditor.cs`
- Two assembly definitions: `Assets/Scripts/Wayfinding.Runtime.asmdef`,
  `Assets/Editor/Wayfinding.Editor.asmdef`

(`Data/FloorMap.cs`, `AR/ARWorldAligner.cs`, and `UI/DebugHud.cs` were also copied from the zip,
then edited — see below.)

**Not copied:** the zip's own `README.md` and `SCRIPT_LIST.md`. Your `docs/pilot-architecture.md`
and `docs/build-sheet.md` already cover the same ground and are the actual design of record for
this repo — adding a second, slightly-differently-worded copy of the same information at the
project root would just be something else to keep in sync.

## Edited beyond a straight copy

**`Assets/Scripts/Data/FloorMap.cs`** — added three checks to `Validate()` that weren't in the
zip's version:
- A beacon whose `position` is still `(0, 0)` — i.e., never surveyed — is now flagged, distinct
  from a beacon that was surveyed and genuinely landed there.
- A room whose `doorPosition` is still `(0, 0)` gets the same treatment.
- A new private method, `FindUnweldedJunctions()`, checks every pair of hallway segments for
  endpoints that are close (≤ 0.6 m, matching `FloorMapEditor`'s own weld radius) but not
  touching, and reports them by name — the "two corridors that should meet and don't" failure
  mode from `pilot-architecture.md` §8, which previously only showed up as an unexplained
  `PathResult.NoRoute` with no pointer back to the survey.

This directly closes the gap between what you asked the validator to catch and what the zip's
version actually caught (it already had duplicate-instance-ID and unreachable-approach-point
checks; it didn't have unsurveyed-position or unwelded-junction checks).

**`Assets/Scripts/AR/ARWorldAligner.cs`** — added `AlignManual(Vector2 mapPosition, float
headingDegrees)`. It builds a throwaway `FloorMap.QrAnchor` and runs it through the existing
`AlignTo(...)`, so it's exercising the exact same alignment math a real QR scan uses — it just
skips the camera and the decode step. Marked in its doc comment as a departure from the design of
record and as something that must never be reachable from the visitor path.

**`Assets/Scripts/UI/DebugHud.cs`** — added the manual-alignment control (addition **A**):
a `showManualAlignment` toggle (off by default) and, when it's on and `worldAligner` is assigned,
a small IMGUI panel with X/Y/heading text fields and an "Align here, facing this way" button that
calls the method above. Uses the HUD's existing `worldAligner` reference rather than adding a
second one.

## Added — new files (explicit departures from the 21-script manifest)

**`Assets/Editor/TestFloorMapMenu.cs`** (addition **B**) — a new menu item, `Wayfinding > Create
Test Floor Map (4-beacon square)`. It builds and saves a small synthetic `FloorMap` in memory via
`ScriptableObject.CreateInstance` and `AssetDatabase.CreateAsset` (avoiding the need to hand-author
a `.asset` YAML file with a script GUID I have no way to know without Unity having assigned one):
one 10 m test corridor, four beacons at the corners of a rectangle straddling it — deliberately
*not* collinear, which is the good-anchor-geometry case `Trilateration.cs`'s own class comment
warns you to build toward — one room, and one QR anchor. Every coordinate in it is invented on
purpose; it's a test fixture, not survey data, and the file's doc comment says so explicitly so
it's never mistaken for real floor data later.

**Addition C (XR Simulation)** needed no code changes — `ARCameraManager`/`ARPlaneManager` and the
rest of the AR layer talk to whatever XR provider is active without knowing which one it is, and
XR Simulation is just another provider. It's entirely a package + Project Settings step — see
`SETUP.md`.

## Untouched

- `Assets/Editor/HubForceResolve.cs` (+ its `.meta`) — a generic Unity Hub package-resolve
  bootstrapper that self-deletes after its first successful run. Not part of Wayfinding, doesn't
  reference any Wayfinding type. Left alone.
- Every file under `docs/` — the design of record, unchanged.
- `Packages/manifest.json`, `ProjectSettings/`, the render pipeline, the scene(s) — none of this
  was touched. The project is still on HDRP and still has no AR Foundation / AI Navigation / XR
  Simulation packages installed. See `SETUP.md`.

---

## What I was unsure of, or could not verify

**I cannot compile any of this.** There is no Unity on this machine. Everything above is unseen by
a compiler. Specifically:

- The three new checks in `FloorMap.Validate()` and the new `AlignManual` / `DebugHud` panel are
  written to match the surrounding code's style and the existing public API exactly as I read it,
  but I have not seen any of it build.
- `DebugHud`'s new panel mixes `GUILayout.BeginArea`/layout controls with the HUD's existing
  immediate-mode `GUI.Box`/`GUI.Label` calls. `BeaconSurveyTool.cs` (also from the zip, untouched)
  does the same kind of mixing in its own `OnGUI`, so the pattern is already proven to work in this
  codebase — but I haven't run it.
- The zip's own `IBeaconScanner.cs` doc comment says the interface is "written against these six
  members." Counting the actual interface, I get eight (three events, one property, four methods).
  That's a pre-existing mismatch in a file I copied but did not otherwise edit — I left the comment
  alone per your instruction to preserve comments unless they're now factually wrong, and this one
  was already wrong before I touched anything. Worth a look if you want the doc comment fixed, but
  it doesn't affect behavior.
- `DebugHud.cs` (unedited, from the zip) calls `Font.CreateDynamicFontFromOSFont("Courier New",
  fontSize)`. Neither iOS nor Android ships a font called "Courier New" by that exact name; I'd
  expect this to fail silently and fall back to Unity's default font rather than throw, but I
  haven't verified that on-device.
- I did not build or wire the Unity scene at all — no XR Origin, no `FloorRoot`/`Systems`/`Canvas`
  hierarchy, no Inspector references, no scene file. That's explicitly your job; `SETUP.md`'s scene
  section is what to follow when you get there.
- I did not touch `Packages/manifest.json`, `ProjectSettings/`, or the render pipeline. Getting an
  HDRP→URP conversion wrong outside the Editor, with no way to check the result, seemed like
  exactly the kind of "speculative change you'd normally verify by building" you told me to avoid.
  `SETUP.md` lays out the decision and both options.

## Checklist — verify these once this opens in Unity

- [ ] Project compiles clean, **after** the URP migration and package installs in `SETUP.md`
      (it will not compile before then — AR Foundation, AI Navigation, and TextMeshPro are all
      referenced by `Wayfinding.Runtime.asmdef` and none of those packages are installed yet).
- [ ] `Create ▸ Wayfinding ▸ Floor Map` appears in the Project window right-click menu (confirms
      `Wayfinding.Runtime.asmdef` compiled).
- [ ] `Wayfinding ▸ Create Test Floor Map (4-beacon square)` appears in the menu bar (confirms
      `Wayfinding.Editor.asmdef` and the new `TestFloorMapMenu.cs` compiled).
- [ ] Run the menu item above, open the generated asset, confirm the custom `FloorMapEditor`
      inspector renders (Validation / Plan view / Survey tools foldouts) without an exception.
- [ ] On that generated FloorMap, deliberately zero out one beacon's position or leave a hallway
      endpoint 20–30 cm off another's, and confirm the two new validator warnings actually fire —
      I wrote them against the field values but have never seen `Validate()` run.
- [ ] Build a minimal scene (`BeaconManager` + `MockBeaconScanner` + `DebugHud`, all pointed at the
      test FloorMap), enter Play mode, set `showManualAlignment` on `DebugHud` with `worldAligner`
      assigned, confirm the panel appears and pressing the button doesn't throw a null reference —
      this needs `ARWorldAligner` wired into the scene first (see `SETUP.md`).
- [ ] Positioning settles to a low error against the test FloorMap the way `TEST-PLAN.md`
      Tier 1 describes (this exercises `RssiFilter`, `Trilateration`, and `BeaconManager` together
      for the first time on your machine, even though none of those three were edited here).

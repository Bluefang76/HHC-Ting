# Reconciliation task — read this and follow it

Read these first, in full, before editing anything:

- `C:\Users\jgsab\HHC Ting\CLAUDE.md`
- `C:\Users\jgsab\HHC Ting\docs\pilot-architecture.md`
- `C:\Users\jgsab\HHC Ting\docs\build-sheet.md`
- `C:\Users\jgsab\HHC Ting\docs\how-it-works.md`

`pilot-architecture.md` is the **most recent** of these and is the design of record —
confirmed, not inferred. Where `build-sheet.md` conflicts with it, the build sheet is out
of date.

The C# scripts in the current folder are an earlier draft written before some of those
decisions were finalized, so where the code and the docs disagree, the docs are usually
right — but not always, and I want you to tell me which it is rather than guessing.

---

## Phase 1 — READ ONLY

**Do not edit any file yet.**

Go through every `.cs` file in this folder and produce a report covering:

**1. Every place the code contradicts the design of record.** The ones already known:

- **Beacon identity.** `BeaconDefinition` may hold a MAC address and/or CoreBluetooth
  UUID. The design uses the **Eddystone-UID instance ID** instead, because MAC is not
  exposed on iOS and the peripheral UUID differs per phone. This likely ripples through
  `IBeaconScanner`, `BleBeaconScanner`, `MockBeaconScanner` and `FloorMap`.
- **`IBeaconScanner` should expose six members, not five.**
- **`EddystoneFrame.cs`** (UID + TLM parser) may be missing entirely.
- **Any use of CoreLocation, `ACCESS_FINE_LOCATION`,** or anything else that would trigger
  a Location permission prompt. The app must request **Camera and Bluetooth only**.
- **Any networking code** — HTTP client, analytics, telemetry upload, logging to a server.
  There must be zero. Not disabled — *absent*.

**2. Anything that will not compile:** missing types, wrong signatures, unresolved
references, files referenced in the docs that do not exist here.

**3. Any file where you think the CODE is right and the DOCS are stale.** Say so
explicitly — this list matters more than the others.

**4. A file-by-file plan of the edits you propose,** grouped in this order:
`Data` → `Positioning` → `Navigation` → `AR` → `UI`/`Tools`/`Editor`.

**Then stop and wait for approval.**

---

## Phase 2 — EDITS

One group at a time, in the order above. After each group, stop and summarize what changed
before starting the next.

Rules:

- Do not rename or restructure anything beyond what the report calls for and I approve.
- Do not invent new types or files unless the design of record names them.
- Do not add any package, library, or dependency. If something appears to need one, stop
  and say so.
- Namespace root is **`Wayfinding`** (`Wayfinding.Positioning`, `Wayfinding.Navigation`, …).
- Keep BLE plugin calls confined to `BleBeaconScanner` and QR decoder calls confined to
  `QrAnchorResolver`, each inside a clearly marked region, gated on
  `WAYFINDING_BLE_PLUGIN` and `WAYFINDING_ZXING` respectively. Everything must still
  compile and run against `MockBeaconScanner` with **neither define set**.
- Positioning math (`Trilateration`, `RssiFilter`, `EddystoneFrame`) stays free of
  `MonoBehaviour` so it can be unit-tested off-device.
- Preserve existing comments and doc comments unless they are now factually wrong.

> **IMPORTANT: I cannot compile here.** This machine cannot run Unity — the build happens
> later on a remote VM. Do not assume anything compiles, and do not make speculative
> changes you would normally verify by building.

---

## When finished

Write `CHANGES.md` in this folder listing every file touched, what changed in each, and a
checklist of what specifically needs verifying once this opens in Unity on the VM. Be
honest in it about anything you were unsure of.

**Before the first edit in Phase 2**, run this if the folder is not already a git repo, so
there is a rollback point:

```
git init && git add -A && git commit -m "before reconciliation"
```

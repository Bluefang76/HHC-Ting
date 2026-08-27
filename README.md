# AR Wayfinder

Indoor augmented-reality wayfinding for a large hospital campus.

Scan a QR code at the entrance, enter a room number, and follow a path drawn over the
real world through your phone camera.

**Private repository. No license granted. Do not distribute.**

## Stack

- Unity 6 (6000.5.9f1)
- AR Foundation 6.x + ARKit XR Plugin + ARCore XR Plugin
- BLE beacon positioning (BlueCharm BC011), trilateration in C#
- Unity NavMesh pathfinding over a 1:1 virtual replica of the floor

## Getting started

The project does not build for AR out of the box — it was created from the HDRP
template and must be converted to URP first. Follow `docs/setup-and-packages.md`
before anything else.

Then: `docs/architecture.md` for how the pieces fit, `CLAUDE.md` for working
conventions.

## Status

Pilot MVP for a single floor. See `docs/pilot-plan.md`.

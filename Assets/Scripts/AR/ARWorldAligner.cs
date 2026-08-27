using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Wayfinding.Data;
using Wayfinding.Positioning;

namespace Wayfinding.AR
{
    /// <summary>
    /// Ties the virtual floor to the real one, and keeps it tied.
    ///
    /// This is the script that decides whether the racing line looks painted onto the hospital
    /// floor or looks like it is sliding around on top of it. It is worth understanding what it
    /// is reconciling, because three coordinate systems meet here and they do not agree:
    ///
    ///   FLOOR SPACE   your paced survey. Correct about the building, knows nothing about the
    ///                 phone. Never moves.
    ///   AR SPACE      what ARKit/ARCore builds from the camera. Beautifully stable over seconds
    ///                 and metres, and slowly, inevitably wrong over minutes — visual-inertial
    ///                 tracking drifts, especially down a long corridor where every stretch of
    ///                 wall looks like every other stretch of wall.
    ///   BEACON SPACE  where trilateration says you are. Correct on average, wrong at any given
    ///                 instant by a metre or two, and never drifts.
    ///
    /// The strategy: AR tracking provides the smooth, moment-to-moment motion. Beacons provide
    /// the slow, absolute truth. This script moves floorRoot so that the two agree — hard once,
    /// at the QR scan, then continuously and very gently afterwards.
    ///
    /// "Very gently" is the whole trick. A hard snap every time the beacons disagree makes the
    /// path visibly jump, which reads as broken even when the average position is better. A slow
    /// correction, a few centimetres a second, is invisible to the visitor and removes drift
    /// faster than AR accumulates it.
    /// </summary>
    public class ARWorldAligner : MonoBehaviour
    {
        [Header("Scene references")]
        [Tooltip("The transform every piece of floor geometry lives under. Moving this is how " +
                 "the virtual floor is lined up with the real one.")]
        public Transform floorRoot;

        [Tooltip("The AR camera transform (Main Camera under XR Origin).")]
        public Transform arCamera;

        public FloorMap floorMap;
        public BeaconManager beaconManager;
        public QrAnchorResolver qrResolver;

        [Tooltip("Optional. Used to place the floor at the real floor's height instead of guessing.")]
        public ARPlaneManager planeManager;

        [Header("Initial alignment")]
        [Tooltip("Assumed phone height above the floor, in metres, used until a horizontal plane " +
                 "is detected. 1.3 suits a phone held at chest height and looked at.")]
        public float assumedDeviceHeight = 1.3f;

        [Header("Drift correction")]
        [Tooltip("Correct alignment continuously from beacon positions after the QR scan. " +
                 "Without this the line drifts a metre or so over a few minutes of walking.")]
        public bool enableDriftCorrection = true;

        [Tooltip("Metres per second of positional correction. Deliberately slow — this should be " +
                 "invisible. 0.15 removes a metre of drift over about seven seconds.")]
        [Range(0.02f, 1f)]
        public float positionCorrectionSpeed = 0.15f;

        [Tooltip("Ignore beacon fixes below this confidence when correcting. A bad fix used for " +
                 "correction drags the whole world with it.")]
        [Range(0f, 1f)]
        public float minimumCorrectionConfidence = 0.45f;

        [Tooltip("Do not correct beyond this disagreement, in metres. A larger gap means " +
                 "something is genuinely wrong — the visitor took a lift, or tracking was lost — " +
                 "and the right answer is to ask for a rescan, not to drag the world across the " +
                 "building.")]
        public float maximumCorrectionMeters = 8f;

        [Tooltip("Correct heading as well as position. Slower and more cautious: yaw error is " +
                 "harder to observe and a wrong yaw correction is very visible.")]
        public bool enableYawCorrection = true;

        [Tooltip("Degrees per second of yaw correction. Keep this small.")]
        [Range(0.5f, 20f)]
        public float yawCorrectionSpeed = 3f;

        [Header("Floor height")]
        [Tooltip("Track the detected floor plane and keep the path sitting on it.")]
        public bool followDetectedPlane = true;

        [Tooltip("How quickly the floor height eases to a newly detected plane, in metres/second.")]
        public float heightAdjustSpeed = 0.5f;

        [Header("Debug")]
        public bool verboseLogging;

        /// <summary>True once the QR scan has established an alignment.</summary>
        public bool IsAligned { get; private set; }

        /// <summary>Current disagreement between AR-tracked and beacon-derived position, in metres.</summary>
        public float AlignmentErrorMeters { get; private set; }

        /// <summary>The floor plane's world height, best estimate.</summary>
        public float FloorHeight { get; private set; }

        private readonly List<Vector2> _recentFloorPositions = new List<Vector2>();
        private readonly List<Vector3> _recentArPositions = new List<Vector3>();
        private float _lastSampleTime;
        private bool _floorHeightKnown;

        private void OnEnable()
        {
            if (qrResolver != null)
            {
                qrResolver.AnchorResolved += OnAnchorResolved;
            }
        }

        private void OnDisable()
        {
            if (qrResolver != null)
            {
                qrResolver.AnchorResolved -= OnAnchorResolved;
            }
        }

        private void Update()
        {
            if (followDetectedPlane)
            {
                UpdateFloorHeight();
            }

            if (IsAligned && enableDriftCorrection)
            {
                UpdateDriftCorrection();
            }
        }

        // ------------------------------------------------------------------
        // Initial alignment
        // ------------------------------------------------------------------

        private void OnAnchorResolved(FloorMap.QrAnchor anchor)
        {
            AlignTo(anchor);
        }

        /// <summary>
        /// Places floorRoot so the QR anchor's floor coordinates land where the phone is standing
        /// right now, facing the way the anchor says the visitor is facing.
        ///
        /// The maths, plainly: work out the rotation that turns "floor space heading H" into
        /// "the direction the camera is currently pointing", apply it to floorRoot, then translate
        /// floorRoot so the anchor's floor point ends up under the camera.
        /// </summary>
        public void AlignTo(FloorMap.QrAnchor anchor)
        {
            if (anchor == null || floorRoot == null || arCamera == null || floorMap == null)
            {
                Debug.LogError("[ARWorldAligner] Cannot align - a reference is missing.");
                return;
            }

            // Which way the phone is pointing, flattened. Yaw in Unity is measured from +Z.
            Vector3 cameraForward = arCamera.forward;
            cameraForward.y = 0f;

            if (cameraForward.sqrMagnitude < 0.001f)
            {
                // Phone is pointed at the floor or ceiling; its yaw is meaningless. Fall back to
                // the device's up vector, which under those conditions points along the corridor.
                cameraForward = arCamera.up;
                cameraForward.y = 0f;
            }

            cameraForward.Normalize();
            float cameraYaw = Mathf.Atan2(cameraForward.x, cameraForward.z) * Mathf.Rad2Deg;

            // The same direction expressed inside the floor's own frame. FloorMap's convention is
            // 0 degrees = +X, and floor +X becomes Unity local +X, so a heading H is the local
            // direction (cos H, 0, sin H), whose Unity yaw is 90 - H.
            float localYaw = 90f - anchor.headingDegrees;

            float rootYaw = cameraYaw - localYaw;
            floorRoot.rotation = Quaternion.Euler(0f, rootYaw, 0f);

            // Now slide the root so the anchor point sits under the camera, at floor height.
            Vector3 cameraGroundPosition = arCamera.position;
            cameraGroundPosition.y = _floorHeightKnown
                ? FloorHeight
                : arCamera.position.y - assumedDeviceHeight;

            if (!_floorHeightKnown)
            {
                FloorHeight = cameraGroundPosition.y;
            }

            Vector3 anchorLocal = floorMap.FloorToLocal(anchor.position);
            floorRoot.position = cameraGroundPosition - (floorRoot.rotation * anchorLocal);

            IsAligned = true;
            AlignmentErrorMeters = 0f;

            _recentFloorPositions.Clear();
            _recentArPositions.Clear();

            // The beacon filters hold readings from before we knew where we were. Reset them and
            // seed the solver with the truth we just gained.
            if (beaconManager != null)
            {
                beaconManager.ResetFilters();
                beaconManager.SetKnownPosition(anchor.position);
            }

            Debug.Log($"[ARWorldAligner] Aligned to '{anchor.label}'. Floor root yaw " +
                      $"{rootYaw:F1} deg, position {floorRoot.position}.");
        }

        /// <summary>
        /// DEPARTURE FROM THE DESIGN OF RECORD — DebugHud-only, never called from the visitor path.
        ///
        /// Establishes alignment from a typed floor position and heading instead of a QR scan, by
        /// building a throwaway anchor and running it through the exact same <see cref="AlignTo"/>
        /// the real QR scan uses. Lets you test AR alignment and drift correction — the hardest
        /// part of this app to get right — before a QR code exists to scan, or before you are
        /// standing next to one.
        ///
        /// This bypasses the one honest source of heading the app has (see the class summary), so
        /// it must never be reachable from anything a visitor can touch.
        /// </summary>
        public void AlignManual(Vector2 mapPosition, float headingDegrees)
        {
            AlignTo(new FloorMap.QrAnchor
            {
                code = "(manual debug alignment)",
                position = mapPosition,
                headingDegrees = headingDegrees,
                label = "Manual debug alignment"
            });
        }

        // ------------------------------------------------------------------
        // Continuous correction
        // ------------------------------------------------------------------

        private void UpdateDriftCorrection()
        {
            if (beaconManager == null || !beaconManager.HasFix || floorMap == null)
            {
                return;
            }

            PositionFix fix = beaconManager.CurrentFix;

            if (fix.Confidence < minimumCorrectionConfidence)
            {
                return;
            }

            // Where the beacons say the visitor is, in world space under the current alignment.
            Vector3 beaconWorld = floorRoot.TransformPoint(floorMap.FloorToLocal(fix.FloorPosition));

            // Where AR tracking says they are.
            Vector3 arWorld = arCamera.position;

            Vector3 error = beaconWorld - arWorld;
            error.y = 0f;

            AlignmentErrorMeters = error.magnitude;

            if (AlignmentErrorMeters > maximumCorrectionMeters)
            {
                if (verboseLogging)
                {
                    Debug.LogWarning($"[ARWorldAligner] Alignment error {AlignmentErrorMeters:F1} m " +
                                     "exceeds the correction limit. Prompt for a QR rescan rather " +
                                     "than dragging the world.");
                }

                return;
            }

            // Move the floor toward agreement, slowly. Note the sign: the beacon point should end
            // up ON the camera, so the root moves by -error.
            float step = positionCorrectionSpeed * Time.deltaTime;
            Vector3 correction = Vector3.ClampMagnitude(-error, step);
            floorRoot.position += correction;

            if (enableYawCorrection)
            {
                UpdateYawCorrection(fix);
            }
        }

        /// <summary>
        /// Estimates yaw drift by comparing the direction the visitor has travelled according to
        /// the beacons with the direction AR says they have travelled. Over a few metres of
        /// walking those two vectors should point the same way; the angle between them is the
        /// accumulated yaw error.
        ///
        /// This only works while genuinely moving, over a decent distance, which is why the
        /// samples are collected over time and discarded if the visitor is standing still.
        /// </summary>
        private void UpdateYawCorrection(PositionFix fix)
        {
            const float sampleInterval = 0.5f;
            const int maxSamples = 8;
            const float minimumTravelMeters = 3f;

            if (Time.unscaledTime - _lastSampleTime < sampleInterval)
            {
                return;
            }

            _lastSampleTime = Time.unscaledTime;

            _recentFloorPositions.Add(fix.FloorPosition);
            _recentArPositions.Add(arCamera.position);

            if (_recentFloorPositions.Count > maxSamples)
            {
                _recentFloorPositions.RemoveAt(0);
                _recentArPositions.RemoveAt(0);
            }

            if (_recentFloorPositions.Count < maxSamples)
            {
                return;
            }

            // Beacon-derived travel, converted to metres and expressed as a world direction under
            // the current alignment.
            Vector2 floorDelta = _recentFloorPositions[maxSamples - 1] - _recentFloorPositions[0];
            float travelledMeters = floorMap.ToMeters(floorDelta.magnitude);

            Vector3 arDelta = _recentArPositions[maxSamples - 1] - _recentArPositions[0];
            arDelta.y = 0f;

            if (travelledMeters < minimumTravelMeters || arDelta.magnitude < minimumTravelMeters)
            {
                // Standing still, or shuffling. Direction of travel means nothing here.
                return;
            }

            Vector3 beaconDirection = floorRoot.TransformDirection(
                new Vector3(floorDelta.x, 0f, floorDelta.y)).normalized;
            Vector3 arDirection = arDelta.normalized;

            float yawError = Vector3.SignedAngle(arDirection, beaconDirection, Vector3.up);

            // Beacon direction over a few metres is itself noisy; anything under a few degrees is
            // not evidence of drift.
            if (Mathf.Abs(yawError) < 3f)
            {
                return;
            }

            float step = Mathf.Sign(yawError) *
                         Mathf.Min(Mathf.Abs(yawError), yawCorrectionSpeed * Time.deltaTime);

            // Rotate about the visitor, not about the root's origin, so correcting the heading
            // does not also shove the world sideways.
            floorRoot.RotateAround(arCamera.position, Vector3.up, step);

            if (verboseLogging)
            {
                Debug.Log($"[ARWorldAligner] Yaw drift {yawError:F1} deg, correcting {step:F2} deg.");
            }
        }

        // ------------------------------------------------------------------
        // Floor height
        // ------------------------------------------------------------------

        /// <summary>
        /// Finds the real floor from detected horizontal planes and eases the virtual floor onto
        /// it. Without this the path floats or sinks, and a path that floats ten centimetres off
        /// the ground is the single most common reason AR navigation demos look fake.
        /// </summary>
        private void UpdateFloorHeight()
        {
            if (planeManager == null || arCamera == null)
            {
                return;
            }

            float bestHeight = float.MaxValue;
            bool found = false;

            foreach (ARPlane plane in planeManager.trackables)
            {
                if (plane.alignment != UnityEngine.XR.ARSubsystems.PlaneAlignment.HorizontalUp)
                {
                    continue;
                }

                // Only planes plausibly beneath the visitor. A detected table top is horizontal
                // too, and is not the floor.
                float heightBelowCamera = arCamera.position.y - plane.center.y;

                if (heightBelowCamera < 0.8f || heightBelowCamera > 2.2f)
                {
                    continue;
                }

                Vector3 flatOffset = plane.center - arCamera.position;
                flatOffset.y = 0f;

                if (flatOffset.magnitude > 6f)
                {
                    continue;
                }

                if (plane.center.y < bestHeight)
                {
                    bestHeight = plane.center.y;
                    found = true;
                }
            }

            if (!found)
            {
                return;
            }

            _floorHeightKnown = true;
            FloorHeight = Mathf.MoveTowards(FloorHeight, bestHeight, heightAdjustSpeed * Time.deltaTime);

            if (floorRoot != null)
            {
                Vector3 position = floorRoot.position;
                position.y = FloorHeight;
                floorRoot.position = position;
            }
        }

        /// <summary>
        /// Clears alignment so the app returns to "please scan the code at the entrance".
        /// Call this when tracking is lost badly, or when the visitor changes floor.
        /// </summary>
        public void ResetAlignment()
        {
            IsAligned = false;
            AlignmentErrorMeters = 0f;
            _recentFloorPositions.Clear();
            _recentArPositions.Clear();
            qrResolver?.AllowRescan();
        }
    }
}

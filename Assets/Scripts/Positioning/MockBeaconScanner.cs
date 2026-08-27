using System;
using System.Collections.Generic;
using UnityEngine;
using Wayfinding.Data;

namespace Wayfinding.Positioning
{
    /// <summary>
    /// A fake radio that manufactures believable readings from the FloorMap's own beacon
    /// positions, so the entire app — filtering, trilateration, pathfinding, the racing line —
    /// can be built and tested in the editor with no hardware powered on.
    ///
    /// This is not a nicety. Debugging positioning while standing in a live hospital corridor,
    /// holding a phone, with a laptop balanced on a supply cart, is miserable and slow, and you
    /// cannot pause time to inspect a variable. Almost every bug you will hit is reproducible
    /// here, at your desk, where the breakpoints work.
    ///
    /// It also gives you something the real radio never will: ground truth. The simulated walker
    /// knows exactly where it is, so DebugHud can show you the solved position next to the true
    /// position and tell you your actual error in metres.
    /// </summary>
    public class MockBeaconScanner : MonoBehaviour, IBeaconScanner
    {
        public enum DriveMode
        {
            /// <summary>Walks the waypoint route on a loop. Good for hands-off testing.</summary>
            PatrolWaypoints,

            /// <summary>WASD / arrow keys move the simulated walker. Good for poking at edge cases.</summary>
            Keyboard,

            /// <summary>Follows a Transform, so you can drag a cube around the scene view.</summary>
            FollowTransform
        }

        [Header("Source data")]
        [Tooltip("Same FloorMap asset the rest of the app uses. Beacon positions come from here, " +
                 "which means the simulation stays honest when you edit the survey.")]
        public FloorMap floorMap;

        [Header("Simulated walker")]
        public DriveMode driveMode = DriveMode.PatrolWaypoints;

        [Tooltip("Floor-space waypoints for PatrolWaypoints mode. Leave empty and it will walk " +
                 "the hallway centre lines in order, which is usually what you want anyway.")]
        public List<Vector2> patrolWaypoints = new List<Vector2>();

        [Tooltip("Walking speed in metres per second. A visitor in a hospital corridor — often " +
                 "elderly, often anxious, sometimes pushing a wheelchair — averages closer to " +
                 "0.8 than the 1.4 you will find quoted for healthy adults outdoors.")]
        public float walkSpeed = 0.9f;

        [Tooltip("Target for FollowTransform mode.")]
        public Transform followTarget;

        [Tooltip("Where the walker starts, in floor space.")]
        public Vector2 startPosition;

        [Header("Radio realism")]
        [Tooltip("Advertisements per second, per beacon. BC011s ship around 1 Hz by default and " +
                 "can be configured up to 10 Hz. Faster gives better positioning and worse " +
                 "battery life — this is the main knob you will trade off in the pilot.")]
        [Range(0.5f, 10f)]
        public float advertisementsPerSecond = 2f;

        [Tooltip("Beyond this distance in metres, the beacon is treated as out of range and " +
                 "produces nothing. Real BC011s reach further in open air, but a hospital " +
                 "corridor with fire doors and people in it is not open air.")]
        public float maxRangeMeters = 25f;

        [Tooltip("Standard deviation of RSSI noise in dB. Real-world BLE in a busy corridor is " +
                 "3 to 6 dB. Turn this to 0 to check your maths in ideal conditions, then put it " +
                 "back to 4 and watch what actually happens.")]
        [Range(0f, 12f)]
        public float rssiNoiseDb = 4f;

        [Tooltip("Chance that any given advertisement is simply lost. Real radios drop packets, " +
                 "and code that assumes steady readings falls apart the first time one goes quiet.")]
        [Range(0f, 0.6f)]
        public float packetLossChance = 0.15f;

        [Tooltip("Extra signal loss in dB applied when a beacon is round a corner, rather than " +
                 "in line of sight. This is what breaks naive trilateration in an L-shaped " +
                 "hallway, so it is worth simulating rather than discovering on site.")]
        [Range(0f, 20f)]
        public float nonLineOfSightPenaltyDb = 8f;

        [Header("Telemetry")]
        [Tooltip("Emit simulated Eddystone-TLM battery frames, roughly every tenth advertisement, " +
                 "the way a real BC011 does. Lets you build and test the battery display without " +
                 "waiting months for a real cell to sag.")]
        public bool simulateTelemetry = true;

        [Tooltip("Battery millivolts reported by the simulated beacons. A fresh CR2477 reads about " +
                 "3000. Drag this down to 2400 to see how the debug overlay flags a dying unit.")]
        [Range(2200, 3100)]
        public int simulatedBatteryMillivolts = 2950;

        [Header("Debug")]
        [Tooltip("Build real Eddystone bytes and run them back through the real parser, instead of " +
                 "constructing readings directly. Costs nothing measurable and means the frame " +
                 "parser is exercised every time you press Play, rather than meeting real bytes " +
                 "for the first time in a corridor.")]
        public bool exerciseRealParser = true;

        public bool verboseLogging;

        public event Action<BeaconReading> ReadingReceived;
        public event Action<BeaconTelemetry> TelemetryReceived;
        public event Action<BeaconScannerStatus, string> StatusChanged;

        public BeaconScannerStatus Status { get; private set; } = BeaconScannerStatus.Uninitialized;

        /// <summary>
        /// Ground truth: where the simulated walker actually is, in floor space. Compare against
        /// BeaconManager's solved position to measure real error.
        /// </summary>
        public Vector2 TrueFloorPosition { get; private set; }

        /// <summary>Ground-truth heading in degrees, matching FloorMap's convention.</summary>
        public float TrueHeadingDegrees { get; private set; }

        private readonly Dictionary<string, float> _nextAdvertisementTime = new Dictionary<string, float>();
        private System.Random _random;
        private int _patrolIndex;
        private List<Vector2> _resolvedRoute;

        // ------------------------------------------------------------------
        // IBeaconScanner
        // ------------------------------------------------------------------

        public void Initialize()
        {
            if (floorMap == null)
            {
                SetStatus(BeaconScannerStatus.Error, "MockBeaconScanner has no FloorMap assigned.");
                return;
            }

            _random = new System.Random(12345); // Fixed seed: reproducible runs beat random ones.
            TrueFloorPosition = startPosition;
            _resolvedRoute = BuildRoute();
            _patrolIndex = 0;

            SetStatus(BeaconScannerStatus.Ready, "Simulated radio ready.");
        }

        public void StartScanning()
        {
            if (Status == BeaconScannerStatus.Uninitialized)
            {
                Initialize();
            }

            if (Status == BeaconScannerStatus.Error)
            {
                return;
            }

            SetStatus(BeaconScannerStatus.Scanning, "Simulating beacon traffic.");
        }

        public void StopScanning()
        {
            if (Status == BeaconScannerStatus.Scanning)
            {
                SetStatus(BeaconScannerStatus.Ready, "Simulation paused.");
            }
        }

        public void Shutdown()
        {
            SetStatus(BeaconScannerStatus.Uninitialized, "Simulation stopped.");
        }

        // ------------------------------------------------------------------
        // Simulation
        // ------------------------------------------------------------------

        private void Update()
        {
            if (Status != BeaconScannerStatus.Scanning || floorMap == null)
            {
                return;
            }

            AdvanceWalker(Time.deltaTime);
            EmitReadings();
        }

        private void AdvanceWalker(float deltaTime)
        {
            switch (driveMode)
            {
                case DriveMode.PatrolWaypoints:
                    AdvanceAlongRoute(deltaTime);
                    break;

                case DriveMode.Keyboard:
                    AdvanceFromKeyboard(deltaTime);
                    break;

                case DriveMode.FollowTransform:
                    if (followTarget != null)
                    {
                        Vector2 next = floorMap.LocalToFloor(followTarget.localPosition);
                        Vector2 delta = next - TrueFloorPosition;

                        if (delta.sqrMagnitude > 0.0001f)
                        {
                            TrueHeadingDegrees = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
                        }

                        TrueFloorPosition = next;
                    }
                    break;
            }
        }

        private void AdvanceAlongRoute(float deltaTime)
        {
            if (_resolvedRoute == null || _resolvedRoute.Count == 0)
            {
                return;
            }

            Vector2 target = _resolvedRoute[_patrolIndex];
            Vector2 toTarget = target - TrueFloorPosition;

            // Route points are in survey units; speed is in metres per second.
            float stepMeters = walkSpeed * deltaTime;
            float stepSurveyUnits = floorMap.ToSurveyUnits(stepMeters);

            if (toTarget.magnitude <= stepSurveyUnits)
            {
                TrueFloorPosition = target;
                _patrolIndex = (_patrolIndex + 1) % _resolvedRoute.Count;
                return;
            }

            Vector2 direction = toTarget.normalized;
            TrueFloorPosition += direction * stepSurveyUnits;
            TrueHeadingDegrees = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
        }

        private void AdvanceFromKeyboard(float deltaTime)
        {
            // Guarded because a project set to the new Input System only will throw on
            // UnityEngine.Input at runtime rather than failing to compile. If your project is
            // new-Input-System-only, replace this body with Keyboard.current reads.
#if ENABLE_LEGACY_INPUT_MANAGER
            float horizontal = Input.GetAxisRaw("Horizontal");
            float vertical = Input.GetAxisRaw("Vertical");
            var input = new Vector2(horizontal, vertical);
#else
            Vector2 input = Vector2.zero;
#endif

            if (input.sqrMagnitude < 0.01f)
            {
                return;
            }

            input.Normalize();
            float stepSurveyUnits = floorMap.ToSurveyUnits(walkSpeed * deltaTime);
            TrueFloorPosition += input * stepSurveyUnits;
            TrueHeadingDegrees = Mathf.Atan2(input.y, input.x) * Mathf.Rad2Deg;
        }

        private void EmitReadings()
        {
            float now = Time.unscaledTime;
            float interval = 1f / Mathf.Max(advertisementsPerSecond, 0.1f);

            foreach (BeaconDefinition beacon in floorMap.beacons)
            {
                if (beacon == null || !beacon.enabled)
                {
                    continue;
                }

                string id = ResolveId(beacon);

                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                if (!_nextAdvertisementTime.TryGetValue(id, out float nextTime))
                {
                    // Stagger the first advertisement per beacon so they do not all fire on the
                    // same frame, which would give the filters a rhythm real radios never have.
                    nextTime = now + (float)_random.NextDouble() * interval;
                    _nextAdvertisementTime[id] = nextTime;
                }

                if (now < nextTime)
                {
                    continue;
                }

                _nextAdvertisementTime[id] = now + interval;

                if (_random.NextDouble() < packetLossChance)
                {
                    continue;
                }

                float distanceMeters = floorMap.ToMeters(
                    Vector2.Distance(TrueFloorPosition, beacon.position));

                // Beacons are mounted high; the phone is held around chest height. The radio
                // travels the slanted line, not the floor line.
                float verticalOffset = Mathf.Max(beacon.mountHeight - 1.3f, 0f);
                float slantDistance = Mathf.Sqrt(
                    (distanceMeters * distanceMeters) + (verticalOffset * verticalOffset));

                if (slantDistance > maxRangeMeters)
                {
                    continue;
                }

                float rssi = RssiForDistance(beacon, slantDistance);

                if (!HasLineOfSight(TrueFloorPosition, beacon.position))
                {
                    rssi -= nonLineOfSightPenaltyDb;
                }

                rssi += SampleGaussian() * rssiNoiseDb;
                int roundedRssi = Mathf.RoundToInt(rssi);

                string resolvedId = id;

                if (exerciseRealParser)
                {
                    // Build the bytes a real beacon would send, then read them back with the same
                    // parser the device build uses. If the parser breaks, it breaks here, at your
                    // desk, with a debugger attached.
                    byte[] advertisement = EddystoneFrame.BuildUidAdvertisement(
                        floorMap.eddystoneNamespace, id, -21);

                    if (!EddystoneFrame.TryParse(advertisement, out EddystoneFrameType frameType,
                            out EddystoneUid parsed, out _) || frameType != EddystoneFrameType.Uid)
                    {
                        Debug.LogError("[MockBeaconScanner] EddystoneFrame failed to parse a frame " +
                                       "it just built. The parser is broken — fix it before trusting " +
                                       "anything on device.");
                        continue;
                    }

                    resolvedId = parsed.InstanceId;
                }

                var reading = new BeaconReading(resolvedId, roundedRssi, now, $"MOCK:{id}");

                if (!reading.IsPlausible)
                {
                    continue;
                }

                if (verboseLogging)
                {
                    Debug.Log($"[MockBeaconScanner] {beacon.DisplayName} true={slantDistance:F1}m {reading}");
                }

                ReadingReceived?.Invoke(reading);

                // A real BC011 interleaves a TLM frame roughly every tenth advertisement.
                if (simulateTelemetry && _random.NextDouble() < 0.1)
                {
                    TelemetryReceived?.Invoke(new BeaconTelemetry(
                        resolvedId,
                        simulatedBatteryMillivolts + _random.Next(-25, 25),
                        22.5f,
                        (uint)Mathf.Max(now * advertisementsPerSecond, 0f),
                        now));
                }
            }
        }

        /// <summary>Inverse of the log-distance path-loss model RssiFilter uses to undo it.</summary>
        private static float RssiForDistance(BeaconDefinition beacon, float distanceMeters)
        {
            float clamped = Mathf.Max(distanceMeters, 0.1f);
            return beacon.txPowerAtOneMeter - (10f * beacon.environmentFactor * Mathf.Log10(clamped));
        }

        /// <summary>
        /// Crude line-of-sight test: the straight line from walker to beacon has to stay inside
        /// hallway footprints. Round a corner, the line cuts through a wall, and we apply the
        /// penalty. Not physically rigorous — just enough to stop the simulation being unfairly
        /// kind about corners, which is exactly where real trilateration falls over.
        /// </summary>
        private bool HasLineOfSight(Vector2 from, Vector2 to)
        {
            const int samples = 8;

            for (int i = 1; i < samples; i++)
            {
                Vector2 point = Vector2.Lerp(from, to, i / (float)samples);

                if (!floorMap.IsWalkable(point))
                {
                    return false;
                }
            }

            return true;
        }

        private List<Vector2> BuildRoute()
        {
            if (patrolWaypoints != null && patrolWaypoints.Count > 0)
            {
                return new List<Vector2>(patrolWaypoints);
            }

            var route = new List<Vector2>();

            foreach (HallwaySegment segment in floorMap.hallways)
            {
                if (segment == null || !segment.IsValid)
                {
                    continue;
                }

                route.Add(segment.start);
                route.Add(segment.end);
            }

            if (route.Count == 0)
            {
                route.Add(startPosition);
            }

            return route;
        }

        private string ResolveId(BeaconDefinition beacon)
        {
            return beacon.NormalizedInstanceId;
        }

        /// <summary>Box-Muller transform: uniform random in, normally distributed out.</summary>
        private float SampleGaussian()
        {
            double u1 = 1.0 - _random.NextDouble();
            double u2 = 1.0 - _random.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2));
        }

        private void SetStatus(BeaconScannerStatus status, string detail)
        {
            if (Status == status)
            {
                return;
            }

            Status = status;
            StatusChanged?.Invoke(status, detail);
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (floorMap == null)
            {
                return;
            }

            Gizmos.color = Color.green;
            Vector3 world = transform.TransformPoint(floorMap.FloorToLocal(TrueFloorPosition));
            Gizmos.DrawWireSphere(world, 0.3f);

            float radians = TrueHeadingDegrees * Mathf.Deg2Rad;
            var facing = new Vector3(Mathf.Cos(radians), 0f, Mathf.Sin(radians));
            Gizmos.DrawLine(world, world + transform.TransformDirection(facing) * 0.8f);
        }
#endif
    }
}

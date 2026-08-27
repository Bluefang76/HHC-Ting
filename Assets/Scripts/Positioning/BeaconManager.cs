using System;
using System.Collections.Generic;
using UnityEngine;
using Wayfinding.Data;

namespace Wayfinding.Positioning
{
    /// <summary>
    /// One solved position, with enough context to decide whether to believe it.
    /// </summary>
    public readonly struct PositionFix
    {
        /// <summary>Position in FloorMap survey units. Use FloorMap.FloorToLocal for Unity space.</summary>
        public readonly Vector2 FloorPosition;

        /// <summary>0 to 1. Below about 0.3, show the visitor a "move around a bit" hint.</summary>
        public readonly float Confidence;

        /// <summary>RMS distance disagreement in metres. Big numbers mean a beacon is misbehaving.</summary>
        public readonly float ResidualMeters;

        /// <summary>How many beacons contributed.</summary>
        public readonly int BeaconsUsed;

        /// <summary>Time.unscaledTime when solved.</summary>
        public readonly float Timestamp;

        /// <summary>Floor units per second, smoothed. Used to infer heading while walking.</summary>
        public readonly Vector2 Velocity;

        public readonly bool IsValid;

        public PositionFix(
            Vector2 floorPosition,
            float confidence,
            float residualMeters,
            int beaconsUsed,
            float timestamp,
            Vector2 velocity,
            bool isValid = true)
        {
            FloorPosition = floorPosition;
            Confidence = confidence;
            ResidualMeters = residualMeters;
            BeaconsUsed = beaconsUsed;
            Timestamp = timestamp;
            Velocity = velocity;
            IsValid = isValid;
        }

        public static PositionFix Invalid => new PositionFix(
            Vector2.zero, 0f, 0f, 0, 0f, Vector2.zero, false);
    }

    /// <summary>
    /// The conductor of the positioning stack, and the first thing to build in this project.
    ///
    /// Owns the scanner, keeps one RssiFilter per beacon, chooses which beacons to trust each
    /// tick, runs the trilateration solve, sanity-checks the answer against human walking speed
    /// and the hallway geometry, and publishes it. Everything downstream — navigation, AR,
    /// UI, the debug HUD — listens to <see cref="PositionUpdated"/> and knows nothing about
    /// radios.
    ///
    /// Get this file working, watch the position dot track you down a corridor in the editor
    /// with MockBeaconScanner, and the hard part of the project is behind you.
    /// </summary>
    public class BeaconManager : MonoBehaviour
    {
        [Header("Data")]
        public FloorMap floorMap;

        [Header("Scanner")]
        [Tooltip("A component implementing IBeaconScanner — BleBeaconScanner on device, " +
                 "MockBeaconScanner in the editor. Left empty, one is looked up on this " +
                 "GameObject and its children.")]
        [SerializeField]
        private MonoBehaviour scannerComponent;

        [Tooltip("In the editor, prefer a MockBeaconScanner if one is present, even when a real " +
                 "scanner is assigned. Saves swapping references twenty times a day.")]
        public bool preferMockInEditor = true;

        [Header("Solve cadence")]
        [Tooltip("Position solves per second. Beacons advertise at 1-2 Hz, so solving much faster " +
                 "than 10 Hz just re-solves the same data and burns battery.")]
        [Range(1f, 20f)]
        public float solveRate = 8f;

        [Tooltip("A beacon unheard for this long stops contributing. Set it to roughly three " +
                 "advertising intervals: long enough to survive a couple of dropped packets, " +
                 "short enough that a beacon you have walked away from stops asserting itself.")]
        public float beaconStaleAfterSeconds = 3f;

        [Tooltip("Beacons to feed the solver, strongest first. More is not better — a distant " +
                 "beacon adds a poor measurement and can drag a good solve off. 5 or 6 is the " +
                 "sweet spot.")]
        [Range(3, 12)]
        public int maxBeaconsPerSolve = 6;

        [Header("Signal conditioning")]
        [Tooltip("Rolling median window per beacon. Larger is steadier and laggier.")]
        [Range(1, 15)]
        public int medianWindow = 5;

        [Tooltip("How high the visitor holds the phone, in metres. Used to convert the slanted " +
                 "radio path down to a floor distance. 1.3 is about right for a phone held at " +
                 "chest height and looked at.")]
        public float deviceHeightMeters = 1.3f;

        [Header("Plausibility")]
        [Tooltip("Fastest a person can plausibly move, in metres per second. Fixes that imply " +
                 "more than this are clamped rather than accepted — it is the cheapest and most " +
                 "effective guard against the position teleporting across the corridor.")]
        public float maxWalkSpeedMetersPerSecond = 2.2f;

        [Tooltip("Pull solved positions onto walkable hallway. Almost always right: the visitor " +
                 "is in a corridor, not inside an exam room wall.")]
        public bool snapToHallways = true;

        [Tooltip("Below this confidence a fix is published but flagged, so the UI can soften the " +
                 "guidance instead of confidently pointing the wrong way.")]
        [Range(0f, 1f)]
        public float lowConfidenceThreshold = 0.3f;

        [Header("Debug")]
        public bool verboseLogging;

        /// <summary>Fires every solve tick that produces a usable position.</summary>
        public event Action<PositionFix> PositionUpdated;

        /// <summary>Relays scanner status so the UI can explain Bluetooth problems in plain words.</summary>
        public event Action<BeaconScannerStatus, string> ScannerStatusChanged;

        /// <summary>Most recent fix. Invalid until the first successful solve.</summary>
        public PositionFix CurrentFix { get; private set; } = PositionFix.Invalid;

        /// <summary>True once at least one fix has been solved this session.</summary>
        public bool HasFix => CurrentFix.IsValid;

        public BeaconScannerStatus ScannerStatus =>
            _scanner?.Status ?? BeaconScannerStatus.Uninitialized;

        private IBeaconScanner _scanner;

        /// <summary>Eddystone instance ID to that beacon's signal filter.</summary>
        private readonly Dictionary<string, RssiFilter> _filters = new Dictionary<string, RssiFilter>();

        /// <summary>Latest telemetry per instance ID. Battery health, straight off the air.</summary>
        private readonly Dictionary<string, BeaconTelemetry> _telemetry =
            new Dictionary<string, BeaconTelemetry>();
        private readonly List<Trilateration.Anchor> _anchorBuffer = new List<Trilateration.Anchor>();
        private readonly List<BeaconCandidate> _candidateBuffer = new List<BeaconCandidate>();

        private float _nextSolveTime;
        private Vector2 _smoothedPosition;
        private Vector2 _velocity;
        private bool _hasPosition;
        private float _lastAcceptedTime;

        private struct BeaconCandidate
        {
            public BeaconDefinition Beacon;
            public RssiFilter Filter;
            public float DistanceMeters;
            public float Weight;
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            _scanner = ResolveScanner();

            if (_scanner == null)
            {
                Debug.LogError("[BeaconManager] No IBeaconScanner found. Assign one, or add a " +
                               "MockBeaconScanner to this GameObject to work without hardware.");
                return;
            }

            _scanner.ReadingReceived += OnReadingReceived;
            _scanner.TelemetryReceived += OnTelemetryReceived;
            _scanner.StatusChanged += OnScannerStatusChanged;
        }

        private void Start()
        {
            if (floorMap == null)
            {
                Debug.LogError("[BeaconManager] No FloorMap assigned. Nothing can be positioned.");
                return;
            }

            _scanner?.Initialize();
            _scanner?.StartScanning();
        }

        private void OnDestroy()
        {
            if (_scanner == null)
            {
                return;
            }

            _scanner.ReadingReceived -= OnReadingReceived;
            _scanner.TelemetryReceived -= OnTelemetryReceived;
            _scanner.StatusChanged -= OnScannerStatusChanged;
            _scanner.Shutdown();
        }

        private void Update()
        {
            if (floorMap == null || _scanner == null)
            {
                return;
            }

            if (Time.unscaledTime < _nextSolveTime)
            {
                return;
            }

            _nextSolveTime = Time.unscaledTime + (1f / Mathf.Max(solveRate, 1f));
            Solve();
        }

        // ------------------------------------------------------------------
        // Public control
        // ------------------------------------------------------------------

        /// <summary>
        /// Seeds the position from a known point — called by QrAnchorResolver when the visitor
        /// scans the entrance code. Gives the solver a correct starting guess, which is worth
        /// several seconds of convergence and stops the first fix landing somewhere silly.
        /// </summary>
        public void SetKnownPosition(Vector2 floorPosition)
        {
            _smoothedPosition = floorPosition;
            _velocity = Vector2.zero;
            _hasPosition = true;
            _lastAcceptedTime = Time.unscaledTime;

            CurrentFix = new PositionFix(
                floorPosition, 1f, 0f, 0, Time.unscaledTime, Vector2.zero);

            PositionUpdated?.Invoke(CurrentFix);
        }

        /// <summary>
        /// Clears all signal history. Call when resuming from background, or after a QR rescan —
        /// filters full of readings from where the phone used to be will fight the new truth for
        /// several seconds.
        /// </summary>
        public void ResetFilters()
        {
            foreach (RssiFilter filter in _filters.Values)
            {
                filter.Reset();
            }

            _hasPosition = false;
            _velocity = Vector2.zero;
            CurrentFix = PositionFix.Invalid;
        }

        /// <summary>Filter for one beacon, or null if it has never been heard. Used by the HUD and survey tool.</summary>
        public RssiFilter GetFilter(BeaconDefinition beacon)
        {
            if (beacon == null || !beacon.HasValidInstanceId)
            {
                return null;
            }

            return _filters.TryGetValue(beacon.NormalizedInstanceId, out RssiFilter filter)
                ? filter
                : null;
        }

        /// <summary>
        /// Most recent telemetry for one beacon, if it has broadcast a TLM frame this session.
        /// Battery voltage without a server.
        /// </summary>
        public bool TryGetTelemetry(BeaconDefinition beacon, out BeaconTelemetry telemetry)
        {
            telemetry = default;

            if (beacon == null || !beacon.HasValidInstanceId)
            {
                return false;
            }

            return _telemetry.TryGetValue(beacon.NormalizedInstanceId, out telemetry);
        }

        /// <summary>Per-beacon snapshot for the debug overlay.</summary>
        public struct BeaconDiagnostic
        {
            public BeaconDefinition Beacon;
            public float FilteredRssi;
            public float RawRssi;
            public float DistanceMeters;
            public bool InUse;
            public bool Fresh;
            public float Weight;

            /// <summary>Battery in millivolts, or 0 if this beacon has not sent telemetry yet.</summary>
            public int BatteryMillivolts;
        }

        /// <summary>Fills a caller-owned list with the current state of every beacon in the map.</summary>
        public void GetDiagnostics(List<BeaconDiagnostic> results)
        {
            results.Clear();

            if (floorMap == null)
            {
                return;
            }

            float now = Time.unscaledTime;

            foreach (BeaconDefinition beacon in floorMap.beacons)
            {
                if (beacon == null)
                {
                    continue;
                }

                RssiFilter filter = GetFilter(beacon);
                int battery = TryGetTelemetry(beacon, out BeaconTelemetry telemetry)
                    ? telemetry.BatteryMillivolts
                    : 0;

                if (filter == null)
                {
                    results.Add(new BeaconDiagnostic
                    {
                        Beacon = beacon,
                        Fresh = false,
                        BatteryMillivolts = battery
                    });
                    continue;
                }

                bool fresh = filter.IsFresh(now, beaconStaleAfterSeconds);
                filter.TryGetDistance(beacon, now, beaconStaleAfterSeconds, deviceHeightMeters,
                    out float distance);

                results.Add(new BeaconDiagnostic
                {
                    Beacon = beacon,
                    FilteredRssi = filter.FilteredRssi,
                    RawRssi = filter.RawRssi,
                    DistanceMeters = distance,
                    Fresh = fresh,
                    InUse = fresh && beacon.enabled,
                    Weight = filter.ConfidenceWeight(distance, now, beaconStaleAfterSeconds),
                    BatteryMillivolts = battery
                });
            }
        }

        // ------------------------------------------------------------------
        // Scanner plumbing
        // ------------------------------------------------------------------

        private IBeaconScanner ResolveScanner()
        {
#if UNITY_EDITOR
            if (preferMockInEditor)
            {
                var mock = GetComponentInChildren<MockBeaconScanner>();

                if (mock != null)
                {
                    return mock;
                }
            }
#endif
            if (scannerComponent is IBeaconScanner assigned)
            {
                return assigned;
            }

            if (scannerComponent != null)
            {
                Debug.LogError($"[BeaconManager] {scannerComponent.GetType().Name} does not " +
                               "implement IBeaconScanner.");
            }

            foreach (MonoBehaviour behaviour in GetComponentsInChildren<MonoBehaviour>())
            {
                if (behaviour is IBeaconScanner found)
                {
                    return found;
                }
            }

            return null;
        }

        private void OnReadingReceived(BeaconReading reading)
        {
            if (floorMap == null)
            {
                return;
            }

            // A hospital's airspace is thick with BLE that is none of our business — infusion
            // pumps, staff badges, other people's earbuds. Anything not in the survey is dropped
            // here, before it costs anything.
            BeaconDefinition beacon = floorMap.FindBeacon(reading.InstanceId);

            if (beacon == null)
            {
                return;
            }

            string key = beacon.NormalizedInstanceId;

            if (!_filters.TryGetValue(key, out RssiFilter filter))
            {
                filter = new RssiFilter(medianWindow);
                _filters[key] = filter;
            }

            filter.AddReading(reading.Rssi, reading.Timestamp);
        }

        private void OnTelemetryReceived(BeaconTelemetry telemetry)
        {
            if (floorMap == null || floorMap.FindBeacon(telemetry.InstanceId) == null)
            {
                return;
            }

            string key = BeaconDefinition.NormalizeHex(telemetry.InstanceId);
            _telemetry[key] = telemetry;

            if (telemetry.BatteryMillivolts > 0 && telemetry.BatteryMillivolts < 2500)
            {
                // Worth a warning even in a release build. A beacon at this voltage will start
                // dropping advertisements well before it goes silent, so the failure it causes
                // looks like bad positioning rather than a dead battery.
                Debug.LogWarning($"[BeaconManager] Beacon {telemetry.InstanceId} battery is " +
                                 $"{telemetry.BatteryMillivolts} mV. Replace it.");
            }
        }

        private void OnScannerStatusChanged(BeaconScannerStatus status, string detail)
        {
            ScannerStatusChanged?.Invoke(status, detail);
        }

        // ------------------------------------------------------------------
        // The solve
        // ------------------------------------------------------------------

        private void Solve()
        {
            float now = Time.unscaledTime;

            CollectCandidates(now);

            if (_candidateBuffer.Count < 3)
            {
                // Not enough to trilaterate. Keep publishing nothing rather than guessing —
                // a confidently wrong arrow is worse than an honest "finding you".
                if (verboseLogging)
                {
                    Debug.Log($"[BeaconManager] Only {_candidateBuffer.Count} beacon(s) in range.");
                }

                return;
            }

            // Strongest first, then keep only the best few. Distant beacons carry poor distance
            // estimates and adding them makes the solve worse, not better.
            _candidateBuffer.Sort((left, right) => left.DistanceMeters.CompareTo(right.DistanceMeters));

            int useCount = Mathf.Min(_candidateBuffer.Count, maxBeaconsPerSolve);
            _anchorBuffer.Clear();

            for (int i = 0; i < useCount; i++)
            {
                BeaconCandidate candidate = _candidateBuffer[i];

                // Anchors work in metres, so beacon positions convert out of survey units here.
                Vector2 positionMeters = candidate.Beacon.position * floorMap.unitsToMeters;
                _anchorBuffer.Add(new Trilateration.Anchor(
                    positionMeters, candidate.DistanceMeters, candidate.Weight));
            }

            Vector2 guessMeters = _smoothedPosition * floorMap.unitsToMeters;

            Trilateration.TrilaterationResult result =
                Trilateration.Solve(_anchorBuffer, guessMeters, _hasPosition);

            if (!result.Success)
            {
                if (verboseLogging)
                {
                    Debug.LogWarning($"[BeaconManager] Solve failed: {result.FailureReason}");
                }

                return;
            }

            Vector2 solvedFloorPosition = floorMap.unitsToMeters > 0f
                ? result.Position / floorMap.unitsToMeters
                : result.Position;

            solvedFloorPosition = ApplyPlausibility(solvedFloorPosition, now);

            if (snapToHallways)
            {
                solvedFloorPosition = floorMap.SnapToWalkable(solvedFloorPosition);
            }

            UpdateVelocity(solvedFloorPosition, now);

            _smoothedPosition = solvedFloorPosition;
            _hasPosition = true;
            _lastAcceptedTime = now;

            CurrentFix = new PositionFix(
                solvedFloorPosition,
                result.Confidence,
                result.ResidualMeters,
                result.AnchorsUsed,
                now,
                _velocity);

            if (verboseLogging && result.Confidence < lowConfidenceThreshold)
            {
                Debug.Log($"[BeaconManager] Low confidence {result.Confidence:F2} " +
                          $"(residual {result.ResidualMeters:F1}m, geometry " +
                          $"{result.GeometryQuality:F2}). Check beacon placement — beacons in a " +
                          "straight line down one wall cause exactly this.");
            }

            PositionUpdated?.Invoke(CurrentFix);
        }

        private void CollectCandidates(float now)
        {
            _candidateBuffer.Clear();

            foreach (BeaconDefinition beacon in floorMap.beacons)
            {
                if (beacon == null || !beacon.enabled)
                {
                    continue;
                }

                RssiFilter filter = GetFilter(beacon);

                if (filter == null || !filter.IsFresh(now, beaconStaleAfterSeconds))
                {
                    continue;
                }

                if (!filter.TryGetDistance(beacon, now, beaconStaleAfterSeconds,
                        deviceHeightMeters, out float distanceMeters))
                {
                    continue;
                }

                _candidateBuffer.Add(new BeaconCandidate
                {
                    Beacon = beacon,
                    Filter = filter,
                    DistanceMeters = distanceMeters,
                    Weight = filter.ConfidenceWeight(distanceMeters, now, beaconStaleAfterSeconds)
                });
            }
        }

        /// <summary>
        /// Rejects physically impossible movement. People walk; they do not teleport. If a solve
        /// implies 8 m/s, something went wrong upstream — a reflected signal, a beacon surveyed
        /// into the wrong room — and the honest response is to move toward the new answer at
        /// walking pace rather than jump to it.
        /// </summary>
        private Vector2 ApplyPlausibility(Vector2 solvedFloorPosition, float now)
        {
            if (!_hasPosition)
            {
                return solvedFloorPosition;
            }

            float elapsed = Mathf.Max(now - _lastAcceptedTime, 0.001f);
            float maxTravelSurveyUnits = floorMap.ToSurveyUnits(
                maxWalkSpeedMetersPerSecond * elapsed);

            Vector2 delta = solvedFloorPosition - _smoothedPosition;

            if (delta.magnitude <= maxTravelSurveyUnits)
            {
                return solvedFloorPosition;
            }

            return _smoothedPosition + (delta.normalized * maxTravelSurveyUnits);
        }

        private void UpdateVelocity(Vector2 newPosition, float now)
        {
            if (!_hasPosition)
            {
                _velocity = Vector2.zero;
                return;
            }

            float elapsed = Mathf.Max(now - _lastAcceptedTime, 0.001f);
            Vector2 instantaneous = (newPosition - _smoothedPosition) / elapsed;

            // Heavily smoothed: velocity is used to infer which way the visitor is facing, and a
            // heading that flickers is worse than one that updates a beat late.
            _velocity = Vector2.Lerp(_velocity, instantaneous, 0.25f);
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using Wayfinder.Mapping;

namespace Wayfinder.Beacons
{
    /// <summary>
    /// Owns the positioning pipeline: scanner -> filter -> trilateration -> position fix.
    ///
    /// This is the first thing to build (see the build order in CLAUDE.md). Everything
    /// above it is worthless if this is not solid.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BeaconManager : MonoBehaviour
    {
        [Header("Map")]
        [SerializeField] private FloorMap floorMap;

        [Header("Tuning")]
        [Tooltip("Seconds between position solves. Faster is not better — RSSI needs time to average.")]
        [SerializeField] private float solveInterval = 0.5f;

        [Tooltip("Maximum plausible walking speed, m/s. Fixes implying more are rejected.")]
        [SerializeField] private float maxWalkingSpeed = 1.8f;

        [Tooltip("Fixes below this confidence are not published.")]
        [Range(0f, 1f)]
        [SerializeField] private float minimumConfidence = 0.35f;

        /// <summary>Raised whenever a new position fix is accepted. Map coordinates, meters.</summary>
        public event Action<PositionFix> PositionUpdated;

        /// <summary>Raised when positioning cannot be trusted — too few beacons, bad geometry, radio off.</summary>
        public event Action<string> PositioningDegraded;

        private IBeaconScanner _scanner;
        private readonly RssiFilter _filter = new();
        private readonly BeaconRegistry _registry = new();
        private readonly List<DistanceObservation> _observations = new();
        private PositionFix _lastFix;
        private float _solveTimer;

        private void Awake()
        {
            // TODO: populate _registry from floorMap's beacon anchors.
            _scanner = BeaconScannerFactory.Create();
            _scanner.ReadingReceived += OnReadingReceived;
            _scanner.ErrorOccurred += OnScannerError;
        }

        private void OnEnable() => _scanner?.StartScanning();

        private void OnDisable() => _scanner?.StopScanning();

        private void OnDestroy()
        {
            if (_scanner == null) return;
            _scanner.ReadingReceived -= OnReadingReceived;
            _scanner.ErrorOccurred -= OnScannerError;
            _scanner.Dispose();
        }

        private void Update()
        {
            _solveTimer += Time.deltaTime;
            if (_solveTimer < solveInterval) return;
            _solveTimer = 0f;
            Solve();
        }

        private void OnReadingReceived(BeaconReading reading)
        {
            // Beacons from other floors are audible through the ceiling — ignore anything
            // not on the loaded floor.
            if (!_registry.IsKnown(reading.BeaconId)) return;
            _filter.Submit(reading);
        }

        private void OnScannerError(BeaconScannerError error)
        {
            PositioningDegraded?.Invoke(error.ToString());
        }

        private void Solve()
        {
            _filter.PruneStale(Time.timeAsDouble);

            _observations.Clear();
            foreach (var pair in _filter.CurrentDistances())
            {
                if (!_registry.TryGet(pair.Key, out var anchor)) continue;
                _observations.Add(new DistanceObservation(anchor.MapPosition, pair.Value));
            }

            if (_observations.Count < Trilateration.MinimumBeacons)
            {
                PositioningDegraded?.Invoke($"Only {_observations.Count} beacons in range.");
                return;
            }

            var fix = Trilateration.Solve(_observations);
            if (!fix.IsValid || fix.Confidence < minimumConfidence)
            {
                PositioningDegraded?.Invoke("Low-confidence position fix.");
                return;
            }

            // TODO: clamp the fix to the walkable region — a visitor is in the hallway,
            //       not inside a wall — and reject jumps faster than maxWalkingSpeed.
            _ = maxWalkingSpeed;

            _lastFix = fix;
            PositionUpdated?.Invoke(fix);
        }

        public PositionFix LastFix => _lastFix;
    }
}

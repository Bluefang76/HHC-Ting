using System.Collections.Generic;
using UnityEngine;
using Wayfinder.AR;
using Wayfinder.Beacons;
using Wayfinder.Mapping;
using Wayfinder.Navigation;
using Wayfinder.UI;

namespace Wayfinder.Core
{
    /// <summary>
    /// Wires the five stages together and owns the app's lifetime.
    ///
    /// Read this file to see how the pieces connect; read docs/architecture.md to see why.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WayfinderBootstrap : MonoBehaviour
    {
        [Header("Data")]
        [SerializeField] private FloorMap floorMap;

        [Header("Scene components")]
        [SerializeField] private BeaconManager beaconManager;
        [SerializeField] private PathfindingEngine pathfinding;
        [SerializeField] private ARPathRenderer pathRenderer;
        [SerializeField] private WorldAnchorManager anchors;
        [SerializeField] private UIController ui;

        [Header("Tuning")]
        [Tooltip("Distance from the destination door at which the user is considered arrived, meters.")]
        [SerializeField] private float arrivalRadius = 2f;

        private readonly MapCoordinateSystem _coordinates = new();
        private readonly List<Vector3> _pathBuffer = new();
        private DestinationResolver _resolver;
        private Vector2 _destinationMapPosition;
        private bool _hasDestination;

        private void Awake()
        {
            _resolver = new DestinationResolver(floorMap);
        }

        private void OnEnable()
        {
            if (beaconManager == null) return;
            beaconManager.PositionUpdated += OnPositionUpdated;
            beaconManager.PositioningDegraded += OnPositioningDegraded;
        }

        private void OnDisable()
        {
            if (beaconManager == null) return;
            beaconManager.PositionUpdated -= OnPositionUpdated;
            beaconManager.PositioningDegraded -= OnPositioningDegraded;
        }

        /// <summary>
        /// Called when the entrance QR is decoded. The code carries the sign's known map
        /// position and heading, which gives a trustworthy origin at session start —
        /// worth more than any amount of beacon math.
        /// </summary>
        public void OnEntranceQrScanned(string payload)
        {
            // TODO: parse building/floor/position/heading out of the payload, load the
            //       matching FloorMap, and call _coordinates.Align(...).
            ui?.Show(UIController.Screen.EnterRoom);
        }

        /// <summary>Called when the visitor submits a room number.</summary>
        public void OnDestinationEntered(string roomQuery)
        {
            var result = _resolver.TryResolve(roomQuery, out var mapPosition, out _);
            if (result != DestinationResolver.Result.Found)
            {
                // TODO: distinct messaging per result — not found, wrong floor, ambiguous.
                ui?.ShowDegraded(result.ToString());
                return;
            }

            _destinationMapPosition = mapPosition;
            _hasDestination = true;
            ui?.Show(UIController.Screen.Navigating);
        }

        private void OnPositionUpdated(PositionFix fix)
        {
            if (!_hasDestination || !_coordinates.IsAligned) return;

            var fromWorld = _coordinates.MapToWorld(fix.Position);
            var toWorld = _coordinates.MapToWorld(_destinationMapPosition);

            if (Vector2.Distance(fix.Position, _destinationMapPosition) <= arrivalRadius)
            {
                pathRenderer?.Clear();
                ui?.Show(UIController.Screen.Arrived);
                return;
            }

            if (pathfinding != null && pathfinding.TryComputePath(fromWorld, toWorld, _pathBuffer))
            {
                pathRenderer?.SetPath(_pathBuffer);
                ui?.SetRemainingDistance(PathfindingEngine.PathLength(_pathBuffer));
            }
            else
            {
                ui?.ShowDegraded("No route available.");
            }

            anchors?.CorrectDrift(fromWorld);
        }

        private void OnPositioningDegraded(string reason)
        {
            // Degrade honestly: stop drawing a confident line to a place we are not sure of.
            pathRenderer?.Clear();
            ui?.ShowDegraded(reason);
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace Wayfinder.Beacons
{
    /// <summary>
    /// Maps a beacon's broadcast identity to where it is physically mounted.
    ///
    /// Populated from the FloorMap asset, which is populated from
    /// docs/floor-map-data.md. Beacon positions are never hardcoded in scripts —
    /// they change every time a unit is re-mounted.
    /// </summary>
    public sealed class BeaconRegistry
    {
        public readonly struct Anchor
        {
            public readonly string BeaconId;
            public readonly Vector2 MapPosition;   // meters, map coordinates
            public readonly float MountHeight;     // meters above the floor
            public readonly int TxPowerAtOneMeter; // dBm, from the unit's label/config

            public Anchor(string beaconId, Vector2 mapPosition, float mountHeight, int txPowerAtOneMeter)
            {
                BeaconId = beaconId;
                MapPosition = mapPosition;
                MountHeight = mountHeight;
                TxPowerAtOneMeter = txPowerAtOneMeter;
            }
        }

        private readonly Dictionary<string, Anchor> _anchors = new();

        public int Count => _anchors.Count;

        public void Register(in Anchor anchor) => _anchors[anchor.BeaconId] = anchor;

        public bool TryGet(string beaconId, out Anchor anchor) => _anchors.TryGetValue(beaconId, out anchor);

        /// <summary>True when this beacon belongs to the loaded floor. Ignore anything else —
        /// other floors' beacons will be audible through the ceiling.</summary>
        public bool IsKnown(string beaconId) => _anchors.ContainsKey(beaconId);

        public void Clear() => _anchors.Clear();
    }
}

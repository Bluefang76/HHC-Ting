using System.Collections.Generic;
using UnityEngine;

namespace Wayfinder.Mapping
{
    /// <summary>
    /// Everything known about one floor: rooms, beacon anchors, hallway geometry.
    ///
    /// A ScriptableObject so the data is edited in the Inspector and versioned as an
    /// asset, not compiled into code. Source of truth for the numbers is
    /// docs/floor-map-data.md.
    /// </summary>
    [CreateAssetMenu(fileName = "FloorMap", menuName = "Wayfinder/Floor Map")]
    public sealed class FloorMap : ScriptableObject
    {
        [System.Serializable]
        public struct RoomNode
        {
            [Tooltip("Room number as printed on the door — this is what the visitor types.")]
            public string roomNumber;

            [Tooltip("Doorway position in map coordinates, meters.")]
            public Vector2 doorPosition;

            [Tooltip("Direction the door faces, degrees from +X.")]
            public float doorHeading;

            [Tooltip("Alternate labels a visitor might be given for this room.")]
            public string[] aliases;
        }

        [System.Serializable]
        public struct BeaconAnchor
        {
            [Tooltip("UUID:major:minor, or MAC on Android.")]
            public string beaconId;

            [Tooltip("Mounted position in map coordinates, meters.")]
            public Vector2 mapPosition;

            [Tooltip("Height above the floor, meters. Keep consistent across the deployment.")]
            public float mountHeight;

            [Tooltip("Advertised RSSI at 1 m, dBm.")]
            public int txPowerAtOneMeter;
        }

        [System.Serializable]
        public struct HallwaySegment
        {
            public Vector2 start;
            public Vector2 end;

            [Tooltip("Width of this segment, meters.")]
            public float width;
        }

        [Header("Identity")]
        public string buildingId;
        public string floorId;

        [Header("Geometry (map coordinates, meters)")]
        [Tooltip("Describe the reference corner the origin sits on, and photograph it.")]
        public string originDescription;

        public List<HallwaySegment> hallways = new();

        [Header("Destinations")]
        public List<RoomNode> rooms = new();

        [Header("Positioning")]
        public List<BeaconAnchor> beacons = new();

        [Header("Calibration")]
        [Tooltip("Path-loss exponent measured on this floor. Do not guess — see docs/floor-map-data.md.")]
        public float pathLossExponent = 2.5f;

        /// <summary>Looks up a room by its printed number or any alias, case-insensitively.</summary>
        public bool TryFindRoom(string query, out RoomNode room)
        {
            room = default;
            if (string.IsNullOrWhiteSpace(query)) return false;

            // TODO: normalise (trim, upper-case, strip spaces) and match number then aliases.
            //       Hospital room numbering is rarely clean — watch for suffixed bays
            //       (214A/214B) and duplicates across buildings.
            return false;
        }
    }
}

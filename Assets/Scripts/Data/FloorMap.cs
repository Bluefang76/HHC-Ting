using System;
using System.Collections.Generic;
using UnityEngine;

namespace Wayfinding.Data
{
    /// <summary>
    /// The complete survey of one hospital floor: its beacons, its rooms, its walkable
    /// hallways, and the QR anchors that tie the whole thing to the real world.
    ///
    /// This is a ScriptableObject, which means it is a data asset in the project rather than
    /// something living on a GameObject. Create one per floor:
    ///     Assets > Create > Wayfinding > Floor Map
    ///
    /// COORDINATE FRAME
    /// Everything here is in "floor space": a flat 2D grid with an origin you pick (put it at
    /// the entrance QR code — it makes every other number easier to reason about), X running
    /// along the main corridor and Y running across it. Floor space maps into Unity like this:
    ///
    ///     floor (x, y)  ->  unity (x, 0, y)
    ///
    /// so Y in your paced survey becomes Z in Unity, and Unity's Y is height. Every conversion
    /// goes through FloorToLocal / LocalToFloor below — never do it by hand elsewhere, because
    /// when you inevitably discover you paced one corridor in feet, the fix belongs in one place.
    /// </summary>
    [CreateAssetMenu(fileName = "FloorMap", menuName = "Wayfinding/Floor Map", order = 0)]
    public class FloorMap : ScriptableObject
    {
        /// <summary>
        /// A physical QR code posted at a known spot. Scanning it tells the app both where the
        /// visitor is standing and, just as importantly, which way they are facing.
        /// </summary>
        [Serializable]
        public class QrAnchor
        {
            [Tooltip("Exact string encoded in the QR code. Keep it short and opaque, e.g. " +
                     "'SBK-F4-ENT-01'. Do not encode a URL that leaks the floor plan.")]
            public string code = "";

            [Tooltip("Where a person stands when scanning this code, in floor space (metres).")]
            public Vector2 position;

            [Tooltip("Which way that person faces while scanning, in degrees. 0 means facing +X " +
                     "(along the corridor), 90 means facing +Y. Post the code so there is only " +
                     "one comfortable way to stand and scan it, and this stays accurate.")]
            [Range(0f, 360f)]
            public float headingDegrees;

            [Tooltip("Label for your own reference, e.g. 'Main lobby, by the elevator bank'.")]
            public string label = "";
        }

        [Header("Floor identity")]
        [Tooltip("Building + floor, shown in the UI header, e.g. 'Tower B - Floor 4'.")]
        public string floorName = "Floor 4";

        [Tooltip("Numeric floor index. Only one floor is supported in the MVP, but carrying the " +
                 "index now means multi-floor routing is additive later instead of a rewrite.")]
        public int floorIndex = 4;

        [Header("Survey units")]
        [Tooltip("Multiply every coordinate in this asset by this to get metres. Leave at 1 if " +
                 "you surveyed in metres. Use 0.3048 if you paced in feet. Use 0.762 if you " +
                 "counted average adult paces (~30 in) and never converted them.")]
        public float unitsToMeters = 1f;

        [Header("Geometry")]
        [Tooltip("Every straight run of walkable hallway on this floor.")]
        public List<HallwaySegment> hallways = new List<HallwaySegment>();

        [Tooltip("How far the walkable strip is pulled in from each wall, in metres. Keeps the " +
                 "racing line off the baseboards and clear of wall-mounted equipment. 0.35 is " +
                 "a reasonable start for a corridor with hand rails.")]
        [Range(0f, 1f)]
        public float wallClearance = 0.35f;

        [Header("Destinations")]
        public List<RoomNode> rooms = new List<RoomNode>();

        [Header("Beacons")]
        [Tooltip("The Eddystone namespace shared by every beacon in this deployment: 10 bytes as " +
                 "20 hex characters. It is what separates your beacons from anyone else's, so pick " +
                 "one value and set it on all 30 units in the KBeacon app. Any hex string works; " +
                 "the Eddystone spec suggests deriving it from a domain name, but for a single " +
                 "hospital floor an arbitrary constant is fine as long as it is not all zeroes.")]
        public string eddystoneNamespace = "48575046494E44494E47";

        public List<BeaconDefinition> beacons = new List<BeaconDefinition>();

        [Header("Entry points")]
        [Tooltip("Every QR code posted on this floor.")]
        public List<QrAnchor> qrAnchors = new List<QrAnchor>();

        // ------------------------------------------------------------------
        // Coordinate conversion
        // ------------------------------------------------------------------

        /// <summary>
        /// Floor-space point (survey units) to a position in the floor root's local space (metres).
        /// The result sits at y = 0; height comes from AR plane detection at render time.
        /// </summary>
        public Vector3 FloorToLocal(Vector2 floorPoint)
        {
            return new Vector3(floorPoint.x * unitsToMeters, 0f, floorPoint.y * unitsToMeters);
        }

        /// <summary>Inverse of <see cref="FloorToLocal"/>. Height is discarded.</summary>
        public Vector2 LocalToFloor(Vector3 localPoint)
        {
            float scale = Mathf.Approximately(unitsToMeters, 0f) ? 1f : unitsToMeters;
            return new Vector2(localPoint.x / scale, localPoint.z / scale);
        }

        /// <summary>Converts a distance expressed in survey units into metres.</summary>
        public float ToMeters(float surveyDistance) => surveyDistance * unitsToMeters;

        /// <summary>Converts a distance in metres back into survey units.</summary>
        public float ToSurveyUnits(float meters)
        {
            return Mathf.Approximately(unitsToMeters, 0f) ? meters : meters / unitsToMeters;
        }

        // ------------------------------------------------------------------
        // Lookups
        // ------------------------------------------------------------------

        /// <summary>
        /// Finds the beacon matching a scanned Eddystone instance ID. Returns null for anything
        /// unknown, which is most of what a hospital's airspace contains — infusion pumps, staff
        /// phones, other people's earbuds, and any Eddystone beacons belonging to someone else.
        /// </summary>
        public BeaconDefinition FindBeacon(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                return null;
            }

            for (int i = 0; i < beacons.Count; i++)
            {
                BeaconDefinition beacon = beacons[i];

                if (beacon != null && beacon.enabled && beacon.Matches(instanceId))
                {
                    return beacon;
                }
            }

            return null;
        }

        /// <summary>
        /// True if a scanned namespace belongs to this deployment. Checked before the instance
        /// lookup so that another Eddystone deployment in the same building — a supplier's asset
        /// tags, say — cannot collide with your instance numbering.
        /// </summary>
        public bool IsOurNamespace(string scannedNamespace)
        {
            if (string.IsNullOrEmpty(eddystoneNamespace))
            {
                return true;   // Not configured: accept everything and let Validate complain.
            }

            return string.Equals(
                BeaconDefinition.NormalizeHex(eddystoneNamespace),
                BeaconDefinition.NormalizeHex(scannedNamespace),
                StringComparison.Ordinal);
        }

        /// <summary>Exact room lookup for the "go" button. Null if nothing matches.</summary>
        public RoomNode FindRoom(string query)
        {
            for (int i = 0; i < rooms.Count; i++)
            {
                RoomNode room = rooms[i];

                if (room != null && room.publiclyRoutable && room.Matches(query))
                {
                    return room;
                }
            }

            return null;
        }

        /// <summary>Prefix suggestions for the room-number keypad, best-first, capped.</summary>
        public List<RoomNode> SuggestRooms(string partialQuery, int maxResults = 6)
        {
            var results = new List<RoomNode>();

            if (string.IsNullOrWhiteSpace(partialQuery))
            {
                return results;
            }

            for (int i = 0; i < rooms.Count && results.Count < maxResults; i++)
            {
                RoomNode room = rooms[i];

                if (room != null && room.publiclyRoutable && room.StartsWith(partialQuery))
                {
                    results.Add(room);
                }
            }

            return results;
        }

        /// <summary>Finds a QR anchor by its encoded payload. Null if the code is not ours.</summary>
        public QrAnchor FindQrAnchor(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return null;
            }

            for (int i = 0; i < qrAnchors.Count; i++)
            {
                QrAnchor anchor = qrAnchors[i];

                if (anchor != null &&
                    string.Equals(anchor.code, code.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return anchor;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------
        // Walkability helpers
        // ------------------------------------------------------------------

        /// <summary>The hallway segment whose centre line is closest to a floor-space point.</summary>
        public HallwaySegment NearestSegment(Vector2 floorPoint)
        {
            HallwaySegment best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < hallways.Count; i++)
            {
                HallwaySegment segment = hallways[i];

                if (segment == null || !segment.IsValid)
                {
                    continue;
                }

                float distance = segment.DistanceToCenterLine(floorPoint);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = segment;
                }
            }

            return best;
        }

        /// <summary>
        /// Pulls a point onto walkable hallway. A trilaterated fix regularly lands a metre inside
        /// a wall; rather than let the racing line clip through an exam room, we snap it back to
        /// the nearest corridor. Points already inside a corridor are returned untouched.
        /// </summary>
        public Vector2 SnapToWalkable(Vector2 floorPoint)
        {
            HallwaySegment segment = NearestSegment(floorPoint);

            if (segment == null)
            {
                return floorPoint;
            }

            float halfWidthSurveyUnits = ToSurveyUnits((ToMeters(segment.width) * 0.5f) - wallClearance);
            halfWidthSurveyUnits = Mathf.Max(halfWidthSurveyUnits, 0.05f);

            Vector2 onCenterLine = segment.ClosestPointOnCenterLine(floorPoint);
            Vector2 offset = floorPoint - onCenterLine;

            if (offset.magnitude <= halfWidthSurveyUnits)
            {
                return floorPoint;
            }

            return onCenterLine + offset.normalized * halfWidthSurveyUnits;
        }

        /// <summary>True if a point sits inside any corridor footprint.</summary>
        public bool IsWalkable(Vector2 floorPoint)
        {
            for (int i = 0; i < hallways.Count; i++)
            {
                HallwaySegment segment = hallways[i];

                if (segment != null && segment.IsValid && segment.Contains(floorPoint))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Bounding rectangle of everything in the survey, in floor space. Used by the editor
        /// window to frame the map and by the debug HUD to draw a mini-map.
        /// </summary>
        public Rect FloorBounds
        {
            get
            {
                bool any = false;
                float minX = 0f, minY = 0f, maxX = 0f, maxY = 0f;

                void Include(Vector2 point)
                {
                    if (!any)
                    {
                        minX = maxX = point.x;
                        minY = maxY = point.y;
                        any = true;
                        return;
                    }

                    minX = Mathf.Min(minX, point.x);
                    minY = Mathf.Min(minY, point.y);
                    maxX = Mathf.Max(maxX, point.x);
                    maxY = Mathf.Max(maxY, point.y);
                }

                foreach (HallwaySegment segment in hallways)
                {
                    if (segment == null)
                    {
                        continue;
                    }

                    Include(segment.start);
                    Include(segment.end);
                }

                foreach (RoomNode room in rooms)
                {
                    if (room != null)
                    {
                        Include(room.doorPosition);
                    }
                }

                foreach (BeaconDefinition beacon in beacons)
                {
                    if (beacon != null)
                    {
                        Include(beacon.position);
                    }
                }

                if (!any)
                {
                    return new Rect(0f, 0f, 1f, 1f);
                }

                return Rect.MinMaxRect(minX, minY, maxX, maxY);
            }
        }

        // ------------------------------------------------------------------
        // Validation
        // ------------------------------------------------------------------

        /// <summary>
        /// Sanity-checks the survey and returns human-readable problems. Called by the editor
        /// inspector and worth calling on startup in a development build — a duplicated room
        /// number or a beacon nobody can reach will otherwise show up as "the app is broken"
        /// during a demo in front of the Assistant Director.
        /// </summary>
        public List<string> Validate()
        {
            var problems = new List<string>();

            if (hallways.Count == 0)
            {
                problems.Add("No hallway segments. There is nothing to walk on.");
            }

            int enabledBeacons = 0;

            foreach (BeaconDefinition beacon in beacons)
            {
                if (beacon != null && beacon.enabled)
                {
                    enabledBeacons++;
                }
            }

            if (enabledBeacons < 3)
            {
                problems.Add($"Only {enabledBeacons} enabled beacon(s). Trilateration needs at " +
                             "least 3 in range at all times, so plan for 4 to 6 within reach of " +
                             "any point on the route.");
            }

            var seenRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (RoomNode room in rooms)
            {
                if (room == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(room.roomNumber))
                {
                    problems.Add("A room has no room number.");
                }
                else if (!seenRooms.Add(room.roomNumber))
                {
                    problems.Add($"Duplicate room number '{room.roomNumber}'.");
                }

                if (room.doorPosition == Vector2.zero)
                {
                    problems.Add($"Room '{room.roomNumber}' door position has not been surveyed " +
                                 "(still at the origin). If (0, 0) really is the door, ignore this.");
                }

                if (room.approachPosition == Vector2.zero && room.doorPosition != Vector2.zero)
                {
                    problems.Add($"Room '{room.roomNumber}' has no approach point. The path will " +
                                 "try to route into the doorway itself, which is not walkable.");
                }
                else if (!IsWalkable(room.approachPosition))
                {
                    problems.Add($"Room '{room.roomNumber}' approach point is not inside any " +
                                 "hallway. It is unreachable.");
                }
            }

            if (BeaconDefinition.NormalizeHex(eddystoneNamespace).Length != 20)
            {
                problems.Add("Eddystone namespace must be exactly 10 bytes — 20 hex characters. " +
                             "Set the same value on every beacon in the KBeacon app.");
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (BeaconDefinition beacon in beacons)
            {
                if (beacon == null)
                {
                    continue;
                }

                if (!beacon.HasValidInstanceId)
                {
                    problems.Add($"Beacon '{beacon.DisplayName}' has no valid Eddystone instance ID " +
                                 "(expected 12 hex characters) and can never be matched to a scan.");
                }
                else if (!seenIds.Add(beacon.NormalizedInstanceId))
                {
                    problems.Add($"Duplicate instance ID '{beacon.NormalizedInstanceId}'. Two " +
                                 "beacons broadcasting the same identity will pull the solved " +
                                 "position toward the midpoint between them.");
                }

                if (beacon.position == Vector2.zero)
                {
                    problems.Add($"Beacon '{beacon.DisplayName}' position has not been surveyed " +
                                 "(still at the origin). If (0, 0) really is where it is mounted, " +
                                 "ignore this.");
                }

                if (Mathf.Approximately(beacon.txPowerAtOneMeter, -62f))
                {
                    problems.Add($"Beacon '{beacon.DisplayName}' still has the default TX power. " +
                                 "Run BeaconSurveyTool on it — guessed calibration is the single " +
                                 "biggest source of position error.");
                }
            }

            if (qrAnchors.Count == 0)
            {
                problems.Add("No QR anchors. Without one the app has no way to know which " +
                             "direction the visitor is facing when they start.");
            }

            problems.AddRange(FindUnweldedJunctions());

            return problems;
        }

        /// <summary>
        /// Flags pairs of hallway endpoints that are close enough to be the same corner but not
        /// close enough for FloorGeometryBuilder to treat them as one — the "no route found" bug
        /// with no visible cause, caused by a paced corridor ending 12 cm short of the one it
        /// should meet. Anything touching exactly (distance 0, e.g. after a weld) is not flagged;
        /// anything further apart than the weld tool's own radius is assumed to be deliberate.
        /// </summary>
        private List<string> FindUnweldedJunctions()
        {
            const float weldRadius = 0.6f;
            var problems = new List<string>();

            for (int i = 0; i < hallways.Count; i++)
            {
                HallwaySegment a = hallways[i];

                if (a == null || !a.IsValid)
                {
                    continue;
                }

                // j starts at i + 1 so each pair is checked exactly once.
                for (int j = i + 1; j < hallways.Count; j++)
                {
                    HallwaySegment b = hallways[j];

                    if (b == null || !b.IsValid)
                    {
                        continue;
                    }

                    float closest = Mathf.Min(
                        Vector2.Distance(a.start, b.start),
                        Mathf.Min(Vector2.Distance(a.start, b.end),
                        Mathf.Min(Vector2.Distance(a.end, b.start),
                                  Vector2.Distance(a.end, b.end))));

                    if (closest > 0f && closest <= weldRadius)
                    {
                        string nameA = string.IsNullOrEmpty(a.label) ? $"hallway #{i}" : a.label;
                        string nameB = string.IsNullOrEmpty(b.label) ? $"hallway #{j}" : b.label;

                        problems.Add(
                            $"'{nameA}' and '{nameB}' have endpoints {closest * 100f:F0} cm apart. " +
                            "If they are meant to meet at a corner, run 'Weld hallway endpoints' — " +
                            "NavMesh treats a gap this small as a wall.");
                    }
                }
            }

            return problems;
        }
    }
}

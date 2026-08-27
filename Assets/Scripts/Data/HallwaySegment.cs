using System;
using UnityEngine;

namespace Wayfinding.Data
{
    /// <summary>
    /// One straight, walkable run of hallway, described as a centre line plus a width.
    ///
    /// This is the only geometry primitive in the whole app. A floor is just a list of these.
    /// Corners are implicit: two segments that share an endpoint form a turn, and
    /// FloorGeometryBuilder fills the corner in so the NavMesh is continuous around it.
    ///
    /// Measuring this is the paced-out survey work: walk the corridor, record where the
    /// centre line starts and ends, and measure wall-to-wall width once per corridor.
    /// </summary>
    [Serializable]
    public class HallwaySegment
    {
        [Tooltip("Optional label to keep long corridor lists readable, e.g. 'Main corridor - east'.")]
        public string label = "";

        [Tooltip("Centre-line start, in floor space (metres).")]
        public Vector2 start;

        [Tooltip("Centre-line end, in floor space (metres).")]
        public Vector2 end;

        [Tooltip("Wall-to-wall width in metres. The walkable strip generated for NavMesh is " +
                 "inset from this by FloorMap.wallClearance so the path never hugs a wall.")]
        public float width = 2.4f;

        /// <summary>Length of the centre line in metres.</summary>
        public float Length => Vector2.Distance(start, end);

        /// <summary>Unit vector pointing from start to end. Zero if the segment is degenerate.</summary>
        public Vector2 Direction
        {
            get
            {
                Vector2 delta = end - start;
                float magnitude = delta.magnitude;
                return magnitude < 0.0001f ? Vector2.zero : delta / magnitude;
            }
        }

        /// <summary>Perpendicular to <see cref="Direction"/>, used to offset the walkable edges.</summary>
        public Vector2 Normal
        {
            get
            {
                Vector2 direction = Direction;
                return new Vector2(-direction.y, direction.x);
            }
        }

        /// <summary>Midpoint of the centre line.</summary>
        public Vector2 Center => (start + end) * 0.5f;

        /// <summary>
        /// Closest point on the centre line to an arbitrary point, clamped to the segment ends.
        /// Used to snap a noisy beacon fix back onto walkable space.
        /// </summary>
        public Vector2 ClosestPointOnCenterLine(Vector2 point)
        {
            Vector2 delta = end - start;
            float lengthSquared = delta.sqrMagnitude;

            if (lengthSquared < 0.0001f)
            {
                return start;
            }

            float t = Mathf.Clamp01(Vector2.Dot(point - start, delta) / lengthSquared);
            return start + delta * t;
        }

        /// <summary>Perpendicular distance from a point to the centre line.</summary>
        public float DistanceToCenterLine(Vector2 point)
        {
            return Vector2.Distance(point, ClosestPointOnCenterLine(point));
        }

        /// <summary>
        /// True if the point lies inside the corridor's footprint, within an optional tolerance.
        /// </summary>
        public bool Contains(Vector2 point, float tolerance = 0f)
        {
            return DistanceToCenterLine(point) <= (width * 0.5f) + tolerance;
        }

        /// <summary>True if this segment shares an endpoint with another (they form a corner).</summary>
        public bool SharesEndpointWith(HallwaySegment other, float tolerance = 0.35f)
        {
            if (other == null)
            {
                return false;
            }

            return Vector2.Distance(start, other.start) <= tolerance ||
                   Vector2.Distance(start, other.end) <= tolerance ||
                   Vector2.Distance(end, other.start) <= tolerance ||
                   Vector2.Distance(end, other.end) <= tolerance;
        }

        /// <summary>Guards against typos in surveyed coordinates before geometry is built.</summary>
        public bool IsValid => Length > 0.2f && width > 0.5f;
    }
}

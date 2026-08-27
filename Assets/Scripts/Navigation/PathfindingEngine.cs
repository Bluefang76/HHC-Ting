using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Wayfinding.Data;

namespace Wayfinding.Navigation
{
    /// <summary>
    /// Why a path request did or did not succeed. Worth distinguishing, because each of these
    /// deserves different words on screen.
    /// </summary>
    public enum PathResult
    {
        Success,

        /// <summary>NavMesh has not been baked yet. Transient — wait for FloorGeometryBuilder.Built.</summary>
        NotReady,

        /// <summary>The visitor's position is not on or near walkable floor.</summary>
        StartOffMesh,

        /// <summary>The destination is not on or near walkable floor. Usually a survey error.</summary>
        DestinationOffMesh,

        /// <summary>Both ends are valid but nothing connects them — a gap in the corridor mesh.</summary>
        NoRoute,

        /// <summary>NavMesh returned a partial path. Reachable in part, blocked further along.</summary>
        Partial
    }

    /// <summary>
    /// Wraps Unity's NavMesh into the one question this app actually asks: "from here, in floor
    /// coordinates, to that room — what points do I walk through?"
    ///
    /// Everything here is deliberately stateless. Ask a question, get an answer. Deciding when to
    /// ask, and what to do with a stale answer, is NavigationSession's job. Keeping the two apart
    /// is what stops this file turning into the usual thousand-line navigation blob.
    ///
    /// One thing to be clear about: NavMesh works in Unity WORLD space, while the survey works in
    /// floor space, and the AR session moves the floor root around underneath both. So every
    /// query converts floor -> local -> world on the way in, and world -> local -> floor on the
    /// way back out. That is what floorRoot is for, and it must be the same transform
    /// FloorGeometryBuilder built the mesh under.
    /// </summary>
    public class PathfindingEngine : MonoBehaviour
    {
        [Header("Data")]
        public FloorMap floorMap;

        [Tooltip("The transform the floor mesh was built under. Floor coordinates are relative " +
                 "to this, and ARWorldAligner moves it to line the virtual floor up with the " +
                 "real one.")]
        public Transform floorRoot;

        [Tooltip("The builder that bakes the NavMesh. Used to check readiness before querying.")]
        public FloorGeometryBuilder geometryBuilder;

        [Header("Query tolerances")]
        [Tooltip("How far from a requested point NavMesh may look for walkable floor, in metres. " +
                 "Generous, because a beacon fix is routinely a metre or two off and refusing to " +
                 "path at all is a much worse failure than pathing from slightly the wrong spot.")]
        public float startSnapRadius = 4f;

        [Tooltip("Same, for the destination. Can be tighter — room approach points come from your " +
                 "survey, not from a radio.")]
        public float destinationSnapRadius = 2f;

        [Header("Path shaping")]
        [Tooltip("Resample the path so points are at most this far apart, in metres. NavMesh " +
                 "returns only corners; the racing line needs points along the straights too, or " +
                 "the ribbon will not follow the floor's slope and will cut through a doorway.")]
        [Range(0.25f, 3f)]
        public float resampleSpacing = 0.5f;

        [Tooltip("Rounds off corners so the line curves through a turn instead of hitting a hard " +
                 "vertex. Two passes looks like a person walking; more starts cutting the corner " +
                 "into the wall.")]
        [Range(0, 4)]
        public int cornerSmoothingPasses = 2;

        /// <summary>True when a path query can succeed.</summary>
        public bool IsReady => floorMap != null && floorRoot != null &&
                               (geometryBuilder == null || geometryBuilder.IsReady);

        private NavMeshPath _navMeshPath;
        private readonly List<Vector3> _rawCorners = new List<Vector3>();
        private readonly List<Vector3> _workingBuffer = new List<Vector3>();

        private void Awake()
        {
            _navMeshPath = new NavMeshPath();

            if (floorRoot == null)
            {
                floorRoot = transform;
            }
        }

        /// <summary>
        /// Finds a walkable route between two floor-space points.
        /// </summary>
        /// <param name="resultWorldPoints">
        /// Filled with the path in Unity world space, ready to hand to ARPathRenderer. Cleared
        /// first. Caller owns the list, so this allocates nothing per query.
        /// </param>
        public PathResult FindPath(
            Vector2 fromFloor,
            Vector2 toFloor,
            List<Vector3> resultWorldPoints)
        {
            resultWorldPoints.Clear();

            if (!IsReady)
            {
                return PathResult.NotReady;
            }

            Vector3 startWorld = FloorToWorld(fromFloor);
            Vector3 endWorld = FloorToWorld(toFloor);

            if (!NavMesh.SamplePosition(startWorld, out NavMeshHit startHit, startSnapRadius, NavMesh.AllAreas))
            {
                return PathResult.StartOffMesh;
            }

            if (!NavMesh.SamplePosition(endWorld, out NavMeshHit endHit, destinationSnapRadius, NavMesh.AllAreas))
            {
                return PathResult.DestinationOffMesh;
            }

            if (!NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, _navMeshPath))
            {
                return PathResult.NoRoute;
            }

            if (_navMeshPath.status == NavMeshPathStatus.PathInvalid || _navMeshPath.corners.Length < 2)
            {
                return PathResult.NoRoute;
            }

            _rawCorners.Clear();
            _rawCorners.AddRange(_navMeshPath.corners);

            SmoothCorners(_rawCorners, cornerSmoothingPasses);
            Resample(_rawCorners, resultWorldPoints, resampleSpacing);

            return _navMeshPath.status == NavMeshPathStatus.PathPartial
                ? PathResult.Partial
                : PathResult.Success;
        }

        /// <summary>Total walking distance along a path, in metres.</summary>
        public static float PathLength(IReadOnlyList<Vector3> worldPoints)
        {
            if (worldPoints == null || worldPoints.Count < 2)
            {
                return 0f;
            }

            float total = 0f;

            for (int i = 1; i < worldPoints.Count; i++)
            {
                total += Vector3.Distance(worldPoints[i - 1], worldPoints[i]);
            }

            return total;
        }

        /// <summary>
        /// Distance still to walk from an arbitrary point, measured along the path rather than
        /// straight-line. Projects the point onto the path first, so it stays honest when the
        /// visitor is a metre off to one side — which they always are.
        /// </summary>
        public static float RemainingDistance(IReadOnlyList<Vector3> worldPoints, Vector3 fromWorld)
        {
            if (worldPoints == null || worldPoints.Count < 2)
            {
                return 0f;
            }

            int nearestIndex = NearestSegmentIndex(worldPoints, fromWorld, out Vector3 projected);

            float remaining = Vector3.Distance(projected, worldPoints[nearestIndex + 1]);

            for (int i = nearestIndex + 1; i < worldPoints.Count - 1; i++)
            {
                remaining += Vector3.Distance(worldPoints[i], worldPoints[i + 1]);
            }

            return remaining;
        }

        /// <summary>
        /// Perpendicular distance from a point to the path. NavigationSession uses this to notice
        /// the visitor has wandered off and to trigger a recompute.
        /// </summary>
        public static float DistanceFromPath(IReadOnlyList<Vector3> worldPoints, Vector3 fromWorld)
        {
            if (worldPoints == null || worldPoints.Count == 0)
            {
                return float.MaxValue;
            }

            if (worldPoints.Count == 1)
            {
                return Vector3.Distance(worldPoints[0], fromWorld);
            }

            NearestSegmentIndex(worldPoints, fromWorld, out Vector3 projected);
            return Vector3.Distance(projected, fromWorld);
        }

        /// <summary>
        /// Index of the path segment closest to a point, plus the projected point on it.
        /// Comparison happens on the horizontal plane only — the visitor's height above the path
        /// depends on how they are holding the phone and says nothing about progress.
        /// </summary>
        public static int NearestSegmentIndex(
            IReadOnlyList<Vector3> worldPoints,
            Vector3 fromWorld,
            out Vector3 projectedPoint)
        {
            int bestIndex = 0;
            float bestDistance = float.MaxValue;
            projectedPoint = worldPoints[0];

            for (int i = 0; i < worldPoints.Count - 1; i++)
            {
                Vector3 a = worldPoints[i];
                Vector3 b = worldPoints[i + 1];
                Vector3 ab = b - a;
                float lengthSquared = ab.sqrMagnitude;

                Vector3 candidate;

                if (lengthSquared < 0.0001f)
                {
                    candidate = a;
                }
                else
                {
                    float t = Mathf.Clamp01(Vector3.Dot(fromWorld - a, ab) / lengthSquared);
                    candidate = a + (ab * t);
                }

                Vector3 flatDelta = candidate - fromWorld;
                flatDelta.y = 0f;
                float distance = flatDelta.sqrMagnitude;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                    projectedPoint = candidate;
                }
            }

            return bestIndex;
        }

        // ------------------------------------------------------------------
        // Coordinate conversion
        // ------------------------------------------------------------------

        public Vector3 FloorToWorld(Vector2 floorPoint)
        {
            return floorRoot.TransformPoint(floorMap.FloorToLocal(floorPoint));
        }

        public Vector2 WorldToFloor(Vector3 worldPoint)
        {
            return floorMap.LocalToFloor(floorRoot.InverseTransformPoint(worldPoint));
        }

        // ------------------------------------------------------------------
        // Path shaping
        // ------------------------------------------------------------------

        /// <summary>
        /// Chaikin corner cutting. Each pass replaces every interior corner with two points at
        /// 25% and 75% along its neighbouring edges, which rounds the turn. Endpoints are kept
        /// exactly, so the line still starts at the visitor's feet and ends at the door.
        /// </summary>
        private void SmoothCorners(List<Vector3> points, int passes)
        {
            for (int pass = 0; pass < passes && points.Count >= 3; pass++)
            {
                _workingBuffer.Clear();
                _workingBuffer.Add(points[0]);

                for (int i = 0; i < points.Count - 1; i++)
                {
                    Vector3 a = points[i];
                    Vector3 b = points[i + 1];

                    // Leave long straights alone. Subdividing them costs points and gains nothing,
                    // and the resample step below handles spacing anyway.
                    if (Vector3.Distance(a, b) < 0.6f)
                    {
                        _workingBuffer.Add(b);
                        continue;
                    }

                    _workingBuffer.Add(Vector3.Lerp(a, b, 0.25f));
                    _workingBuffer.Add(Vector3.Lerp(a, b, 0.75f));
                }

                _workingBuffer.Add(points[points.Count - 1]);

                points.Clear();
                points.AddRange(_workingBuffer);
            }
        }

        /// <summary>
        /// Walks the polyline and emits points at a fixed spacing. Gives the ribbon mesh enough
        /// vertices to follow the floor, and gives the flow animation something even to travel
        /// along.
        /// </summary>
        private static void Resample(
            IReadOnlyList<Vector3> source,
            List<Vector3> destination,
            float spacing)
        {
            destination.Clear();

            if (source.Count == 0)
            {
                return;
            }

            destination.Add(source[0]);
            spacing = Mathf.Max(spacing, 0.05f);

            float carryOver = 0f;

            for (int i = 0; i < source.Count - 1; i++)
            {
                Vector3 a = source[i];
                Vector3 b = source[i + 1];
                float segmentLength = Vector3.Distance(a, b);

                if (segmentLength < 0.0001f)
                {
                    continue;
                }

                float travelled = spacing - carryOver;

                while (travelled < segmentLength)
                {
                    destination.Add(Vector3.Lerp(a, b, travelled / segmentLength));
                    travelled += spacing;
                }

                carryOver = segmentLength - (travelled - spacing);
            }

            Vector3 last = source[source.Count - 1];

            // Avoid a duplicated final point when the last resample landed almost on the end.
            if (Vector3.Distance(destination[destination.Count - 1], last) > 0.05f)
            {
                destination.Add(last);
            }
            else
            {
                destination[destination.Count - 1] = last;
            }
        }

        /// <summary>Plain-English explanation of a failed query, for logs and the debug HUD.</summary>
        public static string Explain(PathResult result)
        {
            switch (result)
            {
                case PathResult.Success:
                    return "Route found.";

                case PathResult.NotReady:
                    return "The floor has not finished loading yet.";

                case PathResult.StartOffMesh:
                    return "Your position is not on a mapped hallway. Either the beacon fix is " +
                           "badly off, or you are somewhere the survey does not cover.";

                case PathResult.DestinationOffMesh:
                    return "That room's approach point is not on a mapped hallway. Fix the room's " +
                           "approachPosition in the FloorMap.";

                case PathResult.NoRoute:
                    return "No connected route exists. Usually a gap between two corridors that " +
                           "should meet — check that their endpoints actually share coordinates.";

                case PathResult.Partial:
                    return "Only part of the route is walkable.";

                default:
                    return "Unknown pathfinding result.";
            }
        }
    }
}

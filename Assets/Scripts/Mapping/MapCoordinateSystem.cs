using UnityEngine;

namespace Wayfinder.Mapping
{
    /// <summary>
    /// The single place where map coordinates become Unity world coordinates.
    ///
    /// Map space: meters, X-Y, origin at the floor's reference corner, +X along the
    /// main hallway. Unity world space: meters, X-Z on the ground plane, origin wherever
    /// the AR session started.
    ///
    /// Everything upstream of this speaks map coordinates. Everything downstream speaks
    /// Unity world. Nothing converts anywhere else.
    ///
    /// Plain C# — unit-testable off-device.
    /// </summary>
    public sealed class MapCoordinateSystem
    {
        /// <summary>World position that map-space origin maps to.</summary>
        public Vector3 WorldOrigin { get; private set; }

        /// <summary>Rotation, degrees about Y, from map +X to world +X.</summary>
        public float HeadingDegrees { get; private set; }

        /// <summary>Height of the floor plane in world space.</summary>
        public float FloorHeight { get; private set; }

        /// <summary>True once an alignment has been established (normally by the entrance QR).</summary>
        public bool IsAligned { get; private set; }

        /// <summary>
        /// Establish the map-to-world alignment. Called when the entrance QR is scanned:
        /// the code encodes the sign's known map position and heading, which gives a
        /// trustworthy origin without any beacon math.
        /// </summary>
        public void Align(Vector2 knownMapPosition, float knownMapHeading, Vector3 worldPosition, float worldHeading, float floorHeight)
        {
            // TODO: solve origin and heading such that knownMapPosition maps to
            //       worldPosition and map heading maps to world heading.
            FloorHeight = floorHeight;
            IsAligned = true;
        }

        /// <summary>
        /// Nudge the alignment toward a new estimate. Used for slow drift correction
        /// against high-confidence beacon fixes.
        ///
        /// Must be gentle. Snapping the line is more alarming to a user than being half
        /// a meter off — see docs/architecture.md.
        /// </summary>
        public void BlendTowards(Vector3 worldOrigin, float headingDegrees, float t)
        {
            WorldOrigin = Vector3.Lerp(WorldOrigin, worldOrigin, t);
            HeadingDegrees = Mathf.LerpAngle(HeadingDegrees, headingDegrees, t);
        }

        public Vector3 MapToWorld(Vector2 mapPosition)
        {
            var rotated = Quaternion.Euler(0f, HeadingDegrees, 0f) * new Vector3(mapPosition.x, 0f, mapPosition.y);
            return WorldOrigin + rotated + Vector3.up * FloorHeight;
        }

        public Vector2 WorldToMap(Vector3 worldPosition)
        {
            var local = Quaternion.Euler(0f, -HeadingDegrees, 0f) * (worldPosition - WorldOrigin);
            return new Vector2(local.x, local.z);
        }

        public void Reset()
        {
            WorldOrigin = Vector3.zero;
            HeadingDegrees = 0f;
            FloorHeight = 0f;
            IsAligned = false;
        }
    }
}

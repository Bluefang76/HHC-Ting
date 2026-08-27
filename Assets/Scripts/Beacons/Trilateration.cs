using System.Collections.Generic;
using UnityEngine;

namespace Wayfinder.Beacons
{
    /// <summary>One beacon's known position paired with a current distance estimate.</summary>
    public readonly struct DistanceObservation
    {
        public readonly Vector2 BeaconPosition;
        public readonly float Distance;

        public DistanceObservation(Vector2 beaconPosition, float distance)
        {
            BeaconPosition = beaconPosition;
            Distance = distance;
        }
    }

    /// <summary>Result of a position solve, with a confidence the caller must respect.</summary>
    public readonly struct PositionFix
    {
        public readonly Vector2 Position;
        public readonly float Confidence;   // 0 = useless, 1 = trust it
        public readonly int BeaconCount;
        public readonly bool IsValid;

        public PositionFix(Vector2 position, float confidence, int beaconCount)
        {
            Position = position;
            Confidence = confidence;
            BeaconCount = beaconCount;
            IsValid = true;
        }

        public static PositionFix Invalid => default;
    }

    /// <summary>
    /// Solves phone position from beacon distances. Because distances are noisy the
    /// circles never intersect at a point, so this is a least-squares multilateration:
    /// linearize against a reference beacon and solve the overdetermined system.
    ///
    /// Plain C#, no MonoBehaviour — unit-testable off-device.
    /// </summary>
    public static class Trilateration
    {
        public const int MinimumBeacons = 3;

        /// <summary>
        /// Solve for a 2D position in map coordinates.
        /// Returns <see cref="PositionFix.Invalid"/> when there is not enough usable
        /// geometry to answer honestly.
        /// </summary>
        public static PositionFix Solve(IReadOnlyList<DistanceObservation> observations)
        {
            if (observations == null || observations.Count < MinimumBeacons)
                return PositionFix.Invalid;

            // TODO: implement.
            //
            //  1. Sort by distance, keep the 3–5 nearest. Far beacons are mostly noise.
            //  2. Reject degenerate geometry: beacons nearly collinear along the corridor
            //     pin the along-hallway axis well and the across-hallway axis not at all.
            //     Check the spread of the beacon set before trusting the solve.
            //  3. Linearize: subtracting the reference beacon's circle equation from each
            //     other one removes the quadratic terms and leaves A·x = b.
            //  4. Solve the normal equations (AᵀA)x = Aᵀb — 2x2, invertible by hand.
            //  5. Confidence from residuals + beacon count + geometric spread.

            return PositionFix.Invalid;
        }

        /// <summary>
        /// Geometric quality of a beacon set, 0..1. Low means the beacons are too close
        /// to collinear for the solve to constrain both axes.
        /// </summary>
        public static float GeometricQuality(IReadOnlyList<DistanceObservation> observations)
        {
            // TODO: e.g. the ratio of the smaller to the larger eigenvalue of the beacon
            //       position covariance. Near 0 = collinear, near 1 = well spread.
            return 0f;
        }
    }
}

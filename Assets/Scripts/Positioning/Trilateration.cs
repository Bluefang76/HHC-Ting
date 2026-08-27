using System.Collections.Generic;
using UnityEngine;

namespace Wayfinding.Positioning
{
    /// <summary>
    /// Solves for a 2D position given several beacons at known places and a noisy distance to each.
    ///
    /// Pure maths — no MonoBehaviour, no scene, no side effects. That is deliberate: this is the
    /// one piece of the app whose correctness you can prove at your desk in milliseconds, and
    /// keeping it free of Unity means the EditMode tests run instantly.
    ///
    /// HOW IT WORKS
    /// Three circles that all pass through one point would be trilateration in a textbook. In a
    /// hospital corridor the distances each carry a metre or two of error, so the circles never
    /// meet at a point — they overlap in a smear. So instead of intersecting circles we minimise
    /// total error: find the point p where the sum of weighted squared differences between
    /// "distance from p to each beacon" and "distance we measured" is smallest.
    ///
    ///   1. Seed with a weighted centroid — nearest beacons pull hardest. Crude but always valid,
    ///      and it keeps Gauss-Newton from wandering off.
    ///   2. Refine with Gauss-Newton. Each iteration linearises the problem around the current
    ///      guess and solves a 2x2 system for the step. Converges in 3 to 6 iterations here.
    ///   3. Report back not just the position but how much to trust it.
    ///
    /// THE HALLWAY PROBLEM — read this before mounting anything
    /// A straight corridor tempts you into mounting every beacon in a line along one wall.
    /// Do not. Trilateration from collinear anchors is well determined ALONG the line and nearly
    /// undetermined ACROSS it: mathematically the solution can mirror to either side of the row
    /// with almost the same error. In practice the position flips across the corridor and the
    /// racing line jumps from one wall to the other while the visitor stands still.
    ///
    /// Alternate walls as you go down the corridor. The solver reports this condition as a low
    /// <see cref="TrilaterationResult.GeometryQuality"/>, and BeaconManager falls back to
    /// hallway-constrained positioning when it sees one — but good geometry is worth far more
    /// than good recovery code.
    /// </summary>
    public static class Trilateration
    {
        /// <summary>One beacon's contribution: where it is, how far we think we are, how much we trust it.</summary>
        public readonly struct Anchor
        {
            public readonly Vector2 Position;
            public readonly float Distance;
            public readonly float Weight;

            public Anchor(Vector2 position, float distance, float weight = 1f)
            {
                Position = position;
                Distance = Mathf.Max(distance, 0.01f);
                Weight = Mathf.Max(weight, 0.0001f);
            }
        }

        public readonly struct TrilaterationResult
        {
            public readonly bool Success;
            public readonly Vector2 Position;

            /// <summary>
            /// Root-mean-square disagreement between measured and implied distances, in metres.
            /// Around 1 m is healthy indoors. Above 3 m means a beacon is lying to you — usually
            /// one that is round a corner, or one whose TX power was never surveyed.
            /// </summary>
            public readonly float ResidualMeters;

            /// <summary>
            /// 0 to 1, how well the anchors surround the solved point. Near 1 means they box it
            /// in from several directions. Near 0 means they are in a line and the answer is
            /// only reliable along that line. See the hallway warning in the class summary.
            /// </summary>
            public readonly float GeometryQuality;

            public readonly int AnchorsUsed;
            public readonly string FailureReason;

            public TrilaterationResult(
                bool success,
                Vector2 position,
                float residualMeters,
                float geometryQuality,
                int anchorsUsed,
                string failureReason = null)
            {
                Success = success;
                Position = position;
                ResidualMeters = residualMeters;
                GeometryQuality = geometryQuality;
                AnchorsUsed = anchorsUsed;
                FailureReason = failureReason;
            }

            /// <summary>
            /// A single 0-1 score combining fit and geometry, for the HUD and for deciding
            /// whether to trust a fix enough to redraw the path.
            /// </summary>
            public float Confidence
            {
                get
                {
                    if (!Success)
                    {
                        return 0f;
                    }

                    float fit = 1f / (1f + (ResidualMeters * ResidualMeters * 0.4f));
                    float anchorBonus = Mathf.Clamp01((AnchorsUsed - 2) / 3f);
                    return Mathf.Clamp01(fit * Mathf.Lerp(0.35f, 1f, GeometryQuality) *
                                         Mathf.Lerp(0.5f, 1f, anchorBonus));
                }
            }
        }

        private const int MaxIterations = 12;
        private const float ConvergenceMeters = 0.02f;

        /// <summary>
        /// Solves for position. All distances and positions must be in the same units — metres,
        /// if you are calling this from BeaconManager.
        /// </summary>
        /// <param name="anchors">Three or more beacons. Two produces an ambiguous answer and is rejected.</param>
        /// <param name="initialGuess">
        /// Previous frame's position, if there is one. Seeding from it makes the solver converge
        /// faster and land on the nearer of two ambiguous solutions, which is almost always the
        /// right one for someone who is walking rather than teleporting.
        /// </param>
        /// <param name="hasInitialGuess">False on the first fix of a session.</param>
        public static TrilaterationResult Solve(
            IReadOnlyList<Anchor> anchors,
            Vector2 initialGuess = default,
            bool hasInitialGuess = false)
        {
            if (anchors == null || anchors.Count < 3)
            {
                return new TrilaterationResult(false, initialGuess, 0f, 0f, anchors?.Count ?? 0,
                    "Need at least 3 beacons in range. Two circles intersect in two places and " +
                    "there is no way to tell which one you are standing at.");
            }

            Vector2 position = hasInitialGuess ? initialGuess : WeightedCentroid(anchors);

            if (float.IsNaN(position.x) || float.IsNaN(position.y))
            {
                position = WeightedCentroid(anchors);
            }

            for (int iteration = 0; iteration < MaxIterations; iteration++)
            {
                // Weighted normal equations for the Gauss-Newton step:
                //   [a b][dx]   [-e]
                //   [b c][dy] = [-f]
                float a = 0f, b = 0f, c = 0f, e = 0f, f = 0f;

                for (int i = 0; i < anchors.Count; i++)
                {
                    Anchor anchor = anchors[i];
                    Vector2 offset = position - anchor.Position;
                    float modelled = offset.magnitude;

                    if (modelled < 0.0001f)
                    {
                        // Sitting exactly on a beacon. Nudge off it so the Jacobian stays defined.
                        offset = new Vector2(0.001f, 0f);
                        modelled = 0.001f;
                    }

                    // Jacobian row: d(distance)/d(position) is just the unit direction.
                    float jx = offset.x / modelled;
                    float jy = offset.y / modelled;
                    float residual = modelled - anchor.Distance;
                    float w = anchor.Weight;

                    a += w * jx * jx;
                    b += w * jx * jy;
                    c += w * jy * jy;
                    e += w * jx * residual;
                    f += w * jy * residual;
                }

                float determinant = (a * c) - (b * b);

                if (Mathf.Abs(determinant) < 1e-9f)
                {
                    // Degenerate geometry — anchors are effectively collinear. Keep whatever we
                    // have rather than dividing by nothing.
                    break;
                }

                float stepX = ((-e * c) - (-f * b)) / determinant;
                float stepY = ((a * -f) - (b * -e)) / determinant;

                // Clamp the step. An unbounded Gauss-Newton step with one badly wrong distance
                // can throw the position across the building in a single iteration.
                var step = new Vector2(stepX, stepY);
                float stepLength = step.magnitude;

                if (stepLength > 5f)
                {
                    step *= 5f / stepLength;
                    stepLength = 5f;
                }

                position += step;

                if (stepLength < ConvergenceMeters)
                {
                    break;
                }
            }

            float residualRms = ComputeResidualRms(anchors, position);
            float geometry = ComputeGeometryQuality(anchors, position);

            bool plausible = !float.IsNaN(position.x) && !float.IsNaN(position.y) &&
                             !float.IsInfinity(position.x) && !float.IsInfinity(position.y);

            if (!plausible)
            {
                return new TrilaterationResult(false, initialGuess, 0f, 0f, anchors.Count,
                    "Solver diverged. Usually means one beacon's surveyed position is wrong.");
            }

            return new TrilaterationResult(true, position, residualRms, geometry, anchors.Count);
        }

        /// <summary>
        /// Inverse-distance-weighted average of the anchors. Always produces something sane,
        /// never produces something precise. Used to seed the solver, and as the fallback when
        /// there are only two beacons in range.
        /// </summary>
        public static Vector2 WeightedCentroid(IReadOnlyList<Anchor> anchors)
        {
            if (anchors == null || anchors.Count == 0)
            {
                return Vector2.zero;
            }

            Vector2 accumulated = Vector2.zero;
            float totalWeight = 0f;

            for (int i = 0; i < anchors.Count; i++)
            {
                Anchor anchor = anchors[i];

                // Weight by 1/d^2: a beacon 2 m away says far more about where you are than one
                // 20 m away, and says it about a hundred times more confidently.
                float weight = anchor.Weight / Mathf.Max(anchor.Distance * anchor.Distance, 0.25f);
                accumulated += anchor.Position * weight;
                totalWeight += weight;
            }

            return totalWeight > 0f ? accumulated / totalWeight : anchors[0].Position;
        }

        private static float ComputeResidualRms(IReadOnlyList<Anchor> anchors, Vector2 position)
        {
            float sumSquares = 0f;
            float totalWeight = 0f;

            for (int i = 0; i < anchors.Count; i++)
            {
                Anchor anchor = anchors[i];
                float error = Vector2.Distance(position, anchor.Position) - anchor.Distance;
                sumSquares += anchor.Weight * error * error;
                totalWeight += anchor.Weight;
            }

            return totalWeight > 0f ? Mathf.Sqrt(sumSquares / totalWeight) : 0f;
        }

        /// <summary>
        /// How well the anchors surround the solved point, from the spread of bearings to them.
        ///
        /// Builds the 2x2 scatter matrix of unit direction vectors and compares its eigenvalues.
        /// Directions spread evenly around the compass give two similar eigenvalues and a score
        /// near 1. Directions all along one axis — the classic beacons-in-a-row corridor — give
        /// one large and one near-zero eigenvalue and a score near 0.
        /// </summary>
        private static float ComputeGeometryQuality(IReadOnlyList<Anchor> anchors, Vector2 position)
        {
            if (anchors.Count < 3)
            {
                return 0f;
            }

            float sxx = 0f, sxy = 0f, syy = 0f;
            int counted = 0;

            for (int i = 0; i < anchors.Count; i++)
            {
                Vector2 offset = anchors[i].Position - position;

                if (offset.sqrMagnitude < 0.0001f)
                {
                    continue;
                }

                Vector2 direction = offset.normalized;
                sxx += direction.x * direction.x;
                sxy += direction.x * direction.y;
                syy += direction.y * direction.y;
                counted++;
            }

            if (counted < 3)
            {
                return 0f;
            }

            sxx /= counted;
            sxy /= counted;
            syy /= counted;

            // Closed-form eigenvalues of a symmetric 2x2 matrix.
            float trace = sxx + syy;
            float determinant = (sxx * syy) - (sxy * sxy);
            float discriminant = Mathf.Max((trace * trace * 0.25f) - determinant, 0f);
            float root = Mathf.Sqrt(discriminant);

            float larger = (trace * 0.5f) + root;
            float smaller = (trace * 0.5f) - root;

            if (larger < 0.0001f)
            {
                return 0f;
            }

            // Ratio of the two: 1 means evenly surrounded, 0 means a straight line.
            return Mathf.Clamp01(smaller / larger);
        }
    }
}

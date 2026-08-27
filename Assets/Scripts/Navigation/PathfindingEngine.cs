using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Wayfinder.Navigation
{
    /// <summary>
    /// Computes the walkable route across the 1:1 virtual replica of the floor.
    ///
    /// Requires com.unity.ai.navigation for NavMeshSurface / runtime baking — see
    /// docs/setup-and-packages.md.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PathfindingEngine : MonoBehaviour
    {
        [Tooltip("How far a point may be from the NavMesh and still snap onto it, meters.")]
        [SerializeField] private float sampleRadius = 1.5f;

        [Tooltip("Distance to pull the drawn line away from corners, meters. Stops the line "
               + "telling someone to walk into a doorframe.")]
        [SerializeField] private float cornerClearance = 0.4f;

        private readonly NavMeshPath _path = new();
        private readonly List<Vector3> _corners = new();

        /// <summary>
        /// Compute a route in Unity world space. Returns false when no route exists —
        /// the caller must surface that honestly rather than drawing a partial line.
        /// </summary>
        public bool TryComputePath(Vector3 fromWorld, Vector3 toWorld, List<Vector3> results)
        {
            results.Clear();

            if (!NavMesh.SamplePosition(fromWorld, out var startHit, sampleRadius, NavMesh.AllAreas)) return false;
            if (!NavMesh.SamplePosition(toWorld, out var endHit, sampleRadius, NavMesh.AllAreas)) return false;
            if (!NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, _path)) return false;
            if (_path.status != NavMeshPathStatus.PathComplete) return false;

            _corners.Clear();
            _corners.AddRange(_path.corners);

            Smooth(_corners, results);
            return results.Count >= 2;
        }

        /// <summary>
        /// The raw NavMesh path hugs corners tightly and is drawn as hard angles. Round
        /// the turns and bias the line toward the corridor centerline so it reads as a
        /// racing line rather than a set of instructions.
        /// </summary>
        private void Smooth(List<Vector3> raw, List<Vector3> smoothed)
        {
            // TODO: chaikin or catmull-rom through the corners, then push each point
            //       cornerClearance away from the inside of every turn.
            _ = cornerClearance;
            smoothed.AddRange(raw);
        }

        /// <summary>Remaining distance along a computed path, meters. Drives the "X m to go" readout.</summary>
        public static float PathLength(IReadOnlyList<Vector3> path)
        {
            var total = 0f;
            for (var i = 1; i < path.Count; i++) total += Vector3.Distance(path[i - 1], path[i]);
            return total;
        }
    }
}

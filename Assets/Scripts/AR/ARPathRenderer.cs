using System.Collections.Generic;
using UnityEngine;

namespace Wayfinder.AR
{
    /// <summary>
    /// Draws the racing line: the path ribbon laid on the floor plane and held in
    /// real-world space.
    ///
    /// Design constraints worth keeping in mind — it has to be legible on a reflective
    /// hospital floor under fluorescent light, and it has to look like it belongs to the
    /// building rather than to the screen.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ARPathRenderer : MonoBehaviour
    {
        [Header("Appearance")]
        [SerializeField] private float lineWidth = 0.35f;

        [Tooltip("Height above the detected floor plane, meters. Small — the line should "
               + "sit on the floor, not float.")]
        [SerializeField] private float floorOffset = 0.02f;

        [Tooltip("How far ahead of the user to draw, meters. Drawing the whole route at "
               + "once is noisier and less trustworthy than drawing the next stretch.")]
        [SerializeField] private float visibleDistance = 12f;

        [Header("References")]
        [SerializeField] private LineRenderer lineRenderer;

        private readonly List<Vector3> _points = new();

        /// <summary>Replace the drawn path. Points are in Unity world space.</summary>
        public void SetPath(IReadOnlyList<Vector3> worldPoints)
        {
            _points.Clear();
            if (worldPoints != null) _points.AddRange(worldPoints);
            Redraw();
        }

        public void Clear()
        {
            _points.Clear();
            if (lineRenderer != null) lineRenderer.positionCount = 0;
        }

        private void Redraw()
        {
            if (lineRenderer == null) return;

            // TODO:
            //  - project points onto the detected floor plane + floorOffset,
            //  - trim to visibleDistance ahead of the user,
            //  - fade the far end rather than cutting it off hard,
            //  - animate flow along the line to imply direction.
            _ = lineWidth;
            _ = visibleDistance;

            lineRenderer.positionCount = _points.Count;
            for (var i = 0; i < _points.Count; i++)
            {
                lineRenderer.SetPosition(i, _points[i] + Vector3.up * floorOffset);
            }
        }
    }
}

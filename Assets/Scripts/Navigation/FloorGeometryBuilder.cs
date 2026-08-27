using System;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using Wayfinding.Data;

namespace Wayfinding.Navigation
{
    /// <summary>
    /// Turns the FloorMap's list of hallway segments into an actual triangle mesh, then bakes a
    /// NavMesh onto it.
    ///
    /// This script exists because of a gap that is easy to miss when planning: NavMesh does not
    /// path across coordinates, it paths across GEOMETRY. A list of start and end points is not a
    /// surface. Something has to turn "the main corridor runs from (0,0) to (34,0) and is 2.4 m
    /// wide" into triangles NavMesh can bake. That is this file, and without it PathfindingEngine
    /// returns "no path" for every query and the reason is not obvious.
    ///
    /// Building at runtime rather than modelling the floor by hand in the editor is the right
    /// call here: your survey numbers will change — several times, once you start pacing corners
    /// properly — and every change would otherwise mean remodelling geometry. Edit the FloorMap,
    /// press play, the floor is rebuilt.
    ///
    /// REQUIRES the AI Navigation package (com.unity.ai.navigation), which is what provides
    /// NavMeshSurface and runtime baking. Window > Package Manager > Unity Registry > AI Navigation.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class FloorGeometryBuilder : MonoBehaviour
    {
        [Header("Data")]
        public FloorMap floorMap;

        [Header("Build")]
        [Tooltip("Build and bake automatically on Start. Turn off if something else — a QR scan, " +
                 "a floor selector — decides when the floor is known.")]
        public bool buildOnStart = true;

        [Tooltip("Extra length added to each end of every corridor, in metres. Corridors that " +
                 "meet at a corner need a little overlap or the baked surface can end up with a " +
                 "hairline gap at the junction, which NavMesh treats as a wall.")]
        [Range(0f, 1.5f)]
        public float endCapOverlap = 0.5f;

        [Header("Visibility")]
        [Tooltip("Show the generated floor. Off for the real app — the visitor should see their " +
                 "actual hallway through the camera, not a grey slab of it. On while you are " +
                 "checking your survey numbers in the scene view.")]
        public bool visualizeFloor;

        [Tooltip("Material used when visualizeFloor is on. A translucent unlit colour works best.")]
        public Material debugMaterial;

        /// <summary>Fires once the mesh is built and the NavMesh is baked. Wait for this before pathing.</summary>
        public event Action Built;

        /// <summary>True once a successful bake has completed.</summary>
        public bool IsReady { get; private set; }

        /// <summary>The generated mesh, kept so it can be released on destroy.</summary>
        public Mesh GeneratedMesh { get; private set; }

        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private NavMeshSurface _navMeshSurface;

        private void Awake()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();

            _navMeshSurface = GetComponent<NavMeshSurface>();

            if (_navMeshSurface == null)
            {
                _navMeshSurface = gameObject.AddComponent<NavMeshSurface>();
            }

            // Collect only this object's own geometry. Left on "All", the bake would also try to
            // include AR planes, UI colliders and anything else that wandered into the scene.
            _navMeshSurface.collectObjects = CollectObjects.Children;
            _navMeshSurface.useGeometry = NavMeshCollectGeometry.RenderMeshes;
        }

        private void Start()
        {
            if (buildOnStart)
            {
                Rebuild();
            }
        }

        private void OnDestroy()
        {
            if (GeneratedMesh != null)
            {
                Destroy(GeneratedMesh);
            }
        }

        /// <summary>
        /// Regenerates the floor mesh and rebakes the NavMesh. Cheap enough to call whenever the
        /// survey changes; a floor of this size bakes in well under a second.
        /// </summary>
        public void Rebuild()
        {
            IsReady = false;

            if (floorMap == null)
            {
                Debug.LogError("[FloorGeometryBuilder] No FloorMap assigned.");
                return;
            }

            Mesh mesh = BuildMesh();

            if (mesh == null || mesh.vertexCount == 0)
            {
                Debug.LogError("[FloorGeometryBuilder] No valid hallway segments in the FloorMap. " +
                               "Nothing to walk on, so nothing to bake.");
                return;
            }

            if (GeneratedMesh != null)
            {
                Destroy(GeneratedMesh);
            }

            GeneratedMesh = mesh;
            _meshFilter.sharedMesh = mesh;

            _meshRenderer.enabled = true; // Must be enabled for the bake to see the render mesh.
            _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;

            if (debugMaterial != null)
            {
                _meshRenderer.sharedMaterial = debugMaterial;
            }

            _navMeshSurface.BuildNavMesh();

            // Now hide it, after the bake has read the geometry.
            _meshRenderer.enabled = visualizeFloor;

            IsReady = true;
            Built?.Invoke();

            Debug.Log($"[FloorGeometryBuilder] Baked {floorMap.hallways.Count} corridor(s), " +
                      $"{mesh.vertexCount} verts.");
        }

        /// <summary>
        /// Builds the walkable surface: one quad per corridor, plus a square patch at every
        /// junction so corners are continuous.
        /// </summary>
        public Mesh BuildMesh()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();

            foreach (HallwaySegment segment in floorMap.hallways)
            {
                if (segment == null || !segment.IsValid)
                {
                    continue;
                }

                AppendCorridorQuad(segment, vertices, triangles);
            }

            foreach (Vector2 junction in FindJunctions())
            {
                AppendJunctionPatch(junction, vertices, triangles);
            }

            if (vertices.Count == 0)
            {
                return null;
            }

            var mesh = new Mesh
            {
                name = $"FloorMesh_{floorMap.floorName}",
                indexFormat = vertices.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16
            };

            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
        }

        private void AppendCorridorQuad(
            HallwaySegment segment,
            List<Vector3> vertices,
            List<int> triangles)
        {
            // Inset from the walls so the path never routes flush against a baseboard, and
            // overlap the ends so junctions fuse rather than butt up against each other.
            float halfWidthMeters = Mathf.Max(
                (floorMap.ToMeters(segment.width) * 0.5f) - floorMap.wallClearance, 0.15f);

            Vector3 start = floorMap.FloorToLocal(segment.start);
            Vector3 end = floorMap.FloorToLocal(segment.end);

            Vector3 along = (end - start).normalized;
            Vector3 across = Vector3.Cross(Vector3.up, along).normalized;

            start -= along * endCapOverlap;
            end += along * endCapOverlap;

            int baseIndex = vertices.Count;

            vertices.Add(start - (across * halfWidthMeters));
            vertices.Add(start + (across * halfWidthMeters));
            vertices.Add(end + (across * halfWidthMeters));
            vertices.Add(end - (across * halfWidthMeters));

            // Wound counter-clockwise viewed from above, so the surface normal points up and
            // NavMesh treats it as a floor rather than a ceiling.
            triangles.Add(baseIndex + 0);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);

            triangles.Add(baseIndex + 0);
            triangles.Add(baseIndex + 2);
            triangles.Add(baseIndex + 3);
        }

        private void AppendJunctionPatch(
            Vector2 junctionFloorPoint,
            List<Vector3> vertices,
            List<int> triangles)
        {
            float halfSize = 0f;

            foreach (HallwaySegment segment in floorMap.hallways)
            {
                if (segment == null || !segment.IsValid)
                {
                    continue;
                }

                bool touches =
                    Vector2.Distance(segment.start, junctionFloorPoint) < 0.35f ||
                    Vector2.Distance(segment.end, junctionFloorPoint) < 0.35f;

                if (touches)
                {
                    halfSize = Mathf.Max(halfSize, floorMap.ToMeters(segment.width) * 0.5f);
                }
            }

            if (halfSize <= 0f)
            {
                return;
            }

            halfSize = Mathf.Max(halfSize - floorMap.wallClearance, 0.15f);

            Vector3 center = floorMap.FloorToLocal(junctionFloorPoint);
            int baseIndex = vertices.Count;

            vertices.Add(center + new Vector3(-halfSize, 0f, -halfSize));
            vertices.Add(center + new Vector3(-halfSize, 0f, halfSize));
            vertices.Add(center + new Vector3(halfSize, 0f, halfSize));
            vertices.Add(center + new Vector3(halfSize, 0f, -halfSize));

            triangles.Add(baseIndex + 0);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);

            triangles.Add(baseIndex + 0);
            triangles.Add(baseIndex + 2);
            triangles.Add(baseIndex + 3);
        }

        /// <summary>
        /// Every point where two or more corridors meet. These are your turns — the places the
        /// visitor will be told to turn left or right, and the places the mesh needs filling in.
        /// </summary>
        private List<Vector2> FindJunctions()
        {
            var junctions = new List<Vector2>();
            var endpoints = new List<Vector2>();

            foreach (HallwaySegment segment in floorMap.hallways)
            {
                if (segment == null || !segment.IsValid)
                {
                    continue;
                }

                endpoints.Add(segment.start);
                endpoints.Add(segment.end);
            }

            for (int i = 0; i < endpoints.Count; i++)
            {
                bool shared = false;

                for (int j = 0; j < endpoints.Count; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }

                    // Endpoints from the SAME segment do not make a junction, so skip the pair
                    // (2k, 2k+1) that came from one corridor.
                    if (i / 2 == j / 2)
                    {
                        continue;
                    }

                    if (Vector2.Distance(endpoints[i], endpoints[j]) < 0.35f)
                    {
                        shared = true;
                        break;
                    }
                }

                if (!shared)
                {
                    continue;
                }

                bool alreadyRecorded = false;

                foreach (Vector2 existing in junctions)
                {
                    if (Vector2.Distance(existing, endpoints[i]) < 0.35f)
                    {
                        alreadyRecorded = true;
                        break;
                    }
                }

                if (!alreadyRecorded)
                {
                    junctions.Add(endpoints[i]);
                }
            }

            return junctions;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (floorMap == null)
            {
                return;
            }

            Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.6f);

            foreach (HallwaySegment segment in floorMap.hallways)
            {
                if (segment == null || !segment.IsValid)
                {
                    continue;
                }

                Vector3 start = transform.TransformPoint(floorMap.FloorToLocal(segment.start));
                Vector3 end = transform.TransformPoint(floorMap.FloorToLocal(segment.end));
                Gizmos.DrawLine(start, end);
            }
        }
#endif
    }
}

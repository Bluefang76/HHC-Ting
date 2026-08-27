using System.Collections.Generic;
using UnityEngine;
using Wayfinding.Navigation;

namespace Wayfinding.AR
{
    /// <summary>
    /// Draws the racing line: a ribbon of geometry laid along the route, sitting on the real
    /// floor, seen through the camera.
    ///
    /// A note on why this builds a mesh rather than using LineRenderer, since LineRenderer is the
    /// obvious first choice. LineRenderer billboards — it always turns to face the camera. That is
    /// exactly right for a laser beam and exactly wrong for a line painted on the ground: as the
    /// visitor tilts the phone, a billboarded line lifts off the floor and stands up toward them,
    /// and the illusion of paint on concrete is gone. A flat ribbon lying in the floor plane stays
    /// on the floor from every angle, which is the entire effect being sold here.
    ///
    /// The other detail that matters more than it sounds: the line is TRIMMED to start a couple of
    /// metres ahead of the visitor rather than at their feet. Positioning is good to a metre or
    /// two, so a line drawn all the way to where the app thinks their feet are will visibly not
    /// start at their feet, and every error in the system becomes something they can see. Starting
    /// it ahead of them hides that error behind the phone and reads as intentional design.
    ///
    /// This component should be a CHILD of floorRoot, so that when ARWorldAligner nudges the
    /// alignment the line moves with it and stays stuck to the building.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class ARPathRenderer : MonoBehaviour
    {
        [Header("Source")]
        public NavigationSession navigationSession;

        [Tooltip("The AR camera. Used to trim the ribbon behind the visitor and to fade its far end.")]
        public Transform arCamera;

        [Header("Ribbon shape")]
        [Tooltip("Width of the line in metres. 0.35 to 0.5 reads clearly on a hospital floor " +
                 "without looking like a carpet.")]
        [Range(0.1f, 1.5f)]
        public float ribbonWidth = 0.4f;

        [Tooltip("Height above the floor in metres. A centimetre or two prevents z-fighting with " +
                 "the detected plane; more than about 5 cm and the line visibly hovers.")]
        [Range(0f, 0.15f)]
        public float heightOffset = 0.02f;

        [Header("Trimming")]
        [Tooltip("Start the ribbon this far ahead of the visitor, in metres. See the class notes " +
                 "- this is what hides positioning error rather than showcasing it.")]
        [Range(0f, 5f)]
        public float trimAheadMeters = 2f;

        [Tooltip("Draw at most this far ahead, in metres. A line running the length of the whole " +
                 "corridor is visual noise; the next stretch is what the visitor needs.")]
        [Range(5f, 60f)]
        public float visibleDistanceMeters = 22f;

        [Tooltip("Length of the fade at each end, in metres. Without it the ribbon ends in a hard " +
                 "edge floating in mid-corridor, which looks like a bug.")]
        [Range(0.5f, 6f)]
        public float fadeLengthMeters = 3f;

        [Header("Animation")]
        [Tooltip("Texture scroll speed along the ribbon. A gentle flow toward the destination " +
                 "communicates direction without an arrow. Negative scrolls the other way.")]
        public float scrollSpeed = 0.35f;

        [Tooltip("Shader property to scroll. Works with any material whose main texture tiles " +
                 "along U. Leave as _MainTex for Built-in, _BaseMap for URP.")]
        public string scrollTextureProperty = "_BaseMap";

        [Header("Destination marker")]
        [Tooltip("Optional prefab dropped at the destination — a floating pin, a ring on the floor. " +
                 "Instantiated once and moved, not respawned.")]
        public GameObject destinationMarkerPrefab;

        [Tooltip("Height above the floor for the marker, in metres.")]
        public float markerHeight = 1.2f;

        [Header("Rebuild policy")]
        [Tooltip("Rebuild the ribbon when the visitor has moved this far, in metres. Rebuilding " +
                 "every frame is wasted work; the trim only needs to keep up with walking.")]
        [Range(0.05f, 2f)]
        public float rebuildMoveThreshold = 0.25f;

        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private Mesh _mesh;
        private GameObject _marker;
        private MaterialPropertyBlock _propertyBlock;

        private readonly List<Vector3> _path = new List<Vector3>();
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Vector2> _uvs = new List<Vector2>();
        private readonly List<Color> _colors = new List<Color>();
        private readonly List<int> _triangles = new List<int>();

        private Vector3 _lastBuildCameraPosition;
        private bool _hasPath;
        private float _scrollOffset;
        private int _scrollPropertyId;

        private void Awake()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();
            _propertyBlock = new MaterialPropertyBlock();
            _scrollPropertyId = Shader.PropertyToID(scrollTextureProperty);

            _mesh = new Mesh { name = "RacingLine" };
            _mesh.MarkDynamic(); // Rebuilt often; tells Unity to keep it in fast-update memory.
            _meshFilter.sharedMesh = _mesh;

            _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;
            _meshRenderer.enabled = false;
        }

        private void OnEnable()
        {
            if (navigationSession != null)
            {
                navigationSession.PathChanged += OnPathChanged;
                navigationSession.StateChanged += OnNavigationStateChanged;
            }
        }

        private void OnDisable()
        {
            if (navigationSession != null)
            {
                navigationSession.PathChanged -= OnPathChanged;
                navigationSession.StateChanged -= OnNavigationStateChanged;
            }
        }

        private void OnDestroy()
        {
            if (_mesh != null)
            {
                Destroy(_mesh);
            }
        }

        private void Update()
        {
            if (!_hasPath || arCamera == null)
            {
                return;
            }

            AnimateScroll();

            if (Vector3.Distance(arCamera.position, _lastBuildCameraPosition) >= rebuildMoveThreshold)
            {
                RebuildRibbon();
            }
        }

        // ------------------------------------------------------------------
        // Path events
        // ------------------------------------------------------------------

        private void OnPathChanged(IReadOnlyList<Vector3> worldPoints)
        {
            _path.Clear();

            if (worldPoints != null)
            {
                _path.AddRange(worldPoints);
            }

            _hasPath = _path.Count >= 2;
            _meshRenderer.enabled = _hasPath;

            if (!_hasPath)
            {
                _mesh.Clear();
                SetMarkerActive(false);
                return;
            }

            UpdateMarker(_path[_path.Count - 1]);
            RebuildRibbon();
        }

        private void OnNavigationStateChanged(NavigationState state)
        {
            bool visible = state == NavigationState.Guiding || state == NavigationState.OffRoute;

            _meshRenderer.enabled = visible && _hasPath;
            SetMarkerActive(visible && _hasPath);
        }

        // ------------------------------------------------------------------
        // Mesh construction
        // ------------------------------------------------------------------

        /// <summary>
        /// Rebuilds the ribbon: find where the visitor is on the path, trim to the visible window,
        /// then extrude a flat strip along it with per-vertex alpha for the fades.
        /// </summary>
        private void RebuildRibbon()
        {
            _lastBuildCameraPosition = arCamera.position;

            _vertices.Clear();
            _uvs.Clear();
            _colors.Clear();
            _triangles.Clear();
            _mesh.Clear();

            if (_path.Count < 2)
            {
                return;
            }

            int startIndex = PathfindingEngine.NearestSegmentIndex(
                _path, arCamera.position, out Vector3 projected);

            // Walk forward from the visitor's position, collecting the stretch to draw.
            var window = new List<Vector3>();
            float distanceFromVisitor = 0f;
            Vector3 cursor = projected;

            for (int i = startIndex + 1; i < _path.Count; i++)
            {
                Vector3 next = _path[i];
                float step = Vector3.Distance(cursor, next);

                if (distanceFromVisitor + step > trimAheadMeters && window.Count == 0)
                {
                    // Enter the window partway along this segment, exactly trimAheadMeters out.
                    float t = (trimAheadMeters - distanceFromVisitor) / Mathf.Max(step, 0.0001f);
                    window.Add(Vector3.Lerp(cursor, next, Mathf.Clamp01(t)));
                }

                if (window.Count > 0)
                {
                    window.Add(next);
                }

                distanceFromVisitor += step;
                cursor = next;

                if (distanceFromVisitor >= trimAheadMeters + visibleDistanceMeters)
                {
                    break;
                }
            }

            // The whole remaining path is inside the trim distance — the visitor is nearly there.
            // Draw what is left rather than nothing.
            if (window.Count < 2)
            {
                window.Clear();
                window.Add(projected);

                for (int i = startIndex + 1; i < _path.Count; i++)
                {
                    window.Add(_path[i]);
                }
            }

            if (window.Count < 2)
            {
                return;
            }

            BuildStrip(window);

            _mesh.SetVertices(_vertices);
            _mesh.SetUVs(0, _uvs);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_triangles, 0);
            _mesh.RecalculateBounds();
        }

        private void BuildStrip(List<Vector3> window)
        {
            float totalLength = 0f;

            for (int i = 1; i < window.Count; i++)
            {
                totalLength += Vector3.Distance(window[i - 1], window[i]);
            }

            if (totalLength < 0.05f)
            {
                return;
            }

            float halfWidth = ribbonWidth * 0.5f;
            float travelled = 0f;

            for (int i = 0; i < window.Count; i++)
            {
                if (i > 0)
                {
                    travelled += Vector3.Distance(window[i - 1], window[i]);
                }

                // Direction of the ribbon here: averaged across the joint so corners mitre
                // instead of producing a pinched notch on the inside of the turn.
                Vector3 forward = Vector3.zero;

                if (i > 0)
                {
                    forward += (window[i] - window[i - 1]).normalized;
                }

                if (i < window.Count - 1)
                {
                    forward += (window[i + 1] - window[i]).normalized;
                }

                forward.y = 0f;

                if (forward.sqrMagnitude < 0.0001f)
                {
                    forward = Vector3.forward;
                }

                forward.Normalize();

                Vector3 across = Vector3.Cross(Vector3.up, forward).normalized;

                Vector3 center = window[i];
                center.y += heightOffset;

                Vector3 left = transform.InverseTransformPoint(center - (across * halfWidth));
                Vector3 right = transform.InverseTransformPoint(center + (across * halfWidth));

                _vertices.Add(left);
                _vertices.Add(right);

                // V goes across the ribbon, U runs along it in metres so the texture tiles at a
                // real-world scale regardless of how long the path happens to be.
                _uvs.Add(new Vector2(travelled, 0f));
                _uvs.Add(new Vector2(travelled, 1f));

                float alpha = ComputeFadeAlpha(travelled, totalLength);
                var color = new Color(1f, 1f, 1f, alpha);
                _colors.Add(color);
                _colors.Add(color);

                if (i > 0)
                {
                    int baseIndex = (i - 1) * 2;

                    _triangles.Add(baseIndex + 0);
                    _triangles.Add(baseIndex + 1);
                    _triangles.Add(baseIndex + 2);

                    _triangles.Add(baseIndex + 1);
                    _triangles.Add(baseIndex + 3);
                    _triangles.Add(baseIndex + 2);
                }
            }
        }

        /// <summary>Fades both ends so the ribbon appears and disappears instead of being cut off.</summary>
        private float ComputeFadeAlpha(float distanceAlong, float totalLength)
        {
            float fade = Mathf.Min(fadeLengthMeters, totalLength * 0.4f);

            if (fade <= 0.01f)
            {
                return 1f;
            }

            float fadeIn = Mathf.Clamp01(distanceAlong / fade);
            float fadeOut = Mathf.Clamp01((totalLength - distanceAlong) / fade);

            return Mathf.Min(fadeIn, fadeOut);
        }

        // ------------------------------------------------------------------
        // Presentation
        // ------------------------------------------------------------------

        private void AnimateScroll()
        {
            if (_meshRenderer.sharedMaterial == null || !_meshRenderer.enabled)
            {
                return;
            }

            if (!_meshRenderer.sharedMaterial.HasProperty(_scrollPropertyId))
            {
                return;
            }

            _scrollOffset -= scrollSpeed * Time.deltaTime;
            _scrollOffset %= 1f;

            // A property block avoids creating a material instance per renderer, which would
            // break batching and leak a material every time this component is enabled.
            _meshRenderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetVector(_scrollPropertyId,
                new Vector4(1f, 1f, _scrollOffset, 0f));
            _meshRenderer.SetPropertyBlock(_propertyBlock);
        }

        private void UpdateMarker(Vector3 destinationWorld)
        {
            if (destinationMarkerPrefab == null)
            {
                return;
            }

            if (_marker == null)
            {
                _marker = Instantiate(destinationMarkerPrefab, transform);
            }

            Vector3 position = destinationWorld;
            position.y += markerHeight;
            _marker.transform.position = position;
            _marker.SetActive(true);
        }

        private void SetMarkerActive(bool active)
        {
            if (_marker != null)
            {
                _marker.SetActive(active);
            }
        }

        /// <summary>
        /// Applies a material at runtime. Use an UNLIT, transparent, vertex-colour-aware shader —
        /// "Universal Render Pipeline/Unlit" with Surface Type set to Transparent and vertex
        /// colour enabled, or a small custom shader. Unlit matters: a lit material picks up the
        /// scene's virtual lighting, which has nothing to do with the hospital's actual fluorescent
        /// tubes, and the mismatch is what makes AR overlays read as pasted on.
        /// </summary>
        public void SetMaterial(Material material)
        {
            _meshRenderer.sharedMaterial = material;
        }
    }
}

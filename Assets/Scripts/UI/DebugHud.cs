using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Wayfinding.AR;
using Wayfinding.Data;
using Wayfinding.Navigation;
using Wayfinding.Positioning;

namespace Wayfinding.UI
{
    /// <summary>
    /// An on-device diagnostic overlay. Deliberately built with IMGUI so it needs no prefab, no
    /// canvas and no wiring — drop the component on any GameObject in the scene and it works.
    ///
    /// You will spend more time looking at this than at the actual app. Standing in a corridor
    /// wondering why the line is pointing at a wall, the questions are always the same: which
    /// beacons can I hear, what distance does each one think it is, how good is the fix, and how
    /// far apart are AR and the beacons right now. All four are on this screen.
    ///
    /// Turn it off before anyone from hospital leadership sees the app. It is a mechanic's view.
    /// </summary>
    public class DebugHud : MonoBehaviour
    {
        [Header("Dependencies")]
        public BeaconManager beaconManager;
        public NavigationSession navigationSession;
        public ARWorldAligner worldAligner;
        public FloorMap floorMap;

        [Tooltip("Optional. When present, the HUD shows the true simulated position and your " +
                 "actual positioning error in metres — the single most useful number you can " +
                 "have while tuning the filters.")]
        public MockBeaconScanner mockScanner;

        [Header("Display")]
        [Tooltip("Show the overlay. Bind this to a hidden gesture in a real build — three-finger " +
                 "tap and hold is conventional and no visitor will hit it by accident.")]
        public bool visible = true;

        [Tooltip("Font size. Phone screens are dense; 22 to 28 reads well at arm's length.")]
        public int fontSize = 22;

        [Tooltip("Width of the panel as a fraction of screen width.")]
        [Range(0.3f, 1f)]
        public float panelWidthFraction = 0.55f;

        [Tooltip("Show the per-beacon table. Turn off for a compact summary when you only care " +
                 "about the fix.")]
        public bool showBeaconTable = true;

        [Header("Mini-map")]
        [Tooltip("Draw a top-down mini-map with beacons, the fix, and the path. Worth having: " +
                 "most positioning bugs are obvious in plan view and invisible in a number.")]
        public bool showMiniMap = true;

        [Range(0.15f, 0.6f)]
        public float miniMapHeightFraction = 0.3f;

        [Header("Manual alignment (debug)")]
        [Tooltip("DEPARTURE FROM THE DESIGN OF RECORD. Shows a panel that sets AR world alignment " +
                 "from typed map coordinates and a heading instead of a QR scan, so the AR layer " +
                 "can be tested before a QR code exists or before you can reach it. Uses the " +
                 "worldAligner reference above. Off by default and never wired to anything a " +
                 "visitor can reach.")]
        public bool showManualAlignment;

        private readonly List<BeaconManager.BeaconDiagnostic> _diagnostics =
            new List<BeaconManager.BeaconDiagnostic>();

        private readonly StringBuilder _builder = new StringBuilder(1024);
        private GUIStyle _labelStyle;
        private GUIStyle _panelStyle;
        private Texture2D _panelTexture;
        private Texture2D _dotTexture;

        // Manual alignment panel (debug only — see showManualAlignment above).
        private string _manualX = "0";
        private string _manualY = "0";
        private string _manualHeading = "0";

        private void Awake()
        {
            _panelTexture = MakeTexture(new Color(0f, 0f, 0f, 0.72f));
            _dotTexture = MakeTexture(Color.white);
        }

        private void OnDestroy()
        {
            if (_panelTexture != null)
            {
                Destroy(_panelTexture);
            }

            if (_dotTexture != null)
            {
                Destroy(_dotTexture);
            }
        }

        private void OnGUI()
        {
            if (!visible)
            {
                return;
            }

            EnsureStyles();

            float width = UnityEngine.Screen.width * panelWidthFraction;
            float x = 10f;
            float y = 10f;

            string text = BuildReport();
            float height = _labelStyle.CalcHeight(new GUIContent(text), width - 20f) + 20f;

            GUI.Box(new Rect(x, y, width, height), GUIContent.none, _panelStyle);
            GUI.Label(new Rect(x + 10f, y + 10f, width - 20f, height - 20f), text, _labelStyle);

            if (showMiniMap && floorMap != null)
            {
                DrawMiniMap(new Rect(
                    x,
                    y + height + 10f,
                    width,
                    UnityEngine.Screen.height * miniMapHeightFraction));
            }

            if (showManualAlignment && worldAligner != null)
            {
                DrawManualAlignmentPanel(new Rect(
                    UnityEngine.Screen.width - (width * 0.6f) - 10f,
                    10f,
                    width * 0.6f,
                    170f));
            }
        }

        /// <summary>
        /// DEPARTURE FROM THE DESIGN OF RECORD — see showManualAlignment. Sets AR alignment from
        /// typed map coordinates and a heading, the same way a QR scan would, without needing a
        /// code to scan. This is a testing aid, not a visitor-facing control — nothing outside
        /// DebugHud may call ARWorldAligner.AlignManual.
        /// </summary>
        private void DrawManualAlignmentPanel(Rect area)
        {
            GUI.Box(area, GUIContent.none, _panelStyle);

            GUILayout.BeginArea(new Rect(area.x + 10f, area.y + 10f, area.width - 20f, area.height - 20f));

            GUILayout.Label("MANUAL ALIGNMENT (debug)", _labelStyle);
            GUILayout.Label(
                worldAligner.IsAligned
                    ? $"Currently aligned. Drift {worldAligner.AlignmentErrorMeters:F2} m."
                    : "Not aligned yet.",
                _labelStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label("X", _labelStyle, GUILayout.Width(20f));
            _manualX = GUILayout.TextField(_manualX, _labelStyle, GUILayout.Width(70f));
            GUILayout.Label("Y", _labelStyle, GUILayout.Width(20f));
            _manualY = GUILayout.TextField(_manualY, _labelStyle, GUILayout.Width(70f));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Heading (deg, 0 = +X)", _labelStyle, GUILayout.Width(180f));
            _manualHeading = GUILayout.TextField(_manualHeading, _labelStyle, GUILayout.Width(70f));
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Align here, facing this way", _labelStyle,
                    GUILayout.Height(fontSize * 1.6f)))
            {
                bool ok = float.TryParse(_manualX, out float x) &&
                          float.TryParse(_manualY, out float y) &&
                          float.TryParse(_manualHeading, out float heading);

                if (ok)
                {
                    worldAligner.AlignManual(new Vector2(x, y), heading);
                }
                else
                {
                    Debug.LogWarning("[DebugHud] Manual alignment fields must all be numbers.");
                }
            }

            GUILayout.EndArea();
        }

        private string BuildReport()
        {
            _builder.Clear();

            // --- Position ---
            if (beaconManager == null)
            {
                _builder.AppendLine("No BeaconManager assigned.");
                return _builder.ToString();
            }

            _builder.AppendLine($"SCANNER  {beaconManager.ScannerStatus}");

            if (beaconManager.HasFix)
            {
                PositionFix fix = beaconManager.CurrentFix;

                _builder.AppendLine(
                    $"FIX      ({fix.FloorPosition.x:F1}, {fix.FloorPosition.y:F1})  " +
                    $"conf {fix.Confidence:F2}");
                _builder.AppendLine(
                    $"         residual {fix.ResidualMeters:F2} m from {fix.BeaconsUsed} beacons");

                if (mockScanner != null)
                {
                    float errorMeters = floorMap != null
                        ? floorMap.ToMeters(Vector2.Distance(
                            fix.FloorPosition, mockScanner.TrueFloorPosition))
                        : Vector2.Distance(fix.FloorPosition, mockScanner.TrueFloorPosition);

                    _builder.AppendLine(
                        $"TRUE     ({mockScanner.TrueFloorPosition.x:F1}, " +
                        $"{mockScanner.TrueFloorPosition.y:F1})   ERROR {errorMeters:F2} m");
                }
            }
            else
            {
                _builder.AppendLine("FIX      none - need 3 beacons in range");
            }

            // --- AR alignment ---
            if (worldAligner != null)
            {
                _builder.AppendLine(
                    $"AR       {(worldAligner.IsAligned ? "aligned" : "NOT ALIGNED")}  " +
                    $"drift {worldAligner.AlignmentErrorMeters:F2} m  " +
                    $"floor y {worldAligner.FloorHeight:F2}");
            }

            // --- Navigation ---
            if (navigationSession != null)
            {
                _builder.Append($"NAV      {navigationSession.State}");

                if (navigationSession.Destination != null)
                {
                    _builder.Append($" -> {navigationSession.Destination.roomNumber}");
                }

                _builder.AppendLine();
                _builder.AppendLine($"         path points {navigationSession.CurrentPath.Count}");
            }

            // --- Beacons ---
            if (showBeaconTable)
            {
                beaconManager.GetDiagnostics(_diagnostics);

                _builder.AppendLine();
                _builder.AppendLine("BEACON           RSSI   DIST    W   BATT");

                int lowBatteryCount = 0;

                foreach (BeaconManager.BeaconDiagnostic diagnostic in _diagnostics)
                {
                    string name = diagnostic.Beacon.DisplayName;

                    if (name.Length > 14)
                    {
                        name = name.Substring(name.Length - 14);
                    }

                    // Battery comes from the beacon's own Eddystone-TLM frame. Blank until it
                    // has sent one — they interleave telemetry roughly every tenth advertisement,
                    // so give it a few seconds before assuming a unit is not reporting.
                    string battery = "   -";

                    if (diagnostic.BatteryMillivolts > 0)
                    {
                        battery = $"{diagnostic.BatteryMillivolts / 1000f,4:F2}";

                        if (diagnostic.BatteryMillivolts < 2500)
                        {
                            battery += "!";
                            lowBatteryCount++;
                        }
                    }

                    if (!diagnostic.Fresh)
                    {
                        _builder.AppendLine($"{name,-14}    --      --    -  {battery}");
                        continue;
                    }

                    _builder.AppendLine(
                        $"{name,-14} {diagnostic.FilteredRssi,6:F0} {diagnostic.DistanceMeters,6:F1} " +
                        $"{diagnostic.Weight,5:F2}  {battery}");
                }

                if (lowBatteryCount > 0)
                {
                    _builder.AppendLine($"** {lowBatteryCount} beacon(s) below 2.50 V - replace **");
                }
            }

            return _builder.ToString();
        }

        /// <summary>
        /// Top-down plan view. Beacons as small squares, the fix as a large one, the path as a
        /// dotted line. Crude, and worth ten times its size in debugging: a position mirrored
        /// across the corridor is instantly obvious here and completely invisible in a coordinate.
        /// </summary>
        private void DrawMiniMap(Rect area)
        {
            GUI.Box(area, GUIContent.none, _panelStyle);

            Rect bounds = floorMap.FloorBounds;

            if (bounds.width < 0.01f || bounds.height < 0.01f)
            {
                return;
            }

            // Fit the floor into the panel, preserving aspect, with a small margin.
            float padding = 12f;
            Rect inner = new Rect(
                area.x + padding, area.y + padding,
                area.width - (padding * 2f), area.height - (padding * 2f));

            float scale = Mathf.Min(inner.width / bounds.width, inner.height / bounds.height);

            Vector2 ToScreen(Vector2 floorPoint)
            {
                float px = inner.x + ((floorPoint.x - bounds.xMin) * scale);
                // Floor +Y goes up the map, screen +Y goes down, hence the flip.
                float py = inner.yMax - ((floorPoint.y - bounds.yMin) * scale);
                return new Vector2(px, py);
            }

            // Hallways.
            GUI.color = new Color(0.4f, 0.6f, 0.9f, 0.5f);

            foreach (HallwaySegment segment in floorMap.hallways)
            {
                if (segment == null || !segment.IsValid)
                {
                    continue;
                }

                DrawLine(ToScreen(segment.start), ToScreen(segment.end), 2f);
            }

            // Beacons: green when contributing, grey when not heard.
            beaconManager?.GetDiagnostics(_diagnostics);

            foreach (BeaconManager.BeaconDiagnostic diagnostic in _diagnostics)
            {
                GUI.color = diagnostic.Fresh
                    ? new Color(0.3f, 0.9f, 0.4f, 0.9f)
                    : new Color(0.5f, 0.5f, 0.5f, 0.5f);

                Vector2 point = ToScreen(diagnostic.Beacon.position);
                GUI.DrawTexture(new Rect(point.x - 3f, point.y - 3f, 6f, 6f), _dotTexture);
            }

            // Ground truth, when simulating.
            if (mockScanner != null)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.6f);
                Vector2 truePoint = ToScreen(mockScanner.TrueFloorPosition);
                GUI.DrawTexture(new Rect(truePoint.x - 4f, truePoint.y - 4f, 8f, 8f), _dotTexture);
            }

            // The fix.
            if (beaconManager != null && beaconManager.HasFix)
            {
                GUI.color = new Color(1f, 0.55f, 0.1f, 1f);
                Vector2 fixPoint = ToScreen(beaconManager.CurrentFix.FloorPosition);
                GUI.DrawTexture(new Rect(fixPoint.x - 5f, fixPoint.y - 5f, 10f, 10f), _dotTexture);
            }

            GUI.color = Color.white;
        }

        /// <summary>Draws a line between two screen points by rotating a 1x1 texture.</summary>
        private void DrawLine(Vector2 from, Vector2 to, float thickness)
        {
            Vector2 delta = to - from;
            float length = delta.magnitude;

            if (length < 0.01f)
            {
                return;
            }

            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;

            Matrix4x4 saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, from);
            GUI.DrawTexture(new Rect(from.x, from.y - (thickness * 0.5f), length, thickness), _dotTexture);
            GUI.matrix = saved;
        }

        private void EnsureStyles()
        {
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = fontSize,
                    richText = false,
                    wordWrap = false,
                    normal = { textColor = Color.white },
                    font = Font.CreateDynamicFontFromOSFont("Courier New", fontSize)
                };
            }

            _labelStyle.fontSize = fontSize;

            if (_panelStyle == null)
            {
                _panelStyle = new GUIStyle(GUI.skin.box)
                {
                    normal = { background = _panelTexture }
                };
            }
        }

        private static Texture2D MakeTexture(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }
    }
}

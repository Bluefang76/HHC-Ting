using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Wayfinding.Data;

namespace Wayfinding.EditorTools
{
    /// <summary>
    /// Custom inspector for FloorMap: draws the survey to scale, validates it, and provides the
    /// three cleanup operations you will otherwise do by hand a hundred times.
    ///
    /// The reason this is worth having: a floor survey is a few hundred numbers typed from paced
    /// measurements, and a typo in any one of them produces a symptom — a room that cannot be
    /// routed to, a corner NavMesh will not cross — that shows up much later and looks like a
    /// code bug. Seeing the plan view redraw as you type catches those in seconds. A transposed
    /// digit is obvious as a corridor pointing into the car park; it is invisible in a list.
    /// </summary>
    [CustomEditor(typeof(FloorMap))]
    public class FloorMapEditor : Editor
    {
        private const float PreviewHeight = 260f;

        private bool _showPreview = true;
        private bool _showValidation = true;
        private bool _showTools;

        private Texture2D _pixel;

        private void OnEnable()
        {
            _pixel = new Texture2D(1, 1);
            _pixel.SetPixel(0, 0, Color.white);
            _pixel.Apply();
        }

        private void OnDisable()
        {
            if (_pixel != null)
            {
                DestroyImmediate(_pixel);
            }
        }

        public override void OnInspectorGUI()
        {
            var map = (FloorMap)target;

            DrawValidationSection(map);
            DrawPreviewSection(map);
            DrawToolsSection(map);

            EditorGUILayout.Space();
            DrawDefaultInspector();
        }

        // ------------------------------------------------------------------
        // Validation
        // ------------------------------------------------------------------

        private void DrawValidationSection(FloorMap map)
        {
            _showValidation = EditorGUILayout.Foldout(_showValidation, "Validation", true);

            if (!_showValidation)
            {
                return;
            }

            List<string> problems = map.Validate();

            if (problems.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    $"Survey looks consistent. {map.hallways.Count} corridor(s), " +
                    $"{map.rooms.Count} room(s), {map.beacons.Count} beacon(s).",
                    MessageType.Info);
                return;
            }

            foreach (string problem in problems)
            {
                EditorGUILayout.HelpBox(problem, MessageType.Warning);
            }
        }

        // ------------------------------------------------------------------
        // Plan view
        // ------------------------------------------------------------------

        private void DrawPreviewSection(FloorMap map)
        {
            _showPreview = EditorGUILayout.Foldout(_showPreview, "Plan view", true);

            if (!_showPreview)
            {
                return;
            }

            Rect area = GUILayoutUtility.GetRect(0f, PreviewHeight, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(area, new Color(0.13f, 0.13f, 0.15f));

            Rect bounds = map.FloorBounds;

            if (bounds.width < 0.01f && bounds.height < 0.01f)
            {
                EditorGUI.LabelField(area, "Nothing surveyed yet.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            const float padding = 18f;
            var inner = new Rect(
                area.x + padding, area.y + padding,
                area.width - (padding * 2f), area.height - (padding * 2f));

            float scale = Mathf.Min(
                inner.width / Mathf.Max(bounds.width, 0.01f),
                inner.height / Mathf.Max(bounds.height, 0.01f));

            Vector2 ToScreen(Vector2 floorPoint)
            {
                return new Vector2(
                    inner.x + ((floorPoint.x - bounds.xMin) * scale),
                    inner.yMax - ((floorPoint.y - bounds.yMin) * scale));
            }

            Handles.BeginGUI();

            // Corridors, drawn at their true width so you can see where they are too narrow.
            foreach (HallwaySegment segment in map.hallways)
            {
                if (segment == null || !segment.IsValid)
                {
                    continue;
                }

                Handles.color = new Color(0.35f, 0.55f, 0.85f, 0.35f);
                Handles.DrawAAPolyLine(
                    Mathf.Max(segment.width * scale, 2f),
                    ToScreen(segment.start),
                    ToScreen(segment.end));

                Handles.color = new Color(0.6f, 0.8f, 1f, 0.9f);
                Handles.DrawAAPolyLine(1.5f, ToScreen(segment.start), ToScreen(segment.end));
            }

            // Beacons.
            foreach (BeaconDefinition beacon in map.beacons)
            {
                if (beacon == null)
                {
                    continue;
                }

                Vector2 point = ToScreen(beacon.position);
                Color color = beacon.enabled
                    ? new Color(0.3f, 0.9f, 0.45f)
                    : new Color(0.5f, 0.5f, 0.5f);

                EditorGUI.DrawRect(new Rect(point.x - 3f, point.y - 3f, 6f, 6f), color);
            }

            // Rooms: door in amber, approach point in white, a line joining them.
            foreach (RoomNode room in map.rooms)
            {
                if (room == null)
                {
                    continue;
                }

                Vector2 door = ToScreen(room.doorPosition);
                Vector2 approach = ToScreen(room.approachPosition);

                Handles.color = new Color(1f, 0.75f, 0.2f, 0.5f);
                Handles.DrawAAPolyLine(1f, door, approach);

                EditorGUI.DrawRect(new Rect(door.x - 2.5f, door.y - 2.5f, 5f, 5f),
                    new Color(1f, 0.75f, 0.2f));
                EditorGUI.DrawRect(new Rect(approach.x - 2f, approach.y - 2f, 4f, 4f),
                    new Color(0.9f, 0.9f, 0.9f, 0.8f));
            }

            // QR anchors, with a stalk showing which way the visitor faces when scanning.
            foreach (FloorMap.QrAnchor anchor in map.qrAnchors)
            {
                if (anchor == null)
                {
                    continue;
                }

                Vector2 point = ToScreen(anchor.position);
                EditorGUI.DrawRect(new Rect(point.x - 4f, point.y - 4f, 8f, 8f),
                    new Color(0.9f, 0.35f, 0.9f));

                float radians = anchor.headingDegrees * Mathf.Deg2Rad;
                var direction = new Vector2(Mathf.Cos(radians), -Mathf.Sin(radians));

                Handles.color = new Color(0.9f, 0.35f, 0.9f, 0.9f);
                Handles.DrawAAPolyLine(2f, point, point + (direction * 22f));
            }

            Handles.EndGUI();

            EditorGUI.LabelField(
                new Rect(area.x + 6f, area.yMax - 18f, area.width - 12f, 16f),
                $"{bounds.width:F1} x {bounds.height:F1} survey units   " +
                "green = beacon,  amber = door,  magenta = QR + facing",
                EditorStyles.miniLabel);
        }

        // ------------------------------------------------------------------
        // Cleanup tools
        // ------------------------------------------------------------------

        private void DrawToolsSection(FloorMap map)
        {
            _showTools = EditorGUILayout.Foldout(_showTools, "Survey tools", true);

            if (!_showTools)
            {
                return;
            }

            EditorGUILayout.HelpBox(
                "These edit the asset directly. They are undoable, but check the plan view after " +
                "running one.", MessageType.None);

            if (GUILayout.Button("Fill missing approach points from nearest hallway"))
            {
                FillApproachPoints(map);
            }

            if (GUILayout.Button("Weld hallway endpoints that are nearly touching"))
            {
                WeldEndpoints(map);
            }

            if (GUILayout.Button("Snap beacons onto the nearest hallway"))
            {
                SnapBeacons(map);
            }
        }

        /// <summary>
        /// For every room whose approach point is unset, drops a point on the nearest corridor
        /// centre line. Saves typing a second coordinate for all eighteen rooms — the approach
        /// point is almost always "straight out of the door into the middle of the corridor".
        /// </summary>
        private void FillApproachPoints(FloorMap map)
        {
            Undo.RecordObject(map, "Fill approach points");
            int filled = 0;

            foreach (RoomNode room in map.rooms)
            {
                if (room == null || room.approachPosition != Vector2.zero)
                {
                    continue;
                }

                HallwaySegment segment = map.NearestSegment(room.doorPosition);

                if (segment == null)
                {
                    continue;
                }

                room.approachPosition = segment.ClosestPointOnCenterLine(room.doorPosition);
                filled++;
            }

            EditorUtility.SetDirty(map);
            Debug.Log($"[FloorMapEditor] Filled {filled} approach point(s).");
        }

        /// <summary>
        /// Averages hallway endpoints that are close but not identical, so corridors that should
        /// meet actually do. Paced coordinates are never exact, and a 12 cm gap between two
        /// corridors is enough for NavMesh to treat the corner as a dead end — a bug that is
        /// genuinely painful to diagnose from the symptom, which is simply "no route found".
        /// </summary>
        private void WeldEndpoints(FloorMap map)
        {
            Undo.RecordObject(map, "Weld hallway endpoints");

            const float weldRadius = 0.6f;

            // Endpoints are tracked alongside the segment they came from rather than by index
            // arithmetic. A single null entry in the hallway list would otherwise desynchronise
            // an index-based scheme and weld the wrong corridors together.
            var points = new List<Vector2>();
            var owners = new List<HallwaySegment>();
            var isStart = new List<bool>();

            foreach (HallwaySegment segment in map.hallways)
            {
                if (segment == null)
                {
                    continue;
                }

                points.Add(segment.start);
                owners.Add(segment);
                isStart.Add(true);

                points.Add(segment.end);
                owners.Add(segment);
                isStart.Add(false);
            }

            var clusters = new List<List<int>>();
            var assigned = new bool[points.Count];

            for (int i = 0; i < points.Count; i++)
            {
                if (assigned[i])
                {
                    continue;
                }

                var cluster = new List<int> { i };
                assigned[i] = true;

                for (int j = i + 1; j < points.Count; j++)
                {
                    if (!assigned[j] && Vector2.Distance(points[i], points[j]) <= weldRadius)
                    {
                        cluster.Add(j);
                        assigned[j] = true;
                    }
                }

                if (cluster.Count > 1)
                {
                    clusters.Add(cluster);
                }
            }

            int welded = 0;

            foreach (List<int> cluster in clusters)
            {
                Vector2 average = Vector2.zero;

                foreach (int index in cluster)
                {
                    average += points[index];
                }

                average /= cluster.Count;

                foreach (int index in cluster)
                {
                    HallwaySegment segment = owners[index];

                    if (isStart[index])
                    {
                        segment.start = average;
                    }
                    else
                    {
                        segment.end = average;
                    }

                    welded++;
                }
            }

            EditorUtility.SetDirty(map);
            Debug.Log($"[FloorMapEditor] Welded {welded} endpoint(s) into {clusters.Count} junction(s).");
        }

        /// <summary>
        /// Pulls beacon positions onto the nearest corridor centre line. Only run this if your
        /// beacons are genuinely mounted along corridors — if any are inside rooms, it will move
        /// them somewhere wrong and quietly ruin your positioning.
        /// </summary>
        private void SnapBeacons(FloorMap map)
        {
            if (!EditorUtility.DisplayDialog(
                    "Snap beacons to hallways?",
                    "This moves every beacon onto the nearest corridor centre line. Only do this " +
                    "if all beacons are mounted in corridors. Beacons inside rooms will be moved " +
                    "to the wrong place.",
                    "Snap them", "Cancel"))
            {
                return;
            }

            Undo.RecordObject(map, "Snap beacons to hallways");
            int moved = 0;

            foreach (BeaconDefinition beacon in map.beacons)
            {
                if (beacon == null)
                {
                    continue;
                }

                HallwaySegment segment = map.NearestSegment(beacon.position);

                if (segment == null)
                {
                    continue;
                }

                Vector2 snapped = segment.ClosestPointOnCenterLine(beacon.position);

                if (Vector2.Distance(snapped, beacon.position) > 0.01f)
                {
                    beacon.position = snapped;
                    moved++;
                }
            }

            EditorUtility.SetDirty(map);
            Debug.Log($"[FloorMapEditor] Moved {moved} beacon(s).");
        }
    }
}

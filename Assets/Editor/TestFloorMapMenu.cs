using UnityEditor;
using UnityEngine;
using Wayfinding.Data;

namespace Wayfinding.EditorTools
{
    /// <summary>
    /// DEPARTURE FROM THE DESIGN OF RECORD — not in the 21-script manifest. Added so positioning
    /// can be exercised against a handful of real beacons on a desk before the real 30-beacon
    /// survey exists.
    ///
    /// Generates a tiny synthetic FloorMap: one straight test corridor, four beacons at the
    /// corners of a rectangle straddling it — deliberately NOT collinear, which is the good
    /// geometry case Trilateration.cs's class comment warns you to build toward — one room, and
    /// one QR anchor at the entrance.
    ///
    /// Every coordinate here is invented on purpose. This is a test fixture, not a survey — it
    /// describes no real place and must never be confused with the floor's real FloorMap asset.
    /// </summary>
    public static class TestFloorMapMenu
    {
        [MenuItem("Wayfinding/Create Test Floor Map (4-beacon square)")]
        public static void CreateTestFloorMap()
        {
            var map = ScriptableObject.CreateInstance<FloorMap>();

            map.floorName = "Test Floor (4-beacon square)";
            map.floorIndex = 0;
            map.unitsToMeters = 1f;
            map.wallClearance = 0.35f;

            map.hallways.Add(new HallwaySegment
            {
                label = "Test corridor",
                start = new Vector2(0f, 0f),
                end = new Vector2(10f, 0f),
                width = 2.4f
            });

            map.rooms.Add(new RoomNode
            {
                roomNumber = "TEST",
                displayName = "Test destination",
                doorPosition = new Vector2(10f, 1.2f),
                approachPosition = new Vector2(9f, 0f)
            });

            // A rectangle straddling the corridor, not a line down one side of it — see the
            // hallway-geometry warning in Trilateration.cs and build-sheet.md. Four beacons is the
            // minimum that still lets BeaconManager drop one and keep the required three.
            AddBeacon(map, "000000000001", "B1 - near/left", new Vector2(1f, 3f));
            AddBeacon(map, "000000000002", "B2 - near/right", new Vector2(1f, -3f));
            AddBeacon(map, "000000000003", "B3 - far/left", new Vector2(9f, 3f));
            AddBeacon(map, "000000000004", "B4 - far/right", new Vector2(9f, -3f));

            map.qrAnchors.Add(new FloorMap.QrAnchor
            {
                code = "TEST-ENTRANCE",
                position = new Vector2(0f, 0f),
                headingDegrees = 0f,
                label = "Test entrance"
            });

            string path = EditorUtility.SaveFilePanelInProject(
                "Save test FloorMap",
                "TestFloorMap",
                "asset",
                "Choose where to save the generated 4-beacon test FloorMap.");

            if (string.IsNullOrEmpty(path))
            {
                Object.DestroyImmediate(map);
                return;
            }

            AssetDatabase.CreateAsset(map, path);
            AssetDatabase.SaveAssets();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = map;

            Debug.Log($"[TestFloorMapMenu] Created a 4-beacon test FloorMap at '{path}'. Point " +
                      "MockBeaconScanner and BeaconManager at it to test positioning without your " +
                      "real survey, or use it as the four real beacons you already have.");
        }

        private static void AddBeacon(FloorMap map, string instanceId, string label, Vector2 position)
        {
            map.beacons.Add(new BeaconDefinition
            {
                instanceId = instanceId,
                label = label,
                position = position
            });
        }
    }
}

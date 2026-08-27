using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Wayfinding.Data;
using Wayfinding.Positioning;

namespace Wayfinding.Tools
{
    /// <summary>
    /// Measures each beacon's real signal strength at one metre and writes it back into the
    /// FloorMap.
    ///
    /// This is the highest-value hour you will spend on the whole pilot, and it is easy to skip
    /// because nothing visibly breaks without it. Here is why it matters. Distance comes out of
    ///
    ///     d = 10 ^ ((TxPower - RSSI) / (10 * n))
    ///
    /// With n around 2.6, a 3 dB error in TxPower moves the estimated distance by roughly 30%.
    /// At 10 m away that is 3 m of error injected into every solve, permanently, from a number
    /// you guessed. And beacons genuinely differ: same model, same box, same firmware, and
    /// they will still land several dB apart depending on battery level, orientation, and what
    /// they are stuck to. A beacon taped to a steel door frame is not the beacon it was on your
    /// desk.
    ///
    /// HOW TO USE IT
    ///   1. Mount all the beacons first. Calibrating them on a table then moving them makes the
    ///      measurement worthless.
    ///   2. Run the app with this component enabled.
    ///   3. Stand exactly 1 m from a beacon, phone at normal holding height, facing it, with your
    ///      body NOT between the phone and the beacon.
    ///   4. Select it in the list and hold Sample for a few seconds.
    ///   5. Repeat for all 30. It goes faster than it sounds — about a minute each.
    ///
    /// In the editor the results are written straight into the FloorMap asset. On device they are
    /// exported as JSON to the app's persistent data folder for you to paste in.
    /// </summary>
    public class BeaconSurveyTool : MonoBehaviour
    {
        [Header("Dependencies")]
        public FloorMap floorMap;
        public BeaconManager beaconManager;

        [Header("Sampling")]
        [Tooltip("Seconds of readings to average per sample. Longer is better: BLE at 1 m still " +
                 "swings several dB, and 5 seconds at 2 Hz is only ten packets.")]
        [Range(2f, 20f)]
        public float sampleDurationSeconds = 6f;

        [Tooltip("Minimum readings before a sample counts. Fewer than this and the beacon was " +
                 "probably not being heard properly.")]
        public int minimumSamples = 8;

        [Tooltip("Discard this fraction of the highest and lowest readings before averaging. " +
                 "Trims the reflections and the momentary blocks.")]
        [Range(0f, 0.4f)]
        public float trimFraction = 0.15f;

        [Header("UI")]
        [Tooltip("Show the survey overlay. IMGUI, so no prefab wiring — enable and it appears.")]
        public bool showOverlay = true;

        public int fontSize = 24;

        /// <summary>Fires when a beacon has been successfully calibrated.</summary>
        public event Action<BeaconDefinition, float> BeaconCalibrated;

        private readonly List<float> _samples = new List<float>();
        private readonly Dictionary<string, float> _results = new Dictionary<string, float>();

        private BeaconDefinition _selectedBeacon;
        private bool _sampling;
        private float _samplingProgress;
        private string _statusMessage = "Select a beacon, stand 1 m away, then Sample.";
        private Vector2 _scrollPosition;
        private GUIStyle _labelStyle;

        /// <summary>Begins a sample against the selected beacon.</summary>
        public void StartSample(BeaconDefinition beacon)
        {
            if (_sampling || beacon == null || beaconManager == null)
            {
                return;
            }

            _selectedBeacon = beacon;
            StartCoroutine(SampleRoutine(beacon));
        }

        private IEnumerator SampleRoutine(BeaconDefinition beacon)
        {
            _sampling = true;
            _samples.Clear();
            _samplingProgress = 0f;
            _statusMessage = $"Sampling {beacon.DisplayName}. Hold still, 1 m away.";

            RssiFilter filter = beaconManager.GetFilter(beacon);
            float startTime = Time.unscaledTime;
            float lastCapturedAt = -1f;

            while (Time.unscaledTime - startTime < sampleDurationSeconds)
            {
                _samplingProgress = (Time.unscaledTime - startTime) / sampleDurationSeconds;

                // Re-fetch: the filter does not exist until the beacon is first heard, so on a
                // beacon that has been quiet this starts null and appears mid-sample.
                filter ??= beaconManager.GetFilter(beacon);

                if (filter != null && filter.LastUpdateTime > lastCapturedAt)
                {
                    lastCapturedAt = filter.LastUpdateTime;

                    // Capture the RAW value, not the filtered one. The filters exist to smooth
                    // positioning; for calibration we want the honest distribution, trimmed
                    // ourselves, so the median window's lag does not bias the mean.
                    _samples.Add(filter.RawRssi);
                }

                yield return null;
            }

            _sampling = false;
            _samplingProgress = 0f;

            if (_samples.Count < minimumSamples)
            {
                _statusMessage = $"Only {_samples.Count} readings from {beacon.DisplayName}. " +
                                 "Move closer, check the battery, or make sure nothing is between " +
                                 "you and it.";
                yield break;
            }

            float measured = TrimmedMean(_samples, trimFraction);

            beacon.txPowerAtOneMeter = measured;
            _results[beacon.DisplayName] = measured;

            _statusMessage = $"{beacon.DisplayName}: {measured:F1} dBm at 1 m " +
                             $"({_samples.Count} readings). Saved.";

            BeaconCalibrated?.Invoke(beacon, measured);

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(floorMap);
            UnityEditor.AssetDatabase.SaveAssets();
#endif
            ExportResults();
        }

        /// <summary>
        /// Mean with the extreme readings discarded. A plain mean is badly skewed by the handful
        /// of readings where someone walked past, and a plain median throws away most of the data.
        /// </summary>
        public static float TrimmedMean(List<float> values, float trimFraction)
        {
            if (values == null || values.Count == 0)
            {
                return 0f;
            }

            var sorted = new List<float>(values);
            sorted.Sort();

            int trim = Mathf.FloorToInt(sorted.Count * Mathf.Clamp(trimFraction, 0f, 0.45f));
            int first = trim;
            int last = sorted.Count - trim;

            if (last - first < 1)
            {
                first = 0;
                last = sorted.Count;
            }

            float total = 0f;

            for (int i = first; i < last; i++)
            {
                total += sorted[i];
            }

            return total / (last - first);
        }

        /// <summary>
        /// Writes every calibrated value to JSON in the app's persistent data folder. On device
        /// this is how the numbers get off the phone — the path is logged, and the file is
        /// reachable through the platform's file sharing.
        /// </summary>
        public void ExportResults()
        {
            if (_results.Count == 0)
            {
                return;
            }

            var builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine($"  \"floor\": \"{floorMap?.floorName}\",");
            builder.AppendLine($"  \"surveyedUtc\": \"{DateTime.UtcNow:o}\",");
            builder.AppendLine("  \"beacons\": [");

            int index = 0;

            foreach (KeyValuePair<string, float> entry in _results)
            {
                string comma = index < _results.Count - 1 ? "," : "";
                builder.AppendLine(
                    $"    {{ \"beacon\": \"{entry.Key}\", \"txPowerAtOneMeter\": {entry.Value:F1} }}{comma}");
                index++;
            }

            builder.AppendLine("  ]");
            builder.AppendLine("}");

            string path = Path.Combine(Application.persistentDataPath, "beacon_survey.json");

            try
            {
                File.WriteAllText(path, builder.ToString());
                Debug.Log($"[BeaconSurveyTool] Survey written to {path}");
            }
            catch (Exception exception)
            {
                Debug.LogError($"[BeaconSurveyTool] Could not write survey: {exception.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Overlay
        // ------------------------------------------------------------------

        private void OnGUI()
        {
            if (!showOverlay || floorMap == null || beaconManager == null)
            {
                return;
            }

            _labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = fontSize, wordWrap = true };
            _labelStyle.fontSize = fontSize;

            float width = UnityEngine.Screen.width * 0.9f;
            float height = UnityEngine.Screen.height * 0.8f;
            var area = new Rect(
                (UnityEngine.Screen.width - width) * 0.5f,
                (UnityEngine.Screen.height - height) * 0.5f,
                width, height);

            GUILayout.BeginArea(area, GUI.skin.box);

            GUILayout.Label("BEACON SURVEY", _labelStyle);
            GUILayout.Label(_statusMessage, _labelStyle);

            if (_sampling)
            {
                GUILayout.Label($"Sampling... {_samplingProgress * 100f:F0}%  " +
                                $"({_samples.Count} readings)", _labelStyle);
                GUILayout.EndArea();
                return;
            }

            GUILayout.Space(10f);
            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition);

            foreach (BeaconDefinition beacon in floorMap.beacons)
            {
                if (beacon == null)
                {
                    continue;
                }

                RssiFilter filter = beaconManager.GetFilter(beacon);
                bool heard = filter != null &&
                             filter.IsFresh(Time.unscaledTime, beaconManager.beaconStaleAfterSeconds);

                string live = heard ? $"{filter.FilteredRssi:F0} dBm" : "not heard";
                string calibrated = _results.ContainsKey(beacon.DisplayName)
                    ? $"  [surveyed {_results[beacon.DisplayName]:F1}]"
                    : "";

                GUILayout.BeginHorizontal();

                GUILayout.Label($"{beacon.DisplayName}   {live}{calibrated}", _labelStyle,
                    GUILayout.Width(width * 0.65f));

                GUI.enabled = heard;

                if (GUILayout.Button("Sample", _labelStyle, GUILayout.Height(fontSize * 2f)))
                {
                    StartSample(beacon);
                }

                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();

            if (GUILayout.Button("Export JSON", _labelStyle, GUILayout.Height(fontSize * 2f)))
            {
                ExportResults();
            }

            GUILayout.EndArea();
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Wayfinding.Positioning
{
    /// <summary>
    /// The real BLE radio adapter, for iOS and Android.
    ///
    /// ---------------------------------------------------------------------------------------
    /// THIS IS THE ONLY FILE THAT KNOWS WHICH BLE PLUGIN YOU BOUGHT.
    /// ---------------------------------------------------------------------------------------
    /// Everything plugin-specific lives in the PLUGIN BRIDGE region near the bottom. Three
    /// methods and one callback. To wire up a plugin:
    ///
    ///   1. Import the plugin package.
    ///   2. Add WAYFINDING_BLE_PLUGIN to Project Settings > Player > Scripting Define Symbols
    ///      (do this for both the iOS and Android tabs).
    ///   3. Fill in the four bodies in the PLUGIN BRIDGE region.
    ///
    /// Until you do step 2 this class compiles and runs, reports Unsupported, and produces no
    /// readings — which means the rest of the project builds today, against MockBeaconScanner,
    /// while the beacons are still in transit.
    ///
    /// PERMISSIONS — the part that eats an afternoon if you skip it:
    ///
    ///   Android 12 (API 31) and up need BLUETOOTH_SCAN and BLUETOOTH_CONNECT at runtime.
    ///     Declare BLUETOOTH_SCAN with android:usesPermissionFlags="neverForLocation" in your
    ///     manifest, otherwise the Play Store review will ask why a wayfinding app wants location.
    ///   Android 11 and below need ACCESS_FINE_LOCATION for BLE scanning. There is no way around
    ///     this on old Android; it is an OS design decision, not a plugin quirk.
    ///   iOS needs NSBluetoothAlwaysUsageDescription in Info.plist with a plain-English reason.
    ///     Write it for a patient, not a developer: "Used to locate you inside the building so
    ///     the app can show you the way to your appointment."
    /// </summary>
    public class BleBeaconScanner : MonoBehaviour, IBeaconScanner
    {
        [Header("Scan behaviour")]
        [Tooltip("Seconds between scan restarts. Android silently throttles an app that scans " +
                 "continuously (5 starts in 30 seconds gets you rate-limited), and some plugins " +
                 "stop delivering callbacks after a few minutes. Cycling the scan avoids both. " +
                 "0 disables cycling.")]
        public float rescanInterval = 20f;

        [Tooltip("Seconds to wait for the plugin to report ready before giving up and reporting " +
                 "an error to the UI.")]
        public float initializeTimeout = 8f;

        [Tooltip("Log every reading to the console. Useful during calibration, extremely noisy " +
                 "otherwise — this fires several times a second per beacon.")]
        public bool verboseLogging;

        public event Action<BeaconReading> ReadingReceived;
        public event Action<BeaconTelemetry> TelemetryReceived;
        public event Action<BeaconScannerStatus, string> StatusChanged;

        public BeaconScannerStatus Status { get; private set; } = BeaconScannerStatus.Uninitialized;

        private Coroutine _initializeRoutine;
        private Coroutine _rescanRoutine;
        private bool _pluginInitialized;
        private bool _startRequested;

        /// <summary>
        /// Transport address to the Eddystone instance ID last seen from it.
        ///
        /// This exists for one reason: a TLM frame carries battery voltage but no identity. The
        /// only thing tying it to a beacon is that it arrived from the same radio as that
        /// beacon's UID frame. The transport address is useless as a global identity — on iOS it
        /// differs on every phone — but within a single scanning session it is a perfectly good
        /// correlation key, which is all this needs.
        /// </summary>
        private readonly Dictionary<string, string> _instanceByTransport =
            new Dictionary<string, string>();

        // ------------------------------------------------------------------
        // IBeaconScanner
        // ------------------------------------------------------------------

        public void Initialize()
        {
            if (Status == BeaconScannerStatus.Initializing || _pluginInitialized)
            {
                return;
            }

#if !WAYFINDING_BLE_PLUGIN
            SetStatus(BeaconScannerStatus.Unsupported,
                "No BLE plugin compiled in. Add the WAYFINDING_BLE_PLUGIN scripting define once " +
                "the plugin is imported, or use MockBeaconScanner for now.");
            return;
#else
            if (_initializeRoutine != null)
            {
                StopCoroutine(_initializeRoutine);
            }

            _initializeRoutine = StartCoroutine(InitializeRoutine());
#endif
        }

        public void StartScanning()
        {
            _startRequested = true;

            if (!_pluginInitialized)
            {
                // Not ready yet. InitializeRoutine will start the scan once the radio comes up.
                Initialize();
                return;
            }

            if (Status == BeaconScannerStatus.Scanning)
            {
                return;
            }

            PluginStartScan();
            SetStatus(BeaconScannerStatus.Scanning, "Scanning for beacons.");

            if (rescanInterval > 0f && _rescanRoutine == null)
            {
                _rescanRoutine = StartCoroutine(RescanRoutine());
            }
        }

        public void StopScanning()
        {
            _startRequested = false;

            if (_rescanRoutine != null)
            {
                StopCoroutine(_rescanRoutine);
                _rescanRoutine = null;
            }

            if (Status != BeaconScannerStatus.Scanning)
            {
                return;
            }

            PluginStopScan();
            SetStatus(BeaconScannerStatus.Ready, "Scan stopped.");
        }

        public void Shutdown()
        {
            StopScanning();

            if (_initializeRoutine != null)
            {
                StopCoroutine(_initializeRoutine);
                _initializeRoutine = null;
            }

            if (_pluginInitialized)
            {
                PluginShutdown();
                _pluginInitialized = false;
            }

            SetStatus(BeaconScannerStatus.Uninitialized, "Shut down.");
        }

        // ------------------------------------------------------------------
        // Unity lifecycle
        // ------------------------------------------------------------------

        private void OnApplicationPause(bool paused)
        {
            // The radio keeps draining battery in the background and iOS will suspend the
            // callbacks anyway. Stop cleanly and resume on the way back in.
            if (paused)
            {
                if (Status == BeaconScannerStatus.Scanning)
                {
                    bool wantedScan = _startRequested;
                    StopScanning();
                    _startRequested = wantedScan;
                }
            }
            else if (_startRequested)
            {
                StartScanning();
            }
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        private IEnumerator InitializeRoutine()
        {
            SetStatus(BeaconScannerStatus.Initializing, "Requesting permissions.");

            yield return RequestPermissions();

            if (Status == BeaconScannerStatus.PermissionDenied)
            {
                _initializeRoutine = null;
                yield break;
            }

            SetStatus(BeaconScannerStatus.Initializing, "Starting Bluetooth.");

            bool pluginReady = false;
            string pluginError = null;

            PluginInitialize(
                onReady: () => pluginReady = true,
                onError: message => pluginError = message);

            float deadline = Time.unscaledTime + initializeTimeout;

            while (!pluginReady && pluginError == null && Time.unscaledTime < deadline)
            {
                yield return null;
            }

            _initializeRoutine = null;

            if (pluginError != null)
            {
                // Plugins conventionally report Bluetooth being switched off as a plain error
                // string; surface it as its own status so the UI can say something actionable.
                bool bluetoothOff = pluginError.IndexOf("off", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    pluginError.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0;

                SetStatus(bluetoothOff ? BeaconScannerStatus.BluetoothOff : BeaconScannerStatus.Error,
                    pluginError);
                yield break;
            }

            if (!pluginReady)
            {
                SetStatus(BeaconScannerStatus.Error,
                    $"Bluetooth did not come up within {initializeTimeout:F0}s.");
                yield break;
            }

            _pluginInitialized = true;
            SetStatus(BeaconScannerStatus.Ready, "Bluetooth ready.");

            if (_startRequested)
            {
                StartScanning();
            }
        }

        private IEnumerator RequestPermissions()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            string[] required = GetRequiredAndroidPermissions();

            foreach (string permission in required)
            {
                if (Permission.HasUserAuthorizedPermission(permission))
                {
                    continue;
                }

                Permission.RequestUserPermission(permission);

                // Unity gives no callback for the permission dialog on older versions, so poll.
                // Give the user a generous window — they may be reading it, or handing the phone
                // to a family member.
                float deadline = Time.realtimeSinceStartup + 60f;

                while (!Permission.HasUserAuthorizedPermission(permission) &&
                       Time.realtimeSinceStartup < deadline)
                {
                    yield return new WaitForSecondsRealtime(0.25f);
                }

                if (!Permission.HasUserAuthorizedPermission(permission))
                {
                    SetStatus(BeaconScannerStatus.PermissionDenied,
                        "Bluetooth permission is needed to find your position inside the building.");
                    yield break;
                }
            }
#endif
            // iOS surfaces its own prompt the first time the plugin touches CoreBluetooth, so
            // there is nothing to request here. The plugin's error callback reports a denial.
            yield return null;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static string[] GetRequiredAndroidPermissions()
        {
            // API 31 (Android 12) split Bluetooth out of the location permission group.
            using var version = new AndroidJavaClass("android.os.Build$VERSION");
            int sdkInt = version.GetStatic<int>("SDK_INT");

            if (sdkInt >= 31)
            {
                return new[]
                {
                    "android.permission.BLUETOOTH_SCAN",
                    "android.permission.BLUETOOTH_CONNECT"
                };
            }

            return new[] { Permission.FineLocation };
        }
#endif

        private IEnumerator RescanRoutine()
        {
            var wait = new WaitForSecondsRealtime(rescanInterval);

            while (true)
            {
                yield return wait;

                if (Status != BeaconScannerStatus.Scanning)
                {
                    continue;
                }

                PluginStopScan();
                ForgetTransportAddresses();
                yield return new WaitForSecondsRealtime(0.25f);
                PluginStartScan();

                if (verboseLogging)
                {
                    Debug.Log("[BleBeaconScanner] Scan cycled.");
                }
            }
        }

        private void SetStatus(BeaconScannerStatus status, string detail)
        {
            if (Status == status)
            {
                return;
            }

            Status = status;

            if (status == BeaconScannerStatus.Error || status == BeaconScannerStatus.PermissionDenied)
            {
                Debug.LogWarning($"[BleBeaconScanner] {status}: {detail}");
            }
            else if (verboseLogging)
            {
                Debug.Log($"[BleBeaconScanner] {status}: {detail}");
            }

            StatusChanged?.Invoke(status, detail);
        }

        /// <summary>
        /// Call this from the plugin's advertising callback, once per advertisement heard.
        ///
        /// Public so the bridge below — and, if you ever need it, a native callback — can reach
        /// it. It expects the RAW advertisement bytes, because the identity we care about lives
        /// inside the payload rather than in whatever address the OS assigned to the radio.
        /// </summary>
        /// <param name="transportAddress">MAC on Android, per-device UUID on iOS. Correlation only.</param>
        /// <param name="rssi">Signal strength in dBm.</param>
        /// <param name="advertisementBytes">The advertisement as received. Null or short is fine — it is ignored.</param>
        public void ReportAdvertisement(string transportAddress, int rssi, byte[] advertisementBytes)
        {
            if (!EddystoneFrame.TryParse(advertisementBytes, out EddystoneFrameType frameType,
                    out EddystoneUid uid, out EddystoneTlm tlm))
            {
                // Not Eddystone. This is the overwhelming majority of traffic in a hospital and
                // is not worth a log line, let alone a warning.
                return;
            }

            float now = Time.unscaledTime;

            switch (frameType)
            {
                case EddystoneFrameType.Uid:
                {
                    if (!string.IsNullOrEmpty(transportAddress))
                    {
                        _instanceByTransport[transportAddress] = uid.InstanceId;
                    }

                    var reading = new BeaconReading(uid.InstanceId, rssi, now, transportAddress);

                    if (!reading.IsPlausible)
                    {
                        return;
                    }

                    if (verboseLogging)
                    {
                        Debug.Log($"[BleBeaconScanner] {reading}");
                    }

                    ReadingReceived?.Invoke(reading);
                    break;
                }

                case EddystoneFrameType.Tlm:
                {
                    // Telemetry has no identity of its own — it is only meaningful once the same
                    // radio has already introduced itself with a UID frame.
                    if (string.IsNullOrEmpty(transportAddress) ||
                        !_instanceByTransport.TryGetValue(transportAddress, out string instanceId))
                    {
                        return;
                    }

                    TelemetryReceived?.Invoke(new BeaconTelemetry(
                        instanceId, tlm.BatteryMillivolts, tlm.TemperatureCelsius,
                        tlm.AdvertisementCount, now));
                    break;
                }
            }
        }

        /// <summary>
        /// Clears the transport-to-instance correlation table. Called on every scan cycle restart,
        /// because addresses can be reassigned and a stale entry would attribute one beacon's
        /// battery reading to another.
        /// </summary>
        private void ForgetTransportAddresses()
        {
            _instanceByTransport.Clear();
        }

        // ==================================================================
        // PLUGIN BRIDGE
        // Everything below is the only plugin-specific code in the project.
        // ==================================================================

        private void PluginInitialize(Action onReady, Action<string> onError)
        {
#if WAYFINDING_BLE_PLUGIN
            // ---- FILL IN FOR YOUR PLUGIN ----------------------------------
            // Shatalmic "Bluetooth LE for iOS, tvOS and Android" looks like this:
            //
            //   BluetoothLEHardwareInterface.Initialize(
            //       asCentral: true,
            //       asPeripheral: false,
            //       action: () => onReady(),
            //       errorAction: error => onError(error));
            //
            // Most other plugins follow the same shape: an initialise call with a success
            // callback and an error callback. Map them onto onReady / onError and the rest of
            // this class works unchanged.
            onError("PluginInitialize is not implemented yet. See the PLUGIN BRIDGE region in " +
                    "BleBeaconScanner.cs.");
#else
            onError("BLE plugin not compiled in.");
#endif
        }

        private void PluginStartScan()
        {
#if WAYFINDING_BLE_PLUGIN
            // ---- FILL IN FOR YOUR PLUGIN ----------------------------------
            // Shatalmic:
            //
            //   BluetoothLEHardwareInterface.ScanForPeripheralsWithServices(
            //       serviceUUIDs: null,           // null = everything; we filter on the payload
            //       action: (address, name) => { },
            //       actionAdvertisingInfo: (address, name, rssi, bytes) =>
            //           ReportAdvertisement(address, rssi, bytes),
            //       rssiOnly: false,              // MUST be false — we need the payload bytes
            //       clearPeripheralList: true);
            //
            // Three things worth getting right here:
            //  - Use the callback that gives you RSSI and BYTES on EVERY advertisement, not the
            //    one that fires once per newly discovered device. The identity is in the bytes,
            //    and positioning needs the repeat readings.
            //  - rssiOnly must be false. With it true you get signal strength and no payload,
            //    which means no Eddystone frame, which means no beacon identity at all.
            //  - Do not filter by service UUID at the radio level. It is tempting to filter on
            //    0xFEAA, and on Android it works, but iOS applies service filters inconsistently
            //    for service-DATA (as opposed to advertised service UUIDs), and an over-tight
            //    filter is the classic reason a scan returns nothing while the beacons are
            //    visibly working in a generic scanner app. Scan wide; the Eddystone parser and
            //    FloorMap discard everything that is not yours, which in a hospital is most of it.
#endif
        }

        private void PluginStopScan()
        {
#if WAYFINDING_BLE_PLUGIN
            // ---- FILL IN FOR YOUR PLUGIN ----------------------------------
            // Shatalmic: BluetoothLEHardwareInterface.StopScan();
#endif
        }

        private void PluginShutdown()
        {
#if WAYFINDING_BLE_PLUGIN
            // ---- FILL IN FOR YOUR PLUGIN ----------------------------------
            // Shatalmic: BluetoothLEHardwareInterface.DeInitialize(() => { });
#endif
        }
    }
}

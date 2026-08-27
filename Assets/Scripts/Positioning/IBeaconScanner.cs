using System;

namespace Wayfinding.Positioning
{
    /// <summary>
    /// One Eddystone-UID advertisement heard from one beacon at one moment.
    /// This is the rawest form of data in the app — everything downstream is derived from it.
    /// </summary>
    public readonly struct BeaconReading
    {
        /// <summary>
        /// The Eddystone instance ID from inside the broadcast: 12 uppercase hex characters.
        /// Identical on every phone, which is the whole reason we identify beacons this way.
        /// </summary>
        public readonly string InstanceId;

        /// <summary>Received signal strength in dBm. Always negative; closer to 0 means closer.</summary>
        public readonly int Rssi;

        /// <summary>
        /// Whatever the platform calls this radio right now — a MAC on Android, a per-device UUID
        /// on iOS. USELESS as an identity (the iOS value differs on every phone), but perfectly
        /// good as a session-local correlation key: it is how a TLM battery frame gets matched to
        /// the UID frame from the same physical unit. Never persist it, never put it in FloorMap.
        /// </summary>
        public readonly string TransportAddress;

        /// <summary>Unscaled time this reading arrived, from Time.unscaledTime.</summary>
        public readonly float Timestamp;

        public BeaconReading(string instanceId, int rssi, float timestamp, string transportAddress = null)
        {
            InstanceId = instanceId;
            Rssi = rssi;
            Timestamp = timestamp;
            TransportAddress = transportAddress;
        }

        /// <summary>
        /// Rejects readings that cannot be real. BLE radios occasionally report 0 or +127 to mean
        /// "unknown", and letting one through produces a distance estimate of a few centimetres,
        /// which drags the whole position solve with it.
        /// </summary>
        public bool IsPlausible =>
            !string.IsNullOrEmpty(InstanceId) && Rssi < 0 && Rssi > -110;

        public override string ToString() => $"{InstanceId} {Rssi} dBm @ {Timestamp:F2}s";
    }

    /// <summary>
    /// An Eddystone-TLM frame: what a beacon reports about itself rather than about you.
    ///
    /// This is the reason beacon fleet health needs no server. Each unit broadcasts its own
    /// battery voltage, so "which of the thirty are dying" is a value you read off the air and
    /// print on screen — no telemetry pipeline, no database, no dashboard service.
    /// </summary>
    public readonly struct BeaconTelemetry
    {
        /// <summary>Instance ID this telemetry belongs to, resolved by transport address.</summary>
        public readonly string InstanceId;

        /// <summary>
        /// Battery in millivolts. A CR2477 reads about 3000 fresh; below roughly 2400 the
        /// advertising becomes unreliable before the beacon actually dies, so treat 2500 as
        /// "order a replacement" rather than waiting for silence.
        /// </summary>
        public readonly int BatteryMillivolts;

        /// <summary>Beacon temperature in Celsius. Mostly a curiosity; 0 if not reported.</summary>
        public readonly float TemperatureCelsius;

        /// <summary>Advertisements this beacon has sent since it powered on. Useful for spotting a unit that has silently reset.</summary>
        public readonly uint AdvertisementCount;

        public readonly float Timestamp;

        public BeaconTelemetry(
            string instanceId,
            int batteryMillivolts,
            float temperatureCelsius,
            uint advertisementCount,
            float timestamp)
        {
            InstanceId = instanceId;
            BatteryMillivolts = batteryMillivolts;
            TemperatureCelsius = temperatureCelsius;
            AdvertisementCount = advertisementCount;
            Timestamp = timestamp;
        }

        /// <summary>Rough state-of-charge for the debug overlay, 0 to 1, from 2200–3000 mV.</summary>
        public float BatteryFraction =>
            UnityEngine.Mathf.Clamp01((BatteryMillivolts - 2200f) / 800f);
    }

    /// <summary>Where the radio currently stands. Drives the UI's "can't scan" states.</summary>
    public enum BeaconScannerStatus
    {
        Uninitialized,
        Initializing,
        Ready,
        Scanning,

        /// <summary>User declined Bluetooth permission. Recoverable — ask again.</summary>
        PermissionDenied,

        /// <summary>Bluetooth is switched off at the OS level. Recoverable — prompt to enable.</summary>
        BluetoothOff,

        /// <summary>No BLE hardware, or running in the editor without the plugin. Not recoverable.</summary>
        Unsupported,

        /// <summary>Plugin threw. Message carries the detail.</summary>
        Error
    }

    /// <summary>
    /// The seam between this app and whatever BLE plugin you end up buying.
    ///
    /// Everything above this interface — BeaconManager, trilateration, navigation, AR — is
    /// written against these six members and nothing else. That is deliberate: BLE plugins for
    /// Unity are a graveyard of abandoned Asset Store packages, and when the one you picked stops
    /// getting updates the replacement cost should be one adapter file, not a rewrite.
    ///
    /// Two implementations ship:
    ///   BleBeaconScanner  - the real radio, iOS and Android, parsing Eddystone frames
    ///   MockBeaconScanner - synthetic readings, for building the app before hardware arrives
    /// </summary>
    public interface IBeaconScanner
    {
        /// <summary>Fires once per Eddystone-UID frame heard. Expect 1-2 Hz per beacon in range.</summary>
        event Action<BeaconReading> ReadingReceived;

        /// <summary>
        /// Fires when a TLM frame is heard AND its transport address has already been seen
        /// carrying a UID frame. Roughly every tenth advertisement per beacon, by default.
        /// </summary>
        event Action<BeaconTelemetry> TelemetryReceived;

        /// <summary>Fires on every status transition, with a human-readable detail string.</summary>
        event Action<BeaconScannerStatus, string> StatusChanged;

        BeaconScannerStatus Status { get; }

        /// <summary>
        /// Brings the radio up: requests permissions, initialises the plugin. Asynchronous —
        /// watch <see cref="StatusChanged"/> for Ready rather than assuming success on return.
        /// </summary>
        void Initialize();

        /// <summary>Begins listening. Safe to call repeatedly; a no-op if already scanning.</summary>
        void StartScanning();

        /// <summary>Stops listening. Call this when the app backgrounds — the radio is not free.</summary>
        void StopScanning();

        /// <summary>Releases the plugin. Call on destroy.</summary>
        void Shutdown();
    }
}

using System;

namespace Wayfinder.Beacons
{
    /// <summary>
    /// One advertisement received from one beacon. Immutable, plugin-agnostic:
    /// whatever BLE plugin is in use converts its own type into this.
    /// </summary>
    public readonly struct BeaconReading
    {
        /// <summary>Stable identity of the beacon (UUID:major:minor, or MAC on Android).</summary>
        public readonly string BeaconId;

        /// <summary>Received signal strength, dBm. Typically -40 (close) to -95 (far).</summary>
        public readonly int Rssi;

        /// <summary>Advertised signal strength at 1 m, dBm. Used by the path-loss model.</summary>
        public readonly int TxPowerAtOneMeter;

        /// <summary>When the advertisement was received (Unity time, seconds).</summary>
        public readonly double Timestamp;

        public BeaconReading(string beaconId, int rssi, int txPowerAtOneMeter, double timestamp)
        {
            BeaconId = beaconId ?? throw new ArgumentNullException(nameof(beaconId));
            Rssi = rssi;
            TxPowerAtOneMeter = txPowerAtOneMeter;
            Timestamp = timestamp;
        }

        public override string ToString() => $"{BeaconId} rssi={Rssi} tx={TxPowerAtOneMeter} t={Timestamp:F2}";
    }
}

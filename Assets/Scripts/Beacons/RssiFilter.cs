using System.Collections.Generic;
using UnityEngine;

namespace Wayfinder.Beacons
{
    /// <summary>
    /// Smooths raw RSSI per beacon and converts it to a distance estimate.
    ///
    /// Plain C#, no MonoBehaviour, so it can be unit-tested off-device.
    /// </summary>
    public sealed class RssiFilter
    {
        private sealed class Track
        {
            public float SmoothedRssi;
            public int TxPowerAtOneMeter;
            public double LastSeen;
            public bool Initialised;
        }

        private readonly Dictionary<string, Track> _tracks = new();

        /// <summary>Weight of each new sample in the exponential moving average (0..1).</summary>
        public float SmoothingAlpha { get; set; } = 0.25f;

        /// <summary>Readings older than this many seconds are dropped as stale.</summary>
        public double StalenessSeconds { get; set; } = 3.0;

        /// <summary>
        /// Path-loss exponent for this environment. ~2.0 in free space; a hospital
        /// corridor is typically 2.5–4. MUST be calibrated on the actual floor —
        /// see docs/floor-map-data.md.
        /// </summary>
        public float PathLossExponent { get; set; } = 2.5f;

        public void Submit(in BeaconReading reading)
        {
            if (!_tracks.TryGetValue(reading.BeaconId, out var track))
            {
                track = new Track();
                _tracks[reading.BeaconId] = track;
            }

            if (!track.Initialised)
            {
                track.SmoothedRssi = reading.Rssi;
                track.Initialised = true;
            }
            else
            {
                track.SmoothedRssi = Mathf.Lerp(track.SmoothedRssi, reading.Rssi, SmoothingAlpha);
            }

            track.TxPowerAtOneMeter = reading.TxPowerAtOneMeter;
            track.LastSeen = reading.Timestamp;
        }

        /// <summary>Drops tracks that have not been heard from within the staleness window.</summary>
        public void PruneStale(double now)
        {
            var expired = new List<string>();
            foreach (var pair in _tracks)
            {
                if (now - pair.Value.LastSeen > StalenessSeconds) expired.Add(pair.Key);
            }
            foreach (var id in expired) _tracks.Remove(id);
        }

        /// <summary>Current smoothed distance estimates, in meters, keyed by beacon id.</summary>
        public IReadOnlyDictionary<string, float> CurrentDistances()
        {
            var result = new Dictionary<string, float>(_tracks.Count);
            foreach (var pair in _tracks)
            {
                if (!pair.Value.Initialised) continue;
                result[pair.Key] = RssiToDistance(pair.Value.SmoothedRssi, pair.Value.TxPowerAtOneMeter);
            }
            return result;
        }

        /// <summary>
        /// Log-distance path-loss model: d = 10 ^ ((txPower - rssi) / (10 * n)).
        /// </summary>
        public float RssiToDistance(float rssi, int txPowerAtOneMeter)
        {
            if (Mathf.Approximately(PathLossExponent, 0f)) return float.PositiveInfinity;
            return Mathf.Pow(10f, (txPowerAtOneMeter - rssi) / (10f * PathLossExponent));
        }

        public void Reset() => _tracks.Clear();
    }
}

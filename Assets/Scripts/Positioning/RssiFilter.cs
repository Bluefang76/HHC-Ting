using System;
using UnityEngine;
using Wayfinding.Data;

namespace Wayfinding.Positioning
{
    /// <summary>
    /// Turns one beacon's stream of raw RSSI values into a stable distance in metres.
    ///
    /// This is the least glamorous file in the project and the one that decides whether the
    /// racing line sits calmly on the floor or vibrates. Raw BLE RSSI in a corridor swings 10 dB
    /// or more between consecutive advertisements — a person walking past, a metal cart, the
    /// phone rotating in your hand. Fed straight into trilateration, that produces a position
    /// that jumps a couple of metres several times a second.
    ///
    /// Three stages, in this order, each fixing what the previous one cannot:
    ///
    ///   1. ROLLING MEDIAN kills outliers outright. A single -95 dBm spike caused by someone
    ///      stepping between you and the beacon is discarded rather than averaged in. A mean
    ///      cannot do this; that is the whole reason for a median here.
    ///
    ///   2. ONE-EURO FILTER smooths what is left, adaptively. When you stand still it filters
    ///      hard and the position stops trembling. When you walk it loosens and the position
    ///      keeps up instead of lagging behind you. A fixed low-pass filter has to choose one or
    ///      the other; this is why it feels better than an exponential average.
    ///
    ///   3. PATH-LOSS CONVERSION turns dBm into metres, then corrects for the fact that the
    ///      beacon is on the ceiling and the phone is at chest height, so the radio path is a
    ///      hypotenuse and the map wants the floor distance.
    /// </summary>
    public class RssiFilter
    {
        private readonly float[] _window;
        private readonly float[] _sortBuffer;
        private int _writeIndex;
        private int _filled;

        private readonly OneEuroFilter _smoother;

        /// <summary>Median-then-smoothed RSSI in dBm. What the debug HUD should display.</summary>
        public float FilteredRssi { get; private set; }

        /// <summary>Most recent raw value, unprocessed. For the HUD, not for maths.</summary>
        public float RawRssi { get; private set; }

        /// <summary>Unscaled time of the most recent reading.</summary>
        public float LastUpdateTime { get; private set; } = -999f;

        /// <summary>Readings seen since the last reset. Below ~3 the estimate is not trustworthy.</summary>
        public int SampleCount { get; private set; }

        /// <summary>
        /// Spread of the current window in dB. High means the beacon is being obstructed
        /// intermittently, which is a good reason to weight it down in the position solve.
        /// </summary>
        public float WindowSpread { get; private set; }

        /// <param name="medianWindow">
        /// Samples in the rolling median. 5 is a good default at 2 Hz advertising: it survives
        /// two consecutive bad packets and costs about a second of lag. Raise it if you push the
        /// beacons to a faster advertising rate.
        /// </param>
        /// <param name="minCutoff">
        /// One-euro minimum cutoff in Hz. Lower means steadier when still. 0.6 is calm without
        /// feeling dead.
        /// </param>
        /// <param name="beta">
        /// One-euro speed coefficient. Higher means the filter opens up faster when the signal
        /// is genuinely changing. Raise this if the position lags behind you when walking.
        /// </param>
        public RssiFilter(int medianWindow = 5, float minCutoff = 0.6f, float beta = 0.02f)
        {
            medianWindow = Mathf.Max(1, medianWindow | 1); // Force odd: a true middle element.
            _window = new float[medianWindow];
            _sortBuffer = new float[medianWindow];
            _smoother = new OneEuroFilter(minCutoff, beta);
        }

        /// <summary>Feeds one advertisement in.</summary>
        public void AddReading(int rssi, float timestamp)
        {
            RawRssi = rssi;
            _window[_writeIndex] = rssi;
            _writeIndex = (_writeIndex + 1) % _window.Length;
            _filled = Mathf.Min(_filled + 1, _window.Length);
            SampleCount++;

            float median = ComputeMedianAndSpread();
            FilteredRssi = _smoother.Filter(median, timestamp);
            LastUpdateTime = timestamp;
        }

        /// <summary>
        /// True if this beacon has been heard recently enough to be worth using.
        /// A beacon that has gone quiet is worse than useless in a position solve: it will happily
        /// keep asserting a stale distance while you walk away from it.
        /// </summary>
        public bool IsFresh(float now, float staleAfterSeconds)
        {
            return SampleCount > 0 && (now - LastUpdateTime) <= staleAfterSeconds;
        }

        /// <summary>
        /// Converts the filtered signal into a horizontal floor distance in metres.
        ///
        /// The model is standard log-distance path loss:
        ///
        ///     RSSI = TxPower - 10 * n * log10(d)
        ///
        /// rearranged for d. TxPower is the measured value at 1 m (survey it — do not guess) and
        /// n is the environment factor. Both live on BeaconDefinition, per beacon, because a
        /// beacon at the end of a corridor full of steel doors genuinely behaves differently
        /// from one in an open waiting area.
        /// </summary>
        /// <returns>False if there is not enough data yet, or the beacon has gone stale.</returns>
        public bool TryGetDistance(
            BeaconDefinition beacon,
            float now,
            float staleAfterSeconds,
            float deviceHeightMeters,
            out float horizontalDistanceMeters)
        {
            horizontalDistanceMeters = 0f;

            if (beacon == null || SampleCount < 2 || !IsFresh(now, staleAfterSeconds))
            {
                return false;
            }

            float exponent = (beacon.txPowerAtOneMeter - FilteredRssi) /
                             (10f * Mathf.Max(beacon.environmentFactor, 1.0f));

            float slantDistance = Mathf.Pow(10f, exponent);

            // Anything beyond this is noise dressed up as a measurement. Log-distance models
            // degrade badly at range: a 5 dB error at 2 m is centimetres, at 25 m it is metres.
            if (float.IsNaN(slantDistance) || slantDistance > 40f)
            {
                return false;
            }

            // Drop from the slanted radio path to the flat floor distance the map works in.
            float verticalOffset = Mathf.Max(beacon.mountHeight - deviceHeightMeters, 0f);
            float squared = (slantDistance * slantDistance) - (verticalOffset * verticalOffset);

            // Standing directly underneath a beacon, the slant distance is essentially all
            // vertical and this goes negative. The honest answer there is "zero metres away".
            horizontalDistanceMeters = squared <= 0f ? 0f : Mathf.Sqrt(squared);
            return true;
        }

        /// <summary>
        /// Confidence in this beacon's current distance, 0 to 1. Trilateration uses it to weight
        /// anchors. Close, steady, freshly heard beacons earn more say in the answer than distant,
        /// erratic ones — which matters, because a far-away beacon's distance estimate can be off
        /// by several metres while looking perfectly reasonable.
        /// </summary>
        public float ConfidenceWeight(float distanceMeters, float now, float staleAfterSeconds)
        {
            if (SampleCount < 2)
            {
                return 0f;
            }

            // Near beacons are far more accurate: error grows roughly with distance in this model.
            float distanceTerm = 1f / (1f + (distanceMeters * distanceMeters * 0.15f));

            // A jumpy window means something is intermittently in the way.
            float stabilityTerm = 1f / (1f + (WindowSpread * 0.25f));

            // Decay smoothly toward the staleness cliff rather than falling off it.
            float age = now - LastUpdateTime;
            float freshnessTerm = Mathf.Clamp01(1f - (age / Mathf.Max(staleAfterSeconds, 0.1f)));

            return distanceTerm * stabilityTerm * freshnessTerm;
        }

        /// <summary>Clears all history. Call after a QR rescan, or when resuming from background.</summary>
        public void Reset()
        {
            Array.Clear(_window, 0, _window.Length);
            _writeIndex = 0;
            _filled = 0;
            SampleCount = 0;
            WindowSpread = 0f;
            FilteredRssi = 0f;
            RawRssi = 0f;
            LastUpdateTime = -999f;
            _smoother.Reset();
        }

        private float ComputeMedianAndSpread()
        {
            Array.Copy(_window, _sortBuffer, _filled);
            Array.Sort(_sortBuffer, 0, _filled);

            WindowSpread = _filled > 1 ? _sortBuffer[_filled - 1] - _sortBuffer[0] : 0f;

            int middle = _filled / 2;

            if (_filled % 2 == 1)
            {
                return _sortBuffer[middle];
            }

            return (_sortBuffer[middle - 1] + _sortBuffer[middle]) * 0.5f;
        }

        /// <summary>
        /// The one-euro filter (Casiez, Roussel and Vogel, 2012). A low-pass filter whose cutoff
        /// rises with the rate of change of the signal: steady when the input is steady, responsive
        /// when it moves. Designed for exactly this problem — noisy input driving something a
        /// human is looking at in real time.
        /// </summary>
        private class OneEuroFilter
        {
            private readonly float _minCutoff;
            private readonly float _beta;
            private const float DerivativeCutoff = 1f;

            private float _previousValue;
            private float _previousDerivative;
            private float _previousTime;
            private bool _initialized;

            public OneEuroFilter(float minCutoff, float beta)
            {
                _minCutoff = Mathf.Max(minCutoff, 0.001f);
                _beta = beta;
            }

            public float Filter(float value, float timestamp)
            {
                if (!_initialized)
                {
                    _initialized = true;
                    _previousValue = value;
                    _previousDerivative = 0f;
                    _previousTime = timestamp;
                    return value;
                }

                float deltaTime = timestamp - _previousTime;

                if (deltaTime <= 0f)
                {
                    deltaTime = 1f / 30f; // Two readings in the same frame; assume a sane rate.
                }

                _previousTime = timestamp;

                float rawDerivative = (value - _previousValue) / deltaTime;
                float derivative = Lerp(_previousDerivative, rawDerivative,
                    SmoothingFactor(deltaTime, DerivativeCutoff));
                _previousDerivative = derivative;

                float cutoff = _minCutoff + (_beta * Mathf.Abs(derivative));
                float filtered = Lerp(_previousValue, value, SmoothingFactor(deltaTime, cutoff));
                _previousValue = filtered;

                return filtered;
            }

            public void Reset()
            {
                _initialized = false;
                _previousValue = 0f;
                _previousDerivative = 0f;
                _previousTime = 0f;
            }

            private static float SmoothingFactor(float deltaTime, float cutoff)
            {
                float timeConstant = 1f / (2f * Mathf.PI * cutoff);
                return 1f / (1f + (timeConstant / deltaTime));
            }

            private static float Lerp(float from, float to, float alpha)
            {
                return from + ((to - from) * Mathf.Clamp01(alpha));
            }
        }
    }
}

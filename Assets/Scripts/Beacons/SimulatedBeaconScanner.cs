using System;
using UnityEngine;

namespace Wayfinder.Beacons
{
    /// <summary>
    /// Editor-only stand-in for a real BLE plugin. Given a "true" position (driven by
    /// whatever is moving in the scene) it produces readings with realistic noise, so
    /// filtering, trilateration and pathing can be built and debugged before a single
    /// beacon is mounted.
    /// </summary>
    public sealed class SimulatedBeaconScanner : IBeaconScanner
    {
        public bool IsScanning { get; private set; }

        public event Action<BeaconReading> ReadingReceived;
        public event Action<BeaconScannerError> ErrorOccurred;

        /// <summary>Where the simulated phone is, in map coordinates (meters).</summary>
        public Vector2 SimulatedPosition { get; set; }

        /// <summary>Path-loss exponent used to synthesise RSSI. Match the calibrated value.</summary>
        public float PathLossExponent { get; set; } = 2.5f;

        /// <summary>Standard deviation of the noise added to synthesised RSSI, in dB.</summary>
        public float NoiseStdDevDb { get; set; } = 4f;

        public void StartScanning()
        {
            IsScanning = true;
        }

        public void StopScanning()
        {
            IsScanning = false;
        }

        /// <summary>
        /// Call once per scan interval from a driver component. Emits one reading per
        /// beacon in range.
        /// </summary>
        public void Tick(double now)
        {
            if (!IsScanning) return;

            // TODO: iterate the FloorMap's beacon anchors, compute true distance from
            //       SimulatedPosition, invert the path-loss model to get an ideal RSSI,
            //       add gaussian noise, and raise ReadingReceived for anything in range.
            _ = ReadingReceived;
            _ = ErrorOccurred;
        }

        public void Dispose() => StopScanning();
    }
}

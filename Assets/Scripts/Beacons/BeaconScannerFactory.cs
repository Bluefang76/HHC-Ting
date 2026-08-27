using UnityEngine;

namespace Wayfinder.Beacons
{
    /// <summary>
    /// The one place that knows which concrete scanner exists on which platform.
    /// </summary>
    public static class BeaconScannerFactory
    {
        /// <summary>
        /// Returns the scanner appropriate to the current platform. In the Editor this is
        /// always the simulator, so the entire positioning stack can be developed and
        /// tested without hardware or a hallway.
        /// </summary>
        public static IBeaconScanner Create()
        {
#if UNITY_EDITOR
            return new SimulatedBeaconScanner();
#elif UNITY_IOS || UNITY_ANDROID
            // TODO: return the chosen BLE plugin's adapter once a plugin is selected.
            //       See docs/setup-and-packages.md for the requirements it must meet.
            Debug.LogWarning("[Wayfinder] No BLE plugin wired up yet — falling back to the simulator.");
            return new SimulatedBeaconScanner();
#else
            Debug.LogWarning("[Wayfinder] Unsupported platform for BLE scanning — using the simulator.");
            return new SimulatedBeaconScanner();
#endif
        }
    }
}

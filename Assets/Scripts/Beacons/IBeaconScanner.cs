using System;

namespace Wayfinder.Beacons
{
    /// <summary>
    /// The seam between the app and whatever BLE plugin is in use.
    ///
    /// Nothing outside this namespace may reference a concrete plugin type. Swapping
    /// plugins, or supporting a new platform, means writing one more implementation of
    /// this interface and changing <see cref="BeaconScannerFactory"/> — nothing else.
    /// </summary>
    public interface IBeaconScanner : IDisposable
    {
        /// <summary>True once the platform radio is available and scanning has started.</summary>
        bool IsScanning { get; }

        /// <summary>Raised for every advertisement received. May fire many times per second.</summary>
        event Action<BeaconReading> ReadingReceived;

        /// <summary>Raised when the scanner cannot continue (radio off, permission denied, plugin error).</summary>
        event Action<BeaconScannerError> ErrorOccurred;

        /// <summary>
        /// Request platform permissions and begin scanning. Safe to call when already scanning.
        /// iOS needs Bluetooth usage permission; Android needs BLUETOOTH_SCAN and, on older
        /// versions, fine location.
        /// </summary>
        void StartScanning();

        /// <summary>Stop scanning and release the radio. Safe to call when not scanning.</summary>
        void StopScanning();
    }

    public enum BeaconScannerError
    {
        Unknown = 0,
        BluetoothDisabled,
        PermissionDenied,
        UnsupportedPlatform,
        PluginFailure
    }
}

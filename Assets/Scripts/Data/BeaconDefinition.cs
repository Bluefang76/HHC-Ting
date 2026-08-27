using System;
using UnityEngine;

namespace Wayfinding.Data
{
    /// <summary>
    /// One physical BLE beacon (BlueCharm BC011) mounted on the floor.
    ///
    /// IDENTITY — read this before configuring any hardware.
    ///
    /// Beacons are identified by their EDDYSTONE-UID INSTANCE ID, not by a MAC address and not
    /// by anything the phone's operating system assigns. That choice is forced by iOS and it is
    /// worth understanding, because getting it wrong is only discovered after all thirty units
    /// are on the wall.
    ///
    ///   - Android reports a beacon's MAC address. Stable, global, fine.
    ///   - iOS does not. CoreBluetooth hands you a UUID derived from the peripheral AND the
    ///     phone doing the scanning, so the same beacon has a different identifier on every
    ///     iPhone. An identifier typed into this asset would work on exactly one device.
    ///   - iOS also filters iBeacon payloads out of CoreBluetooth entirely; reaching them means
    ///     CoreLocation, which means prompting the visitor for LOCATION permission — a terrible
    ///     look for an app whose main claim is that it does not track anyone.
    ///
    /// Eddystone-UID sidesteps all of it. The instance ID travels inside the broadcast, so it is
    /// the same six bytes on every phone, and CoreBluetooth exposes Eddystone service data
    /// without complaint. One identifier, one code path, no location permission.
    ///
    /// CONFIGURE EACH BC011 (via the KBeacon app) TO BROADCAST:
    ///   - Eddystone-UID  — the identity frame. Namespace shared, instance unique per beacon.
    ///   - Eddystone-TLM  — telemetry. Gives you battery voltage for free, so "which beacons are
    ///                      dying" needs no server.
    ///   - iBeacon        — OFF. Nothing here reads it, and leaving it on halves the effective
    ///                      advertising rate of the frames you do use.
    ///
    /// POSITIONS live in FLOOR SPACE: a flat 2D grid you define yourself by pacing the hallway.
    /// X runs along the main corridor, Y across it. No GPS, no true north — the QR code at the
    /// entrance is what ties this grid to the real world.
    /// </summary>
    [Serializable]
    public class BeaconDefinition
    {
        [Header("Identity")]
        [Tooltip("Eddystone-UID instance ID: 6 bytes as 12 hex characters, e.g. '000000000007'. " +
                 "Unique per beacon. Set it in the KBeacon app. A simple scheme beats a clever " +
                 "one — number them 000000000001 upward in the order you mount them, and write " +
                 "the number on the unit with a marker before it goes up.")]
        public string instanceId = "";

        [Tooltip("Human label for the survey tool and debug overlay, e.g. 'B03 - outside 412'.")]
        public string label = "";

        [Tooltip("The MAC address, for your own reference only. Never used for matching — it is " +
                 "here so you can find a specific physical unit again with a generic BLE scanner " +
                 "app when one stops responding. Safe to leave blank.")]
        public string macForReference = "";

        [Header("Placement (floor space, survey units)")]
        [Tooltip("Where this beacon is mounted, in your paced X/Y grid.")]
        public Vector2 position;

        [Tooltip("Height above the floor, in metres. Used to correct the slanted line-of-sight " +
                 "distance down to a flat floor-plane distance. Mount them all at the same height " +
                 "if you can — roughly 2.4 m is above the crowd, which matters because a human " +
                 "body between the phone and a beacon costs you 10 to 15 dB.")]
        public float mountHeight = 2.4f;

        [Header("Radio calibration")]
        [Tooltip("Measured RSSI in dBm at exactly 1 metre. DO NOT guess this — run BeaconSurveyTool " +
                 "against each beacon after it is mounted. A 3 dB error here is roughly a 30% " +
                 "distance error, injected into every solve, permanently.")]
        public float txPowerAtOneMeter = -62f;

        [Tooltip("Path-loss exponent for this beacon's surroundings. 2.0 is free space; a hospital " +
                 "corridor with metal doors, carts and people is usually 2.4 to 3.2.")]
        [Range(1.6f, 4.0f)]
        public float environmentFactor = 2.6f;

        [Header("Runtime behaviour")]
        [Tooltip("Turn off to exclude a beacon from positioning without deleting it — useful when " +
                 "one has a dead battery or is giving you garbage readings.")]
        public bool enabled = true;

        /// <summary>
        /// True if a scanned Eddystone instance ID belongs to this beacon.
        /// Compared case-insensitively and ignoring any separators someone typed by hand, so
        /// "00:00:00:00:00:07" and "000000000007" both match.
        /// </summary>
        public bool Matches(string scannedInstanceId)
        {
            if (string.IsNullOrEmpty(scannedInstanceId) || string.IsNullOrEmpty(instanceId))
            {
                return false;
            }

            return string.Equals(
                NormalizeHex(instanceId),
                NormalizeHex(scannedInstanceId),
                StringComparison.Ordinal);
        }

        /// <summary>The canonical form of this beacon's instance ID: 12 uppercase hex characters.</summary>
        public string NormalizedInstanceId => NormalizeHex(instanceId);

        /// <summary>
        /// Strips anything that is not a hex digit and uppercases the rest, so hand-typed
        /// identifiers with colons, dashes or spaces in them still match.
        /// </summary>
        public static string NormalizeHex(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var buffer = new System.Text.StringBuilder(value.Length);

            foreach (char c in value)
            {
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                {
                    buffer.Append(char.ToUpperInvariant(c));
                }
            }

            return buffer.ToString();
        }

        /// <summary>True if the instance ID is a well-formed 12-character hex string.</summary>
        public bool HasValidInstanceId => NormalizedInstanceId.Length == 12;

        /// <summary>
        /// The identifier to show a human. Prefers the label, falls back to the instance ID.
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(label))
                {
                    return label;
                }

                return string.IsNullOrEmpty(instanceId) ? "(unidentified beacon)" : instanceId;
            }
        }
    }
}

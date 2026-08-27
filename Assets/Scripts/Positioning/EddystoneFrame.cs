using System;
using System.Text;

namespace Wayfinding.Positioning
{
    public enum EddystoneFrameType
    {
        None = -1,
        Uid  = 0x00,
        Url  = 0x10,
        Tlm  = 0x20,
        Eid  = 0x30
    }

    /// <summary>The identity frame: who this beacon is.</summary>
    public readonly struct EddystoneUid
    {
        /// <summary>10 bytes as 20 hex characters. Shared by every beacon in your deployment.</summary>
        public readonly string Namespace;

        /// <summary>6 bytes as 12 hex characters. Unique per beacon. This is the identity.</summary>
        public readonly string InstanceId;

        /// <summary>
        /// The beacon's own claim about its signal strength at 0 metres, in dBm. Interesting but
        /// not trusted here — BeaconSurveyTool measures the real value at 1 m per unit, because
        /// the factory figure knows nothing about the wall it ended up taped to.
        /// </summary>
        public readonly int RangingRssi;

        public EddystoneUid(string ns, string instanceId, int rangingRssi)
        {
            Namespace = ns;
            InstanceId = instanceId;
            RangingRssi = rangingRssi;
        }
    }

    /// <summary>The telemetry frame: how this beacon is doing.</summary>
    public readonly struct EddystoneTlm
    {
        public readonly int BatteryMillivolts;
        public readonly float TemperatureCelsius;
        public readonly uint AdvertisementCount;
        public readonly float UptimeSeconds;

        public EddystoneTlm(int batteryMillivolts, float temperatureCelsius,
                            uint advertisementCount, float uptimeSeconds)
        {
            BatteryMillivolts = batteryMillivolts;
            TemperatureCelsius = temperatureCelsius;
            AdvertisementCount = advertisementCount;
            UptimeSeconds = uptimeSeconds;
        }
    }

    /// <summary>
    /// Parses Eddystone frames out of a raw BLE advertisement.
    ///
    /// Pure byte handling, no Unity, no allocation beyond the hex strings — which makes it
    /// unit-testable and means you can verify it against a known-good capture without a phone.
    ///
    /// WHAT AN ADVERTISEMENT LOOKS LIKE. A BLE advertisement is a sequence of length-prefixed
    /// structures:
    ///
    ///     [len][type][ ...len-1 bytes of payload... ][len][type][ ... ] ...
    ///
    /// Eddystone lives in the structure with type 0x16 — "service data, 16-bit UUID" — whose
    /// payload starts with the service UUID 0xFEAA, little-endian, so the bytes read AA FE.
    /// After that comes the frame type and its fields.
    ///
    /// Plugins are inconsistent about what they hand you: some give the whole advertisement,
    /// some give only the service data payload for a UUID you asked about. This parser accepts
    /// either — it looks for the AD structure first and falls back to treating the buffer as
    /// bare service data.
    /// </summary>
    public static class EddystoneFrame
    {
        private const byte AdTypeServiceData16 = 0x16;
        private const byte ServiceUuidLow  = 0xAA;   // 0xFEAA, little-endian on the air
        private const byte ServiceUuidHigh = 0xFE;

        /// <summary>
        /// Attempts to read an Eddystone frame. Returns false for the overwhelming majority of
        /// advertisements in a hospital — infusion pumps, staff phones, other people's earbuds —
        /// which is normal and must not be logged.
        /// </summary>
        public static bool TryParse(
            byte[] advertisement,
            out EddystoneFrameType frameType,
            out EddystoneUid uid,
            out EddystoneTlm tlm)
        {
            frameType = EddystoneFrameType.None;
            uid = default;
            tlm = default;

            if (!TryFindServiceData(advertisement, out int start, out int length) || length < 1)
            {
                return false;
            }

            byte type = advertisement[start];

            switch (type)
            {
                case (byte)EddystoneFrameType.Uid:
                    if (!TryParseUid(advertisement, start, length, out uid))
                    {
                        return false;
                    }

                    frameType = EddystoneFrameType.Uid;
                    return true;

                case (byte)EddystoneFrameType.Tlm:
                    if (!TryParseTlm(advertisement, start, length, out tlm))
                    {
                        return false;
                    }

                    frameType = EddystoneFrameType.Tlm;
                    return true;

                case (byte)EddystoneFrameType.Url:
                    frameType = EddystoneFrameType.Url;
                    return true;   // Recognised, but this app has no use for it.

                case (byte)EddystoneFrameType.Eid:
                    frameType = EddystoneFrameType.Eid;
                    return true;   // Rotating identifier; would need a resolver service.

                default:
                    return false;
            }
        }

        /// <summary>
        /// Locates the Eddystone service data inside an advertisement, returning where the frame
        /// type byte sits and how many bytes follow it.
        /// </summary>
        private static bool TryFindServiceData(byte[] data, out int frameStart, out int frameLength)
        {
            frameStart = 0;
            frameLength = 0;

            if (data == null || data.Length < 4)
            {
                return false;
            }

            // Walk the length-prefixed AD structures.
            int index = 0;

            while (index < data.Length)
            {
                int structureLength = data[index];

                // A zero length terminates the advertisement; anything running past the buffer
                // means the packet is malformed and there is nothing to salvage.
                if (structureLength == 0 || index + structureLength >= data.Length + 1)
                {
                    break;
                }

                int typeIndex = index + 1;

                if (typeIndex >= data.Length)
                {
                    break;
                }

                if (data[typeIndex] == AdTypeServiceData16 &&
                    typeIndex + 2 < data.Length &&
                    data[typeIndex + 1] == ServiceUuidLow &&
                    data[typeIndex + 2] == ServiceUuidHigh)
                {
                    frameStart = typeIndex + 3;                  // just past the UUID
                    frameLength = structureLength - 3;           // minus AD type and the 2 UUID bytes

                    return frameLength > 0 && frameStart + frameLength <= data.Length;
                }

                index += structureLength + 1;
            }

            // Fallback: some plugins hand over only the service data payload, already stripped.
            // In that case byte 0 is the frame type. Sanity-check it before believing it.
            byte maybeType = data[0];

            if (maybeType == (byte)EddystoneFrameType.Uid ||
                maybeType == (byte)EddystoneFrameType.Tlm ||
                maybeType == (byte)EddystoneFrameType.Url ||
                maybeType == (byte)EddystoneFrameType.Eid)
            {
                frameStart = 0;
                frameLength = data.Length;
                return true;
            }

            return false;
        }

        /// <summary>
        /// UID frame layout, from the frame type byte:
        ///   0        frame type, 0x00
        ///   1        ranging data — RSSI at 0 m, signed
        ///   2 .. 11  namespace, 10 bytes
        ///   12 .. 17 instance, 6 bytes
        ///   18 .. 19 reserved
        /// </summary>
        private static bool TryParseUid(byte[] data, int start, int length, out EddystoneUid uid)
        {
            uid = default;

            if (length < 18)
            {
                return false;
            }

            int rangingRssi = (sbyte)data[start + 1];
            string ns       = ToHex(data, start + 2, 10);
            string instance = ToHex(data, start + 12, 6);

            uid = new EddystoneUid(ns, instance, rangingRssi);
            return true;
        }

        /// <summary>
        /// TLM frame layout (version 0), from the frame type byte:
        ///   0        frame type, 0x20
        ///   1        version, 0x00
        ///   2 .. 3   battery millivolts, big-endian
        ///   4 .. 5   temperature, signed 8.8 fixed point
        ///   6 .. 9   advertisement count since power-on, big-endian
        ///   10 .. 13 uptime in 0.1 s units, big-endian
        /// </summary>
        private static bool TryParseTlm(byte[] data, int start, int length, out EddystoneTlm tlm)
        {
            tlm = default;

            if (length < 14 || data[start + 1] != 0x00)
            {
                return false;
            }

            int battery = (data[start + 2] << 8) | data[start + 3];

            // 8.8 fixed point: the high byte is a signed whole number of degrees, the low byte is
            // 256ths. 0x8000 is the "not supported" sentinel.
            float temperature = 0f;

            if (!(data[start + 4] == 0x80 && data[start + 5] == 0x00))
            {
                temperature = (sbyte)data[start + 4] + (data[start + 5] / 256f);
            }

            uint advertisementCount =
                ((uint)data[start + 6] << 24) |
                ((uint)data[start + 7] << 16) |
                ((uint)data[start + 8] << 8)  |
                data[start + 9];

            uint deciseconds =
                ((uint)data[start + 10] << 24) |
                ((uint)data[start + 11] << 16) |
                ((uint)data[start + 12] << 8)  |
                data[start + 13];

            tlm = new EddystoneTlm(battery, temperature, advertisementCount, deciseconds / 10f);
            return true;
        }

        /// <summary>Uppercase hex, no separators — the canonical form used everywhere in this project.</summary>
        public static string ToHex(byte[] data, int offset, int count)
        {
            var builder = new StringBuilder(count * 2);

            for (int i = 0; i < count; i++)
            {
                builder.Append(data[offset + i].ToString("X2"));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Builds a synthetic UID frame. Used by MockBeaconScanner so the simulated radio produces
        /// bytes in exactly the shape the real parser expects — which means the parser is exercised
        /// at your desk rather than first meeting real data in a corridor.
        /// </summary>
        public static byte[] BuildUidAdvertisement(string namespaceHex, string instanceHex, int rangingRssi)
        {
            byte[] ns = FromHex(namespaceHex, 10);
            byte[] instance = FromHex(instanceHex, 6);

            // [len][0x16][AA][FE][type][ranging][ns × 10][instance × 6][rfu × 2]
            var frame = new byte[23];
            frame[0] = 22;                       // everything after this byte
            frame[1] = AdTypeServiceData16;
            frame[2] = ServiceUuidLow;
            frame[3] = ServiceUuidHigh;
            frame[4] = (byte)EddystoneFrameType.Uid;
            frame[5] = (byte)(sbyte)rangingRssi;

            Array.Copy(ns, 0, frame, 6, 10);
            Array.Copy(instance, 0, frame, 16, 6);

            return frame;
        }

        /// <summary>Builds a synthetic TLM frame, for the same reason.</summary>
        public static byte[] BuildTlmAdvertisement(int batteryMillivolts, float temperatureCelsius,
                                                   uint advertisementCount, float uptimeSeconds)
        {
            var frame = new byte[18];
            frame[0] = 17;
            frame[1] = AdTypeServiceData16;
            frame[2] = ServiceUuidLow;
            frame[3] = ServiceUuidHigh;
            frame[4] = (byte)EddystoneFrameType.Tlm;
            frame[5] = 0x00;                     // version

            frame[6] = (byte)((batteryMillivolts >> 8) & 0xFF);
            frame[7] = (byte)(batteryMillivolts & 0xFF);

            var whole = (sbyte)Math.Floor(temperatureCelsius);
            var fraction = (byte)Math.Round((temperatureCelsius - whole) * 256f);
            frame[8] = (byte)whole;
            frame[9] = fraction;

            frame[10] = (byte)((advertisementCount >> 24) & 0xFF);
            frame[11] = (byte)((advertisementCount >> 16) & 0xFF);
            frame[12] = (byte)((advertisementCount >> 8) & 0xFF);
            frame[13] = (byte)(advertisementCount & 0xFF);

            uint deciseconds = (uint)Math.Max(uptimeSeconds * 10f, 0f);
            frame[14] = (byte)((deciseconds >> 24) & 0xFF);
            frame[15] = (byte)((deciseconds >> 16) & 0xFF);
            frame[16] = (byte)((deciseconds >> 8) & 0xFF);
            frame[17] = (byte)(deciseconds & 0xFF);

            return frame;
        }

        /// <summary>Hex string to bytes, padded or truncated to exactly <paramref name="byteCount"/>.</summary>
        public static byte[] FromHex(string hex, int byteCount)
        {
            var result = new byte[byteCount];
            string clean = Data.BeaconDefinition.NormalizeHex(hex);

            for (int i = 0; i < byteCount; i++)
            {
                int position = i * 2;

                if (position + 1 < clean.Length)
                {
                    result[i] = Convert.ToByte(clean.Substring(position, 2), 16);
                }
            }

            return result;
        }
    }
}

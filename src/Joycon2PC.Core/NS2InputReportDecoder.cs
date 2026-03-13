using System;

namespace Joycon2PC.Core
{
    /// <summary>
    /// Decodes Joy-Con 2 (NS2) BLE input notifications into button/stick values.
    /// This keeps byte-level parsing out of UI or transport code paths.
    /// </summary>
    public static class NS2InputReportDecoder
    {
        /// <summary>Factory calibration centre for all four 12-bit stick axes (min=746, centre=1998, max=3249).</summary>
        public const int NeutralStickValue = 1998;

        /// <summary>
        /// Value the Joy-Con 2 hardware reports in an unused stick slot (e.g. the right-stick bytes on a Joy-Con L).
        /// This is 0x7FF (2047), which is the 11-bit all-ones pattern, not the 12-bit all-ones (0xFFF = 4095).
        /// Axes that read this value are treated as centred rather than as a real deflection.
        /// </summary>
        public const int SentinelStickValue = 2047;

        /// <summary>All parsed values extracted from a single Joy-Con 2 BLE input notification.</summary>
        public readonly struct DecodedInput
        {
            /// <summary>4-byte button bitmask (bytes [4..7] after prefix strip).</summary>
            public uint Buttons { get; init; }

            /// <summary>Raw 12-bit left-stick X before sentinel neutralisation (0 when report is too short).</summary>
            public int RawLeftStickX { get; init; }

            /// <summary>Raw 12-bit left-stick Y before sentinel neutralisation (0 when report is too short).</summary>
            public int RawLeftStickY { get; init; }

            /// <summary>Raw 12-bit right-stick X before sentinel neutralisation (0 when report is too short).</summary>
            public int RawRightStickX { get; init; }

            /// <summary>Raw 12-bit right-stick Y before sentinel neutralisation (0 when report is too short).</summary>
            public int RawRightStickY { get; init; }

            /// <summary>Left-stick X after sentinel neutralisation; equals <see cref="NeutralStickValue"/> when raw value is 0 or <see cref="SentinelStickValue"/>.</summary>
            public int LeftStickX { get; init; }

            /// <summary>Left-stick Y after sentinel neutralisation.</summary>
            public int LeftStickY { get; init; }

            /// <summary>Right-stick X after sentinel neutralisation.</summary>
            public int RightStickX { get; init; }

            /// <summary>Right-stick Y after sentinel neutralisation.</summary>
            public int RightStickY { get; init; }
        }

        public static bool TryDecode(byte[] data, out DecodedInput decoded)
        {
            decoded = default;

            if (data == null || data.Length < 8)
                return false;

            // Strip HID-over-GATT 0xA1 prefix if present.
            int off = (data[0] == 0xA1) ? 1 : 0;
            if (data.Length < off + 8)
                return false;

            // 0x21 is a subcommand reply, not a regular input frame.
            if (data[off] == 0x21)
                return false;

            uint buttons = (uint)data[off + 4]
                         | ((uint)data[off + 5] << 8)
                         | ((uint)data[off + 6] << 16)
                         | ((uint)data[off + 7] << 24);

            int rawLx = 0;
            int rawLy = 0;
            int rawRx = 0;
            int rawRy = 0;

            int lx = NeutralStickValue;
            int ly = NeutralStickValue;
            int rx = NeutralStickValue;
            int ry = NeutralStickValue;

            if (data.Length >= off + 16)
            {
                rawLx = data[off + 10] | ((data[off + 11] & 0x0F) << 8);
                rawLy = (data[off + 11] >> 4) | (data[off + 12] << 4);
                rawRx = data[off + 13] | ((data[off + 14] & 0x0F) << 8);
                rawRy = (data[off + 14] >> 4) | (data[off + 15] << 4);

                lx = (rawLx == 0 || rawLx == SentinelStickValue) ? NeutralStickValue : rawLx;
                ly = (rawLy == 0 || rawLy == SentinelStickValue) ? NeutralStickValue : rawLy;
                rx = (rawRx == 0 || rawRx == SentinelStickValue) ? NeutralStickValue : rawRx;
                ry = (rawRy == 0 || rawRy == SentinelStickValue) ? NeutralStickValue : rawRy;
            }

            decoded = new DecodedInput
            {
                Buttons        = buttons,
                RawLeftStickX  = rawLx,
                RawLeftStickY  = rawLy,
                RawRightStickX = rawRx,
                RawRightStickY = rawRy,
                LeftStickX     = lx,
                LeftStickY     = ly,
                RightStickX    = rx,
                RightStickY    = ry,
            };
            return true;
        }
    }
}

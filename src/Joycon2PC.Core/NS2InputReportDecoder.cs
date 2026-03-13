using System;

namespace Joycon2PC.Core
{
    /// <summary>
    /// Decodes Joy-Con 2 (NS2) BLE input notifications into button/stick values.
    /// This keeps byte-level parsing out of UI or transport code paths.
    /// </summary>
    public static class NS2InputReportDecoder
    {
        public const int NeutralStickValue = 1998;
        public const int SentinelStickValue = 2047;

        public readonly struct DecodedInput
        {
            public DecodedInput(
                uint buttons,
                int rawLeftStickX,
                int rawLeftStickY,
                int rawRightStickX,
                int rawRightStickY,
                int leftStickX,
                int leftStickY,
                int rightStickX,
                int rightStickY)
            {
                Buttons = buttons;
                RawLeftStickX = rawLeftStickX;
                RawLeftStickY = rawLeftStickY;
                RawRightStickX = rawRightStickX;
                RawRightStickY = rawRightStickY;
                LeftStickX = leftStickX;
                LeftStickY = leftStickY;
                RightStickX = rightStickX;
                RightStickY = rightStickY;
            }

            public uint Buttons { get; }
            public int RawLeftStickX { get; }
            public int RawLeftStickY { get; }
            public int RawRightStickX { get; }
            public int RawRightStickY { get; }
            public int LeftStickX { get; }
            public int LeftStickY { get; }
            public int RightStickX { get; }
            public int RightStickY { get; }
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

            decoded = new DecodedInput(buttons, rawLx, rawLy, rawRx, rawRy, lx, ly, rx, ry);
            return true;
        }
    }
}

using System;

namespace Joycon2PC.Core
{
    /// <summary>
    /// Helpers to build Joy-Con subcommand and rumble payloads.
    /// Reference: dekuNukem/Nintendo_Switch_Reverse_Engineering, hid-nintendo.c
    /// </summary>
    public static class SubcommandBuilder
    {
        /// <summary>
        /// Build a subcommand payload: [subcommand, ...data].
        /// The transport wrapper (report id, sequence byte) is added by SubcommandManager.
        /// </summary>
        public static byte[] BuildSubcommand(byte subcommand, byte[] data)
        {
            var outp = new byte[1 + (data?.Length ?? 0)];
            outp[0] = subcommand;
            if (data != null && data.Length > 0)
                Buffer.BlockCopy(data, 0, outp, 1, data.Length);
            return outp;
        }

        // ── Rumble encoding ──────────────────────────────────────────────────
        // The Joy-Con expects an 8-byte rumble payload split into two 4-byte
        // motor descriptors (high-band/right, then low-band/left).
        //
        // Each motor descriptor encodes two frequencies (HF and LF encoded as a
        // 1-byte table index) and an 8-bit amplitude using the exact lookup tables
        // from hid-nintendo.c / BetterJoy.
        //
        // Byte layout for each 4-byte descriptor (from hid-nintendo.c):
        //   byte0 = HF freq encoded bits[7:0]
        //   byte1 = HF freq encoded bits[8] | (HF amp bits[6:0])
        //   byte2 = LF freq encoded
        //   byte3 = LF amp
        //
        // We expose a simple BuildRumble(float ampHigh, float ampLow) helper
        // that sends a neutral 320 Hz / 160 Hz constant rumble at the requested
        // amplitude (0.0 = off, 1.0 = full).  For a "neutral off" packet call
        // BuildRumbleOff().

        // Neutral rumble-off packet (all motors silent).
        public static byte[] BuildRumbleOff()
            => new byte[] { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 };

        /// <summary>
        /// Build an 8-byte rumble payload.
        /// <paramref name="ampHigh"/> and <paramref name="ampLow"/> are 0.0–1.0.
        /// Uses 320 Hz (high band) and 160 Hz (low band) — the most commonly used
        /// Joy-Con rumble frequencies for generic feedback.
        /// </summary>
        public static byte[] BuildRumble(float ampHigh = 0.0f, float ampLow = 0.0f)
        {
            ampHigh = Math.Clamp(ampHigh, 0f, 1f);
            ampLow  = Math.Clamp(ampLow,  0f, 1f);

            // HF: 320 Hz encoded as 0x60. LF: 160 Hz encoded as 0x40.
            // These are the exact bytes used by BetterJoy / hid-nintendo for generic rumble.
            // Amplitude is encoded in the upper nibble with a small fixed table.
            byte hfFreq = 0x60; // 320 Hz
            byte lfFreq = 0x40; // 160 Hz

            // Amplitude encoding: linear scale into the byte range used by the driver.
            // At 0 → 0x00, at 1.0 → 0x72 (max usable without clipping on most Joy-Cons).
            byte encodeAmp(float a) => (byte)(a * 0x72);

            byte ampH = encodeAmp(ampHigh);
            byte ampL = encodeAmp(ampLow);

            // Descriptor layout per motor:
            //   [0] = HF freq low byte
            //   [1] = HF freq high bit | HF amp
            //   [2] = LF freq
            //   [3] = LF amp
            var buf = new byte[8];

            // Motor 1 (right/high-band)
            buf[0] = hfFreq;
            buf[1] = (byte)(0x00 | (ampH & 0x7F));   // bit8 of freq = 0 for 320Hz
            buf[2] = lfFreq;
            buf[3] = ampL;

            // Motor 2 (left/low-band) — same settings
            buf[4] = hfFreq;
            buf[5] = (byte)(0x00 | (ampH & 0x7F));
            buf[6] = lfFreq;
            buf[7] = ampL;

            return buf;
        }

        // ── Common subcommands ───────────────────────────────────────────────

        /// <summary>Enable IMU (6-axis). Send once after connecting.</summary>
        public static byte[] EnableIMU(bool enable = true)
            => BuildSubcommand(0x40, new byte[] { enable ? (byte)0x01 : (byte)0x00 });

        /// <summary>Enable vibration (rumble). Send once after connecting.</summary>
        public static byte[] EnableVibration(bool enable = true)
            => BuildSubcommand(0x48, new byte[] { enable ? (byte)0x01 : (byte)0x00 });

        /// <summary>
        /// Set the input report mode.
        /// 0x30 = standard full (buttons + sticks + IMU, ~60 Hz)
        /// 0x3F = simple HID (low latency, buttons + sticks only)
        /// </summary>
        public static byte[] SetInputReportMode(byte mode = 0x30)
            => BuildSubcommand(0x03, new byte[] { mode });

        /// <summary>
        /// Set player LED pattern (Joy-Con 1 / Joy-Con 2 HOGP subcommand 0x30).
        /// <paramref name="pattern"/> bits: [3:0] = LEDs solid, [7:4] = LEDs flashing.
        /// Player 1 = 0x01, Player 2 = 0x03, Player 3 = 0x07, Player 4 = 0x0F.
        /// NOTE: This is the Joy-Con 1 subcommand. For Joy-Con 2 (NS2) use BuildNS2PlayerLed().
        /// </summary>
        public static byte[] SetPlayerLeds(byte pattern)
            => BuildSubcommand(0x30, new byte[] { pattern });

        // ── NS2 / Joy-Con 2 (Switch 2) commands ─────────────────────────────
        // The NS2 uses a different custom BLE service and command format.
        // Reference: Nohzockt/Switch2-Controllers (NS2-Connect.py)

        // Rolling counter for NS2 rumble/keep-alive packets (0-15, wraps).
        private static int _ns2Counter = 0;

        /// <summary>
        /// Build an NS2 keep-alive (silent rumble) packet.
        /// Send every ~15 ms to prevent the Joy-Con 2 from disconnecting.
        /// Format: [0x50, 0x50|(counter&amp;0x0F), 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]
        /// </summary>
        public static byte[] BuildNS2KeepAlive()
        {
            int c = System.Threading.Interlocked.Increment(ref _ns2Counter) & 0x0F;
            return new byte[] { 0x50, (byte)(0x50 | c), 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        }

        /// <summary>
        /// Build an NS2 keep-alive with an explicit per-device rolling counter (0-15).
        /// Each device should maintain its own counter to avoid counter collisions.
        /// </summary>
        public static byte[] BuildNS2KeepAlive(byte counter)
            => new byte[] { 0x50, (byte)(0x50 | (counter & 0x0F)), 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        /// <summary>
        /// Build an NS2 rumble packet.
        /// <paramref name="on"/> true = rumble on, false = off (keep-alive).
        /// Format: [0x50, 0x50|(counter&amp;0x0F), onByte, 0x00, 0x00, 0x00, 0x00, 0x00]
        /// </summary>
        public static byte[] BuildNS2Rumble(bool on = false)
        {
            int c = System.Threading.Interlocked.Increment(ref _ns2Counter) & 0x0F;
            return new byte[] { 0x50, (byte)(0x50 | c), on ? (byte)0x01 : (byte)0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        }

        /// <summary>
        /// Build an NS2 player LED command (confirms player number and stops the pairing blink).
        /// <paramref name="playerNum"/> 1-4 (maps to led patterns 0x01,0x02,0x04,0x08).
        /// Returns a 16-byte output report.
        /// Format: [0x30,0x01,0x00,0x30,0x00,0x08,0x00,0x00,ledValue,0x00,...,0x00]
        /// </summary>
        public static byte[] BuildNS2PlayerLed(int playerNum = 1)
        {
            // BT_HID_LED_DEV_ID_MAP from NS2-Connect.py
            byte[] ledMap = { 0x01, 0x02, 0x04, 0x08, 0x03, 0x06, 0x0C, 0x0F };
            byte ledValue = ledMap[Math.Clamp(playerNum - 1, 0, 7)];
            var cmd = new byte[16];
            cmd[0] = 0x30;
            cmd[1] = 0x01;
            cmd[2] = 0x00;
            cmd[3] = 0x30;
            cmd[4] = 0x00;
            cmd[5] = 0x08;
            cmd[6] = 0x00;
            cmd[7] = 0x00;
            cmd[8] = ledValue;
            // bytes 9-15 = 0x00 (already zero-initialised)
            return cmd;
        }

        /// <summary>
        /// Build an NS2 set-input-report-mode command.
        /// <paramref name="mode"/> 0x3F = simple HID (continuous full-rate reports),
        /// 0x30 = standard full mode (default firmware mode, change-triggered).
        /// Sending 0x3F switches the Joy-Con 2 to continuous streaming, which eliminates
        /// perceived joystick lag and enables drawing smooth circles.
        /// Returns a 16-byte output report.
        /// </summary>
        public static byte[] BuildNS2SetInputMode(byte mode = 0x3F)
        {
            var cmd = new byte[16];
            cmd[0] = 0x30;   // output report type
            cmd[1] = 0x01;
            cmd[2] = 0x00;
            cmd[3] = 0x03;   // subcommand: SetInputReportMode
            cmd[4] = 0x00;
            cmd[5] = 0x01;   // data length = 1
            cmd[6] = 0x00;
            cmd[7] = 0x00;
            cmd[8] = mode;   // 0x3F = full-rate / 0x30 = standard
            return cmd;
        }
    }
}

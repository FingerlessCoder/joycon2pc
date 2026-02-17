using System;

namespace Joycon2PC.Core
{
    public class JoyconState
    {
        public ushort Buttons { get; set; }
        public byte LeftStickX { get; set; }
        public byte LeftStickY { get; set; }
        public byte RightStickX { get; set; }
        public byte RightStickY { get; set; }
    }

    public class JoyconParser
    {
        // Fires when a parsed state is available
        public event Action<JoyconState>? StateChanged;

        public JoyconParser()
        {
        }

        // Very small/safe parser: reads a 2-byte button mask at offset 1
        // and optional stick bytes if present. This is a scaffold to
        // replace with the full parsing logic from hid-nintendo.c later.
        public void Parse(byte[] report)
        {
            if (report == null || report.Length < 3)
            {
                Console.WriteLine("[JoyconParser] Report too short");
                return;
            }

            try
            {
                var state = new JoyconState();

                // Many Switch reports use report[0] as report ID; button mask often at 1..2
                state.Buttons = (ushort)(report[1] | (report.Length > 2 ? (report[2] << 8) : 0));

                if (report.Length >= 7)
                {
                    state.LeftStickX = report[3];
                    state.LeftStickY = report[4];
                    state.RightStickX = report[5];
                    state.RightStickY = report[6];
                }

                StateChanged?.Invoke(state);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JoyconParser] Parse error: {ex.Message}");
            }
        }
    }
}

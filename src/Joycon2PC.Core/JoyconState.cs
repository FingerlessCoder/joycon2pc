using System;

namespace Joycon2PC.Core
{
    // High-level parsed state for a single Joy-Con 2 (Switch 2 / NS2).
    //
    // This type is LIVE: it is the state object handed to ViGEmBridge.UpdateFromState,
    // cloned/merged in MainForm.Pipeline.cs and consumed by the UI refresh path.
    // It used to live inside JoyconParser.cs; it was moved here so that JoyconParser
    // (dead code, see that file) can be deleted without touching live behaviour.
    //
    // Population of this type happens in MainForm.Pipeline.cs from the byte-level
    // decode done by NS2InputReportDecoder.TryDecode — not by JoyconParser.
    public class JoyconState
    {
        // 32-bit button word from report bytes [4..7] (little-endian).
        // See SW2Button enum for individual bit definitions.
        public uint Buttons { get; set; }

        // 12-bit stick values (0..4095, factory centre ≈ 1998)
        public int LeftStickX  { get; set; }
        public int LeftStickY  { get; set; }
        public int RightStickX { get; set; }
        public int RightStickY { get; set; }

        // Convenience: test a single button.
        public bool IsPressed(SW2Button btn) => (Buttons & (uint)btn) != 0;
    }
}
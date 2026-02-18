using System;

namespace Joycon2PC.Core
{
    // 32-bit button flags for Joy-Con 2 (Switch 2 / NS2).
    //
    // Source: Nohzockt/Switch2-Controllers (NS2-Connect.py, ns2-ble-cgamepad.py)
    //
    // These bits come from report bytes [4..7] read as a 32-bit little-endian uint.
    // The enum is split into logical groups matching the physical controller layout:
    //   Right Joy-Con face buttons / shoulder buttons (bits 0-7)
    //   Menu / stick-click buttons   (bits 8-14)
    //   D-Pad (Left Joy-Con)         (bits 16-19)
    //   Left Joy-Con shoulder        (bits 20-23)
    //   Grip buttons (Joy-Con 2 new) (bits 24-25)
    //
    // Note: Bit 15 is reserved / unknown.
    // Note: The C button is new on Joy-Con 2 R (right side, bit 14).

    [Flags]
    public enum SW2Button : uint
    {
        // ── Right Joy-Con face + shoulder ─────────────────────────────────
        Y        = 1u << 0,   // face button
        X        = 1u << 1,   // face button
        B        = 1u << 2,   // face button
        A        = 1u << 3,   // face button
        RSR      = 1u << 4,   // SR on right Joy-Con
        RSL      = 1u << 5,   // SL on right Joy-Con
        R        = 1u << 6,   // right shoulder
        ZR       = 1u << 7,   // right trigger

        // ── Shared / menu buttons ─────────────────────────────────────────
        Minus    = 1u << 8,   // - button
        Plus     = 1u << 9,   // + button
        RStick   = 1u << 10,  // right stick click
        LStick   = 1u << 11,  // left stick click
        Home     = 1u << 12,  // home button
        Capture  = 1u << 13,  // capture / screenshot button
        C        = 1u << 14,  // *** NEW on Joy-Con 2 R *** (between ZR and R)
        // bit 15 reserved / unknown

        // ── Left Joy-Con d-pad ────────────────────────────────────────────
        Down     = 1u << 16,
        Up       = 1u << 17,
        Right    = 1u << 18,  // d-pad right
        Left     = 1u << 19,  // d-pad left
        LSR      = 1u << 20,  // SR on left Joy-Con
        LSL      = 1u << 21,  // SL on left Joy-Con
        L        = 1u << 22,  // left shoulder
        ZL       = 1u << 23,  // left trigger

        // ── Grip buttons (Joy-Con 2 new) ──────────────────────────────────
        GripRight = 1u << 24, // grip button on right Joy-Con
        GripLeft  = 1u << 25, // grip button on left Joy-Con
    }
}


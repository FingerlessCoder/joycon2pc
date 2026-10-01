using System;

namespace Joycon2PC.Core
{
    // ============================================================================================
    //  DEAD CODE — scheduled for removal. See the note at the bottom of this file.
    // ============================================================================================
    //
    // Parser for Joy-Con 2 (Switch 2 / NS2) BLE notification reports.
    //
    // NS2 custom GATT service: ab7de9be-89fe-49ad-828f-118f09df7fd0
    // Input notification characteristic: ab7de9be-89fe-49ad-828f-118f09df7fd2
    //
    // There is NO call site for Parse() anywhere in the repository. The live decode
    // path is NS2InputReportDecoder.TryDecode (Core) called from
    // MainForm.RawReportReceived (App). Everything here is therefore unreachable;
    // it is kept only because MainForm still declares/initialises a JoyconParser
    // field and subscribes to its StateChanged event.
    //
    // Notification payload layout (InTheHand GATT delivers raw ATT notification value):
    //   [0]      = 0xA1  — BLE HID-over-GATT "Input Report" ATT opcode prefix
    //                      (present when InTheHand delivers raw ATT value; stripped before parsing)
    //
    //   After stripping 0xA1 (logical offsets):
    //   [0]      = rolling counter — NOT a report ID
    //   [1..3]   = header (timer, battery, misc)
    //   [4..7]   = button data — 32-bit little-endian uint (26 buttons; see SW2Button)
    //   [8..9]   = misc / unknown
    //   [10..15] = stick axes — two 12-bit packed values per stick (same encoding as JC1)
    //              LX = data[10] | ((data[11] & 0x0F) << 8)
    //              LY = (data[11] >> 4) | (data[12] << 4)
    //              RX = data[13] | ((data[14] & 0x0F) << 8)
    //              RY = (data[14] >> 4) | (data[15] << 4)
    //   [16+]    = extra data
    public class JoyconParser
    {
        public event Action<JoyconState>? StateChanged;

        public JoyconParser() { }

        // Entry point: called from raw characteristic notification bytes.
        // No caller exists — see the class comment above.
        public void Parse(byte[] report)
        {
            if (report == null || report.Length < 8)
            {
                Console.WriteLine("[JoyconParser] Report too short or null");
                return;
            }

            try
            {
                // NS2 controllers do NOT use a fixed report ID at byte[0].
                // Byte[0] is a rolling timer/counter (0x3F, 0x4D, …) — NOT a report type.
                // We always parse the full report if it's long enough.
                //
                // Exception: 0x21 = subcommand reply (for SubcommandManager ACK)
                if (report[0] == 0x21)
                {
                    Console.WriteLine("[JoyconParser] Subcommand reply (0x21)");
                    return;
                }

                ParseNS2InputReport(report);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JoyconParser] Parse error: {ex.Message}");
            }
        }

        // Decode NS2 input report.
        // Defensive: reads only as many bytes as are present.
        private void ParseNS2InputReport(byte[] r)
        {
            var state = new JoyconState();

            // Strip 0xA1 HID-over-GATT prefix if present
            int off = (r.Length > 0 && r[0] == 0xA1) ? 1 : 0;

            // Buttons — 32-bit little-endian at logical [4..7]
            if (r.Length >= off + 8)
            {
                state.Buttons = (uint)r[off + 4]
                              | ((uint)r[off + 5] << 8)
                              | ((uint)r[off + 6] << 16)
                              | ((uint)r[off + 7] << 24);
            }

            // Sticks — 12-bit packed at logical [10..15]
            // Sentinel 0x7FF (2047) = axis not present on this Joy-Con side; treat as neutral.
            // Value 0 also means no data; treat as neutral (1998).
            if (r.Length >= off + 16)
            {
                static int Neutral(int v) => (v == 0 || v == 2047) ? 1998 : v;

                state.LeftStickX  = Neutral(r[off + 10] | ((r[off + 11] & 0x0F) << 8));
                state.LeftStickY  = Neutral((r[off + 11] >> 4) | (r[off + 12] << 4));
                state.RightStickX = Neutral(r[off + 13] | ((r[off + 14] & 0x0F) << 8));
                state.RightStickY = Neutral((r[off + 14] >> 4) | (r[off + 15] << 4));
            }

            StateChanged?.Invoke(state);
        }
    }

    // --------------------------------------------------------------------------------------------
    //  REMOVAL NOTE
    //
    //  To delete this file, remove these five lines (they live in files owned by another lane):
    //
    //    src/Joycon2PC.App/MainForm.cs:115    private JoyconParser _parser = new();
    //    src/Joycon2PC.App/MainForm.cs:233    _parser.StateChanged += OnStateChanged;
    //    src/Joycon2PC.App/MainForm.Options.cs:357  _parser.StateChanged -= OnStateChanged;
    //    src/Joycon2PC.App/MainForm.Options.cs:358  _parser = new JoyconParser();
    //    src/Joycon2PC.App/MainForm.Options.cs:359  _parser.StateChanged += OnStateChanged;
    //
    //  and then remove the OnStateChanged handler at
    //    src/Joycon2PC.App/MainForm.Pipeline.cs:1608
    //
    //  No other file references JoyconParser, StateChanged or Parse. JoyconState (used by live
    //  code) has been moved to its own file, JoyconState.cs, so it survives this deletion.
    // --------------------------------------------------------------------------------------------
}

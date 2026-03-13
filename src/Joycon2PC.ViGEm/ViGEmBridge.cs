using System;
using Joycon2PC.Core;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace Joycon2PC.ViGEm
{
    // Maps a parsed JoyconState (Joy-Con 2 / NS2) onto a virtual Xbox 360 controller via ViGEm.
    //
    // Joy-Con 2 → XInput mapping:
    //   A   → A        B   → B        X   → X        Y   → Y
    //   R   → RB       ZR  → RT (trigger, full-press)
    //   L   → LB       ZL  → LT (trigger, full-press)
    //   RSL/RSR → RB   LSL/LSR → LB (rail buttons fallback mapping)
    //   +   → Start    -   → Back
    //   Home→ Guide    Capture → (unused)
    //   LStick click → LS   RStick click → RS
    //   D-Pad Up/Down/Left/Right → D-Pad
    //   C   → Right Thumb (extra Joy-Con 2 R button)
    public class ViGEmBridge : IDisposable
    {
        private ViGEmClient? _client;
        private IXbox360Controller? _controller;
        private bool _connected = false;

        // NS2 factory calibration defaults (from NS2-Connect.py / BetterJoy-NS2)
        // Raw 12-bit value range: min=746, centre=1998, max=3249
        private const int NS2_CENTRE = 1998;
        private const int NS2_RANGE  = 3249 - 746;   // ≈ 2503 counts each side (asymmetric)

        // Output conditioning for smoother and more stable stick behavior.
        // Deadzone is applied in Xbox signed-short space (-32768..32767).
        private const short AXIS_OUTPUT_DEADZONE = 2200;
        private const float AXIS_SMOOTHING_ALPHA = 0.35f;

        private bool _axisFilterInitialized = false;
        private short _filteredLeftX;
        private short _filteredLeftY;
        private short _filteredRightX;
        private short _filteredRightY;

        /// <summary>
        /// Fired when the game/OS sends a rumble command to the virtual Xbox controller.
        /// Parameters: (largeMotor 0–255, smallMotor 0–255).
        /// Subscribe to this to forward haptic feedback to the physical Joy-Con 2.
        /// </summary>
        public event Action<byte, byte>? RumbleReceived;

        public ViGEmBridge() { }

        public void Connect()
        {
            try
            {
                _client = new ViGEmClient();
                _controller = _client.CreateXbox360Controller();
                _controller.AutoSubmitReport = false; // batch all changes then submit once
                _controller.FeedbackReceived += (sender, e) =>
                {
                    RumbleReceived?.Invoke(e.LargeMotor, e.SmallMotor);
                };
                _controller.Connect();
                _connected = true;
                Console.WriteLine("[ViGEmBridge] Virtual Xbox 360 controller connected.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ViGEmBridge] Connect failed: {ex.Message}");
                _connected = false;
            }
        }

        public void UpdateFromState(JoyconState state)
        {
            if (!_connected || _controller == null)
            {
                Console.WriteLine($"[ViGEmBridge] (no device) LX={state.LeftStickX} LY={state.LeftStickY}");
                return;
            }

            try
            {
                var c = _controller;

                // ── Axes ─────────────────────────────────────────────────────────────
                // 12-bit NS2 stick (0..4095, factory centre ≈ 1998) → signed short (-32768..32767)
                short rawLx = MapStick(state.LeftStickX);
                short rawLy = MapStick(state.LeftStickY);
                short rawRx = MapStick(state.RightStickX);
                short rawRy = MapStick(state.RightStickY);

                if (!_axisFilterInitialized)
                {
                    _filteredLeftX = ApplyDeadzone(rawLx);
                    _filteredLeftY = ApplyDeadzone(rawLy);
                    _filteredRightX = ApplyDeadzone(rawRx);
                    _filteredRightY = ApplyDeadzone(rawRy);
                    _axisFilterInitialized = true;
                }
                else
                {
                    _filteredLeftX = FilterAxis(_filteredLeftX, rawLx);
                    _filteredLeftY = FilterAxis(_filteredLeftY, rawLy);
                    _filteredRightX = FilterAxis(_filteredRightX, rawRx);
                    _filteredRightY = FilterAxis(_filteredRightY, rawRy);
                }

                c.SetAxisValue(Xbox360Axis.LeftThumbX, _filteredLeftX);
                c.SetAxisValue(Xbox360Axis.LeftThumbY, _filteredLeftY);
                c.SetAxisValue(Xbox360Axis.RightThumbX, _filteredRightX);
                c.SetAxisValue(Xbox360Axis.RightThumbY, _filteredRightY);

                // ZL / ZR → analogue triggers (0 or 255)
                c.SetSliderValue(Xbox360Slider.LeftTrigger,  state.IsPressed(SW2Button.ZL) ? (byte)255 : (byte)0);
                c.SetSliderValue(Xbox360Slider.RightTrigger, state.IsPressed(SW2Button.ZR) ? (byte)255 : (byte)0);

                // ── Buttons ──────────────────────────────────────────────────────────
                // Face
                c.SetButtonState(Xbox360Button.A, state.IsPressed(SW2Button.A));
                c.SetButtonState(Xbox360Button.B, state.IsPressed(SW2Button.B));
                c.SetButtonState(Xbox360Button.X, state.IsPressed(SW2Button.X));
                c.SetButtonState(Xbox360Button.Y, state.IsPressed(SW2Button.Y));

                // Shoulders
                bool leftShoulder = state.IsPressed(SW2Button.L)
                                 || state.IsPressed(SW2Button.LSL)
                                 || state.IsPressed(SW2Button.LSR);
                bool rightShoulder = state.IsPressed(SW2Button.R)
                                  || state.IsPressed(SW2Button.RSL)
                                  || state.IsPressed(SW2Button.RSR);
                c.SetButtonState(Xbox360Button.RightShoulder, rightShoulder);
                c.SetButtonState(Xbox360Button.LeftShoulder,  leftShoulder);

                // Menu / system
                c.SetButtonState(Xbox360Button.Start,      state.IsPressed(SW2Button.Plus));
                c.SetButtonState(Xbox360Button.Back,       state.IsPressed(SW2Button.Minus));
                c.SetButtonState(Xbox360Button.Guide,      state.IsPressed(SW2Button.Home));
                c.SetButtonState(Xbox360Button.LeftThumb,  state.IsPressed(SW2Button.LStick));
                c.SetButtonState(Xbox360Button.RightThumb, state.IsPressed(SW2Button.RStick));

                // D-Pad
                c.SetButtonState(Xbox360Button.Up,    state.IsPressed(SW2Button.Up));
                c.SetButtonState(Xbox360Button.Down,  state.IsPressed(SW2Button.Down));
                c.SetButtonState(Xbox360Button.Left,  state.IsPressed(SW2Button.Left));
                c.SetButtonState(Xbox360Button.Right, state.IsPressed(SW2Button.Right));

                c.SubmitReport();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ViGEmBridge] Update failed: {ex.Message}");
            }
        }

        // Map NS2 12-bit stick value (0..4095, centre ≈ 1998) to signed short.
        // v == 0 means no data received yet — treat as centre.
        private static short MapStick(int v)
        {
            if (v == 0) return 0; // no data → output centre (0 in Xbox signed space)

            // Centre-relative, scaled so that max deviation → ±32767
            int centered = v - NS2_CENTRE;

            // Use asymmetric half-ranges matching factory calibration
            int halfRange = centered >= 0
                ? (3249 - NS2_CENTRE)   // positive half: 1251 counts to max
                : (NS2_CENTRE - 746);   // negative half: 1252 counts to min

            if (halfRange == 0) return 0;
            int scaled = centered * 32767 / halfRange;
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            if (scaled < short.MinValue) scaled = short.MinValue;
            return (short)scaled;
        }

        private static short ApplyDeadzone(short value)
        {
            int abs = Math.Abs(value);
            if (abs <= AXIS_OUTPUT_DEADZONE)
                return 0;

            int sign = value < 0 ? -1 : 1;
            int maxMagnitude = short.MaxValue;
            int range = maxMagnitude - AXIS_OUTPUT_DEADZONE;

            double normalized = (abs - AXIS_OUTPUT_DEADZONE) / (double)range;
            int rescaled = (int)Math.Round(normalized * maxMagnitude);
            return ClampToShort(sign * rescaled);
        }

        private static short FilterAxis(short previous, short current)
        {
            short target = ApplyDeadzone(current);
            int next = previous + (int)Math.Round((target - previous) * AXIS_SMOOTHING_ALPHA);
            return ClampToShort(next);
        }

        private static short ClampToShort(int value)
        {
            if (value > short.MaxValue) return short.MaxValue;
            if (value < short.MinValue) return short.MinValue;
            return (short)value;
        }

        public void Dispose()
        {
            try
            {
                _controller?.Disconnect();
                (_controller as IDisposable)?.Dispose();
                _client?.Dispose();
                _connected = false;
                _axisFilterInitialized = false;
                _filteredLeftX = 0;
                _filteredLeftY = 0;
                _filteredRightX = 0;
                _filteredRightY = 0;
            }
            catch { }
        }
    }
}


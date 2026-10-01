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

        // ── Stick conditioning ──────────────────────────────────────────────────
        // Per-device asymmetric normalisation (raw 12-bit -> signed-16 XInput),
        // persisted as %APPDATA%\Joycon2PC\calibration.json. The NS2 factory
        // defaults live on AxisCalibration (centre 1998, min 746, max 3249) and
        // replace the old hardcoded NS2_CENTRE / NS2_RANGE constants.
        private readonly StickCalibration _calibration = new();

        // One filter per physical stick. Radial deadzone (enter/exit hysteresis)
        // + error-feedback low-pass, both in the signed-16 output domain.
        //
        // THREADING: these are deliberately lock-free and thread-confined to the
        // output pump thread. Every UpdateFromState call must originate there
        // (see Joycon2PC.App.MainForm.OutputPumpLoopAsync).
        private readonly StickFilter _leftStickFilter = new();
        private readonly StickFilter _rightStickFilter = new();

        /// <summary>Live calibration profiles; a UI can drive Begin/Record/EndCapture on this.</summary>
        public StickCalibration Calibration => _calibration;

        // Deadzone applied in Xbox signed-short space (-32768..32767).
        // 6000 ≈ 18.3 % of full range — covers NS2 drift at rest and factory off-centre offsets.
        // Remainder is rescaled so full deflection still reaches ±32767.
        //
        // DO NOT remove, rename, repurpose or change the meaning of this constant:
        // JoyConVisualizerPanel reads it and re-derives the identical remap curve
        // so the on-screen stick dot never disagrees with the emulated output.
        // StickFilter.DefaultEnterRadius is deliberately the same value so the
        // filter's deflected gate and this rescale start at the same point.
        public const short AxisOutputDeadzone = 6000;

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
                // Best-effort: a missing / malformed calibration.json just leaves
                // the NS2 factory defaults in place.
                _calibration.Load();

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

        /// <summary>
        /// Writes one merged state to the virtual controller.
        ///
        /// Thread confinement: the StickFilter instances this touches are lock-free,
        /// so every call must come from the output pump thread.
        /// </summary>
        /// <param name="state">Merged Joy-Con state for this tick.</param>
        /// <param name="leftDeviceId">Device id that owns the left stick, for calibration lookup; null uses factory defaults.</param>
        /// <param name="rightDeviceId">Device id that owns the right stick, for calibration lookup; null uses factory defaults.</param>
        public void UpdateFromState(JoyconState state, string? leftDeviceId = null, string? rightDeviceId = null)
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
                // Pipeline: per-device asymmetric calibration normalise (12-bit ->
                // signed-16 XInput) → StickFilter (radial deadzone with
                // enter/exit hysteresis + error-feedback low-pass) → output
                // deadzone rescale. All three stages live in Joycon2PC.Core and are
                // covered by tests/Joycon2PC.Tests/StickFilterTests.cs.
                //
                // No integer EMA is used anywhere on this path: an un-remaindered
                // integer EMA sticks (state += (target-state)*7/20 rounds to 0 for
                // small residuals), which is what made the earlier smoothing attempt
                // get reverted. StickFilter carries the division remainder instead.
                var left = _leftStickFilter.Filter(
                    _calibration.Normalize(leftDeviceId, StickAxis.LeftX, state.LeftStickX),
                    _calibration.Normalize(leftDeviceId, StickAxis.LeftY, state.LeftStickY));
                var right = _rightStickFilter.Filter(
                    _calibration.Normalize(rightDeviceId, StickAxis.RightX, state.RightStickX),
                    _calibration.Normalize(rightDeviceId, StickAxis.RightY, state.RightStickY));

                c.SetAxisValue(Xbox360Axis.LeftThumbX,  StickFilter.RemapPastDeadzone(left.X, AxisOutputDeadzone));
                c.SetAxisValue(Xbox360Axis.LeftThumbY,  StickFilter.RemapPastDeadzone(left.Y, AxisOutputDeadzone));
                c.SetAxisValue(Xbox360Axis.RightThumbX, StickFilter.RemapPastDeadzone(right.X, AxisOutputDeadzone));
                c.SetAxisValue(Xbox360Axis.RightThumbY, StickFilter.RemapPastDeadzone(right.Y, AxisOutputDeadzone));

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

        // MapStick / ApplyDeadzone / ClampToShort used to live here. They had zero
        // test coverage and their logic is now split across two tested Core types:
        //   raw 12-bit -> signed-16 XInput : StickCalibration.AxisCalibration.Normalize
        //   deadzone hysteresis + low-pass : StickFilter.Filter
        //   output deadzone rescale        : StickFilter.RemapPastDeadzone
        // Keeping copies here would only give the pipeline two sources of truth.

        public void Dispose()
        {
            try
            {
                _controller?.Disconnect();
                (_controller as IDisposable)?.Dispose();
                _client?.Dispose();
                _connected = false;
            }
            catch { }
        }
    }
}


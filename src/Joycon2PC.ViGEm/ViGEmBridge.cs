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
    /// <summary>
    /// Pure predicate: "would writing <paramref name="next"/> to the virtual
    /// controller actually change anything the game can observe?"
    ///
    /// WHY THIS EXISTS
    /// ---------------
    /// The output pump ticks at 120 Hz but the Joy-Con 2 BLE link only delivers a
    /// fresh input report every ~60 ms (measured p95/p99 interval 60.0/60.1 ms,
    /// i.e. ~16.7 Hz). The pump used to call <see cref="ViGEmBridge.UpdateFromState"/>
    /// unconditionally on every tick, and UpdateFromState ended in an unconditional
    /// SubmitReport, so the game received 7.2 byte-identical reports per fresh
    /// sample. That is pure waste on the submit path, and it fed seven identical
    /// targets into the StickFilter low-pass per input sample, which made the
    /// filter emit a decaying 120 Hz ripple of its own on top of the 16.7 Hz
    /// signal (the filter advances 0.65, 0.42, 0.27, ... of the residual across
    /// those seven steps). Gating on change removes both problems at once.
    ///
    /// WHAT COUNTS AS "CHANGED"
    /// ------------------------
    /// Every field of <see cref="JoyconState"/> reaches the controller, so all five
    /// are compared: the four stick axes (they become LeftThumb/RightThumb via
    /// calibration → filter → deadzone remap) and the 32-bit button word (which
    /// covers the analogue triggers, the shoulders, the D-pad, everything). If a
    /// future field is added to JoyconState it must be added here too -- this is
    /// the single place that decides whether the controller gets written.
    ///
    /// COMPARISON IS BY VALUE, NEVER BY REFERENCE
    /// -----------------------------------------
    /// MergeDeviceStates allocates a fresh JoyconState on every pump tick, so
    /// reference equality is *always* false. A reference-based gate would look
    /// like it compiled fine and would silently disable the whole optimisation
    /// (every tick would submit). JoyconState is also a mutable class, so a
    /// caller that mutated an instance in place must still be detected -- hence
    /// value comparison in both directions.
    ///
    /// THREADING
    /// ---------
    /// Pure and static: no instance state, no locks, safe from any thread. It
    /// deliberately lives outside ViGEmBridge and mentions no ViGEm type so unit
    /// tests can exercise it without loading Nefarius.ViGEm.Client / ViGEmBus.dll.
    /// </summary>
    public static class ControllerReportGate
    {
        /// <summary>
        /// True when <paramref name="next"/> must be submitted, i.e. when there is
        /// no previously submitted state or it differs in any field that reaches
        /// the controller.
        /// </summary>
        public static bool HasChanged(JoyconState? previouslySubmitted, JoyconState next)
        {
            ArgumentNullException.ThrowIfNull(next);

            // Nothing submitted yet (fresh connect, or just after Dispose): the
            // controller has never seen any state, so the first sample must go out.
            if (previouslySubmitted == null) return true;

            return previouslySubmitted.Buttons     != next.Buttons
                || previouslySubmitted.LeftStickX   != next.LeftStickX
                || previouslySubmitted.LeftStickY   != next.LeftStickY
                || previouslySubmitted.RightStickX  != next.RightStickX
                || previouslySubmitted.RightStickY  != next.RightStickY;
        }

        /// <summary>
        /// Field-for-field copy, used to snapshot the baseline. The gate must not
        /// alias the caller's object: JoyconState is mutable, and the merged state
        /// is also handed to RequestInputDisplayRefresh, so a retained reference
        /// could be mutated out from under the baseline.
        /// </summary>
        public static JoyconState Copy(JoyconState source)
        {
            ArgumentNullException.ThrowIfNull(source);

            return new JoyconState
            {
                Buttons     = source.Buttons,
                LeftStickX  = source.LeftStickX,
                LeftStickY  = source.LeftStickY,
                RightStickX = source.RightStickX,
                RightStickY = source.RightStickY,
            };
        }
    }

    public class ViGEmBridge : IDisposable
    {
        private ViGEmClient? _client;
        private IXbox360Controller? _controller;
        private bool _connected = false;

        // ── Change gate baseline ─────────────────────────────────────────────────
        // The last state that was actually written to the virtual controller, as a
        // private snapshot (ControllerReportGate.Copy). Null means "nothing has been
        // submitted yet", which is the correct initial state and also the correct
        // state after Connect/Dispose.
        //
        // This lives here rather than in the App pump because the knowledge of
        // *what reaches the controller* belongs next to the code that writes it;
        // MainForm.OutputPumpLoopAsync cannot know that, for instance, that two
        // different button words can map to the same shoulder state.
        //
        // THREADING: written only from UpdateFromState, which has exactly one
        // caller -- the 120 Hz output pump (MainForm.OutputPumpLoopAsync). Same
        // confinement as the StickFilter fields below, so no lock is needed. Do
        // not add a second caller without adding synchronisation.
        private JoyconState? _lastSubmittedState;

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

        // ── Failure reporting ──────────────────────────────────────────────────
        // Connect() and UpdateFromState() both swallow their exceptions, so a
        // failure is invisible to the caller: Connect() returns void with no status
        // and UpdateFromState() returns void too. That used to be fine because the
        // bridge reported the reason with Console.WriteLine -- except
        // Joycon2PC.App is OutputType=WinExe, so it has no console attached and
        // every one of those lines was written to nowhere. A first-time user with
        // no ViGEmBus installed got a silent no-op. ConnectionFailed / UpdateFailed
        // below are the channel that actually reaches a human.
        //
        // THREADING: both events are raised synchronously on the thread that hits
        // the failure -- ConnectionFailed on whatever thread called Connect(),
        // UpdateFailed on the 120 Hz output pump. Subscribers that touch UI must
        // marshal themselves.
        private static readonly TimeSpan UPDATE_FAILURE_NOTIFY_INTERVAL = TimeSpan.FromSeconds(10);
        private DateTime _lastUpdateFailureUtc = DateTime.MinValue;
        private string? _lastUpdateFailureMessage;

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

        /// <summary>
        /// Fired when <see cref="Connect"/> could not attach to ViGEmBus.
        /// Parameter: the driver/OS exception message, e.g. "ViGEmBus is not
        /// installed" or "Access is denied". This is the only place a subscriber
        /// can learn *why* the virtual controller is missing, and for a first-time
        /// user it is the difference between an actionable next step and an app
        /// that looks broken. It may fire more than once if Connect() is retried.
        ///
        /// Raised synchronously on the calling thread inside Connect()'s catch, so
        /// it fires before Connect() returns -- check <see cref="IsConnected"/>
        /// after calling Connect() rather than assuming a return means success.
        /// </summary>
        public event Action<string>? ConnectionFailed;

        /// <summary>
        /// Fired when a live virtual controller rejects a report (SubmitReport,
        /// SetAxisValue, ...). Parameter: the exception message.
        ///
        /// Throttled on the bridge side: at most one notification per
        /// <see cref="UPDATE_FAILURE_NOTIFY_INTERVAL"/>, and only when the message
        /// changes, because the output pump can fail up to 120 times a second and an
        /// unthrottled event would bury the user-visible log. A successful submit
        /// clears the throttle so the next distinct failure is reported at once.
        /// </summary>
        public event Action<string>? UpdateFailed;

        /// <summary>
        /// True when a virtual controller is attached and reports are being
        /// accepted. <see cref="Connect"/> does not throw, so this -- not the mere
        /// fact that Connect() returned -- is the success test.
        /// </summary>
        public bool IsConnected => _connected;

        /// <summary>
        /// Message from the most recent <see cref="ConnectionFailed"/>, or null if
        /// Connect() has not failed since the last successful Connect().
        /// </summary>
        public string? LastError { get; private set; }

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
                // A new virtual controller has never seen any state, so drop the
                // gate baseline: the first pump tick after a (re)connect must
                // submit even if the merged state matches the pre-disconnect one.
                _lastSubmittedState = null;
                LastError = null;
                Console.WriteLine("[ViGEmBridge] Virtual Xbox 360 controller connected.");
            }
            catch (Exception ex)
            {
                // Kept for a developer running under a debugger (Console.WriteLine
                // is a no-op under OutputType=WinExe, which is why the event below
                // exists at all).
                Console.WriteLine($"[ViGEmBridge] Connect failed: {ex.Message}");
                _connected = false;
                LastError = ex.Message;
                ConnectionFailed?.Invoke(ex.Message);
            }
        }

        /// <summary>
        /// Writes one merged state to the virtual controller.
        ///
        /// Does nothing at all when the merged state is field-for-field identical
        /// to the last one actually submitted: see <see cref="ControllerReportGate"/>
        /// for why the pump rate and the report rate disagree, and why the gate is
        /// checked *before* the filters run (so the low-pass only ever sees fresh
        /// samples, and cannot manufacture a 120 Hz ripple out of a 16.7 Hz
        /// signal).
        ///
        /// Thread confinement: the StickFilter instances and the
        /// _lastSubmittedState baseline this touches are lock-free, so every call
        /// must come from the output pump thread.
        /// </summary>
        /// <param name="state">Merged Joy-Con state for this tick.</param>
        /// <param name="leftDeviceId">Device id that owns the left stick, for calibration lookup; null uses factory defaults.</param>
        /// <param name="rightDeviceId">Device id that owns the right stick, for calibration lookup; null uses factory defaults.</param>
        public void UpdateFromState(JoyconState state, string? leftDeviceId = null, string? rightDeviceId = null)
        {
            if (!_connected || _controller == null)
            {
                // Deliberately NOT raised as ConnectionFailed/UpdateFailed: the
                // pump starts before the 200 ms driver check has necessarily
                // finished, so "no device yet" is the normal startup state and this
                // line fires at 120 Hz until Connect() lands. The real reason we
                // have no device is reported exactly once, by Connect()'s catch.
                // If Connect() succeeded and the device vanished later, _connected
                // stays true and the failure surfaces through the catch below.
                Console.WriteLine($"[ViGEmBridge] (no device) LX={state.LeftStickX} LY={state.LeftStickY}");
                return;
            }

            // ── Change gate ──────────────────────────────────────────────────────
            // 120 Hz pump, ~16.7 Hz reports: without this the game receives 7.2
            // identical reports per fresh sample. Compared by value, never by
            // reference (MergeDeviceStates allocates a new JoyconState every tick,
            // so a reference compare would never suppress anything).
            if (!ControllerReportGate.HasChanged(_lastSubmittedState, state))
                return;

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
                // integer EMA sticks (state += (target-state)*num/den rounds to 0
                // for small residuals), which is what made the earlier smoothing
                // attempt get reverted. StickFilter carries the division remainder
                // instead. Its alpha (63/100) is tuned for the ~16.7 Hz report rate
                // and only makes sense because the change gate above stops the pump
                // from feeding this filter duplicate targets.
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

                // Baseline is recorded only after a successful submit, so a
                // transient SubmitReport failure is retried on the next tick
                // instead of being swallowed as "already up to date".
                _lastSubmittedState = ControllerReportGate.Copy(state);

                // The link is healthy again: drop the throttle so a later failure
                // is reported immediately instead of waiting out the window.
                _lastUpdateFailureUtc = DateTime.MinValue;
                _lastUpdateFailureMessage = null;
            }
            catch (Exception ex)
            {
                // Kept for a developer running under a debugger; the event below is
                // what a real user sees.
                Console.WriteLine($"[ViGEmBridge] Update failed: {ex.Message}");
                RaiseUpdateFailed(ex.Message);
            }
        }

        /// <summary>
        /// Throttled fan-out for <see cref="UpdateFailed"/>. See the field comment
        /// on UPDATE_FAILURE_NOTIFY_INTERVAL for why this cannot just invoke the
        /// event on every failure: UpdateFromState runs on the 120 Hz output pump.
        ///
        /// Thread confinement: pump thread only, same as the StickFilter fields
        /// and _lastSubmittedState, so no lock is needed.
        /// </summary>
        private void RaiseUpdateFailed(string message)
        {
            if (string.Equals(message, _lastUpdateFailureMessage, StringComparison.Ordinal))
            {
                if ((DateTime.UtcNow - _lastUpdateFailureUtc) < UPDATE_FAILURE_NOTIFY_INTERVAL)
                    return;
            }

            _lastUpdateFailureUtc = DateTime.UtcNow;
            _lastUpdateFailureMessage = message;
            UpdateFailed?.Invoke(message);
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
                // Nothing is submitted to a disposed controller, so the baseline
                // must not survive into a later Connect().
                _lastSubmittedState = null;
            }
            catch { }
        }
    }
}


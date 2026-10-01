using System;

namespace Joycon2PC.Core
{
    /// <summary>
    /// Analog-stick jitter filter: a fixed-point one-pole low-pass with error
    /// feedback, wrapped in a radial deadzone with dual-threshold hysteresis.
    ///
    /// DOMAIN
    /// ------
    /// Everything in this class lives in the signed 16-bit XInput domain
    /// (-32768..32767), NOT in the raw 12-bit hardware domain (0..4095).
    /// Filtering in the hardware domain quantises the filtered value to 1/1251 of
    /// full scale; in the output domain one LSB of internal state is exactly one
    /// LSB that the game would eventually see, so the low-pass has real authority
    /// near centre where the jitter lives.
    ///
    /// WHY ERROR FEEDBACK INSTEAD OF A NAIVE INTEGER EMA
    /// -------------------------------------------------
    /// The obvious filter is <c>state += (target - state) * alpha</c>. In integer
    /// arithmetic that truncates: with alpha = 7/20 a residual of 1 gives
    /// <c>(1 * 7) / 20 == 0</c>, so <c>state</c> never moves and the output sticks
    /// at a value that is not the target, forever. That is the exact disease that
    /// made a previous smoothing attempt get reverted (the old
    /// <c>previous=2, target=0 -> Math.Round(-0.44)=0</c> trap) and it is also the
    /// disease a naive integer EMA would reintroduce.
    ///
    /// The trap is a property of the <em>truncation</em>, not of any particular
    /// alpha: the current 63/100 traps just as hard on a residual of 1, since
    /// <c>(1 * 63) / 100 == 0</c> too. Any numerator/denominator pair has some
    /// residual that rounds to zero, which is precisely why the accumulator below
    /// is load-bearing and must not be "simplified" away.
    ///
    /// The fix is to carry the division remainder into the next step:
    ///
    ///     d     = (target - state) * alphaNum + rem
    ///     state += d / alphaDen        // C# truncates toward zero
    ///     rem    = d % alphaDen
    ///
    /// <c>rem</c> is precisely the information the truncation threw away. Carrying
    /// it forward means the error is only ever deferred, never rounded away, so a
    /// constant target is reached exactly and there is no (state, rem) pair for
    /// which the filter fails to converge. <c>rem</c> inherits the sign of the
    /// dividend under C# remainder rules and so may be negative; that is fine,
    /// the arithmetic still telescopes.
    ///
    /// RADIAL DEADZONE WITH HYSTERESIS
    /// --------------------------------
    /// The deadzone is evaluated on the 2D vector magnitude, not per axis. A
    /// per-axis box leaves a cross-shaped live region at the corners of the
    /// deadzone; a radial gate does not. Two thresholds split the input into
    /// three bands, and the emitted output is a pure function of which band the
    /// sample landed in:
    ///
    ///   Band 1  magnitude &lt;= ExitRadius   -> at rest: output is exactly 0
    ///   Band 2  ExitRadius &lt; magnitude &lt; EnterRadius
    ///                                      -> frozen: keep the last emitted
    ///                                         output, but keep advancing the
    ///                                         filter state so it stays warm
    ///   Band 3  magnitude &gt;= EnterRadius  -> deflected: filter runs, output
    ///                                         is emitted
    ///
    /// Because band 2 never emits, a stick hovering at the boundary produces one
    /// constant output instead of chattering between 0 and non-zero. The gap
    /// between the two thresholds is the hysteresis band: a stick has to travel
    /// 18% of full scale to engage and only 10% to disengage.
    ///
    /// RE-SEED ON ACTIVATION
    /// ---------------------
    /// Every entry into band 3 from band 1 or band 2 sets the state to the target
    /// directly (<c>state = target; rem = 0</c>) instead of ramping up from the
    /// stale zero. That is deliberate: a stick pushed just past EnterRadius has a
    /// target of only a few thousand counts, and ramping from zero at the low-pass
    /// alpha per step makes it visibly creep out of the centre for a sample or two
    /// ("stick lags when you nudge it"). Re-seeding on every entry also stops the
    /// emitted value from jumping when the stick leaves band 2 again, since the
    /// held output can be far away from the target at that point.
    ///
    /// SAMPLE RATE (why alpha is 63/100 and not 7/20)
    /// -------------------------------------------------
    /// A one-pole low-pass is only meaningful relative to the rate its input
    /// actually changes. Two facts pin that rate here:
    ///
    ///   1. The hardware only reports at ~16.7 Hz. A measured BLE capture gave
    ///      an input interval p95/p99 = 60.0/60.1 ms, i.e. T = 60 ms exactly.
    ///   2. The output pump ticks at 120 Hz, but ViGEmBridge.UpdateFromState
    ///      now gates on change (ControllerReportGate.HasChanged) and returns
    ///      before touching these filters when the merged state is unchanged.
    ///      So a sample that reaches the filter is a *fresh* one; duplicates
    ///      never arrive. The filter therefore runs at 16.7 Hz, not 120 Hz.
    ///
    /// Derivation of the default alpha:
    ///
    ///   Target: a time constant of tau = 60 ms = one input sample period, i.e.
    ///   a group delay strictly below one sample. That is the most smoothing a
    ///   causal filter can do while never adding more latency than the input
    ///   path already carries, and it is the reason for the choice rather than
    ///   "7/20 felt OK". Matching the discrete pole to the continuous decay:
    ///
    ///       alpha = 1 - exp(-T / tau)
    ///             = 1 - exp(-60 / 60)
    ///             = 1 - e^-1
    ///             = 1 - 0.367879
    ///             = 0.632121
    ///
    ///   Stored as the reduced fraction 63/100 (63/100 = 0.63, within 0.34% of
    ///   e^-1). A coarse fraction is safe here precisely because the error-
    ///   feedback accumulator carries the discarded remainder, so alpha does
    ///   not have to be finely quantised for the filter to converge.
    ///
    /// Sanity numbers for this alpha at T = 60 ms:
    ///   - cutoff fc = -ln(1-alpha) / (2*pi*T) = 0.9943/0.37699 = 2.64 Hz,
    ///     i.e. under fs/4, which is the highest cutoff a 16.7 Hz input can
    ///     actually represent;
    ///   - exact discrete ramp lag = T*(1-alpha)/alpha = 60*0.37/0.63 = 35.2 ms
    ///     = 0.59 samples (sub-sample, as intended);
    ///   - 95% settled after 3 samples = 180 ms.
    ///
    /// What the old 7/20 did at this rate, and why it was wrong here:
    ///   fc = 1.14 Hz and a ramp lag of 60*(13/20)/(7/20) = 111 ms, i.e. 1.86
    ///   sample periods of extra lag on top of the input path. Worse, 7/20 was
    ///   tuned for a 120 Hz filter that was actually being fed 7 identical
    ///   targets per input sample, which is what produced the decaying 120 Hz
    ///   ripple on the output (see the repeated-target note in
    ///   Filter). With duplicates no longer fed in, 63/100 is both smooth
    ///   enough and, if a duplicate ever did arrive, self-limiting: seven
    ///   repeats of one target leave (1-alpha)^7 = 0.095% of the step behind.
    ///
    /// AT REST
    /// -------
    /// While the output is 0 the accumulator is not run at all. Integrating
    /// resting noise into the state would slowly bias it away from centre and
    /// would defeat the re-seed above.
    ///
    /// THREADING
    /// ---------
    /// This class is deliberately lock-free and NOT thread-safe. One instance per
    /// physical stick, confined to the single output-pump thread (see
    /// MainForm.OutputPumpLoopAsync). Every call must come from that thread.
    /// </summary>
    public sealed class StickFilter
    {
        // Alpha as a reduced fraction: 63/100 == 0.63 ~= 1 - e^-1 == 0.632121.
        //
        // DERIVATION (see the SAMPLE RATE block in the class summary for the
        // full argument): the Joy-Con 2 BLE link reports at a measured
        // ~16.7 Hz (input interval p95/p99 = 60.0/60.1 ms, so T = 60 ms), and
        // ViGEmBridge gates UpdateFromState on change, so this filter is fed at
        // the report rate rather than the 120 Hz pump rate. Targeting a time
        // constant of tau = 60 ms -- one input sample, so the added group delay
        // stays sub-sample -- gives
        //
        //     alpha = 1 - exp(-T / tau) = 1 - exp(-1) = 0.632121
        //
        // rounded to 63/100. Equivalently: keep the filter's cutoff below
        // fs/4 = 4.2 Hz, because a 16.7 Hz input cannot represent more than
        // that anyway, and take the largest alpha whose pole still leaves the
        // ramp lag below one sample period (60*0.37/0.63 = 35 ms).
        //
        // The old value was 7/20 == 0.35, chosen for a 120 Hz filter. At the
        // real 16.7 Hz that is fc = 1.14 Hz with 111 ms (1.86 samples) of
        // added lag, and -- because the pump used to hand it seven identical
        // targets per input sample -- it emitted a decaying 120 Hz ripple of
        // its own on top of the 16.7 Hz signal. Do not "restore 7/20".
        public const int DefaultAlphaNum = 63;
        public const int DefaultAlphaDen = 100;

        // 6000 == 18.3% of full scale. Deliberately the same value as
        // Joycon2PC.ViGEm.ViGEmBridge.AxisOutputDeadzone so the gate that decides
        // "is the stick deflected" and the rescale that decides "how much output
        // for that deflection" start at the same point.
        public const int DefaultEnterRadius = 6000;

        // 3300 == 10.1% of full scale. The gap between Exit and Enter is the
        // hysteresis band; a stick parked anywhere inside it keeps its last
        // emitted value instead of flapping between 0 and non-zero.
        public const int DefaultExitRadius = 3300;

        private int _enterRadius = DefaultEnterRadius;
        private int _exitRadius = DefaultExitRadius;
        private int _alphaNum = DefaultAlphaNum;
        private int _alphaDen = DefaultAlphaDen;

        // Filter state, one accumulator per axis.
        private int _x;
        private int _y;
        private int _remX;
        private int _remY;

        // Last emitted output, held while frozen.
        private int _outX;
        private int _outY;

        // True while the stick is outside the enter radius (output is being
        // emitted). Cleared by BOTH the at-rest and the frozen band, so every
        // re-entry into the deflected region re-seeds onto the target.
        private bool _deflected;

        public StickFilter()
            : this(DefaultEnterRadius, DefaultExitRadius, DefaultAlphaNum, DefaultAlphaDen)
        {
        }

        /// <param name="enterRadius">Magnitude at (or above) which the stick counts as deflected and output is emitted.</param>
        /// <param name="exitRadius">Magnitude at (or below) which the stick counts as at rest and output is exactly 0. Must be &lt;= <paramref name="enterRadius"/>.</param>
        /// <param name="alphaNum">Low-pass numerator of the reduced fraction alphaNum/alphaDen.</param>
        /// <param name="alphaDen">Low-pass denominator; must be &gt; 0.</param>
        public StickFilter(int enterRadius, int exitRadius, int alphaNum, int alphaDen)
        {
            _alphaDen = alphaDen > 0 ? alphaDen : DefaultAlphaDen;
            _alphaNum = alphaNum >= 0 ? alphaNum : DefaultAlphaNum;
            EnterRadius = enterRadius;
            ExitRadius = exitRadius;
        }

        /// <summary>Magnitude at or above which the stick is considered deflected. Tunable.</summary>
        public int EnterRadius
        {
            get => _enterRadius;
            set
            {
                _enterRadius = Math.Max(0, value);
                // Keep the hysteresis band well-formed: Exit can never exceed Enter.
                if (_exitRadius > _enterRadius) _exitRadius = _enterRadius;
            }
        }

        /// <summary>Magnitude at or below which output is exactly 0. Tunable; clamped to EnterRadius.</summary>
        public int ExitRadius
        {
            get => _exitRadius;
            set => _exitRadius = Math.Clamp(value, 0, _enterRadius);
        }

        /// <summary>Low-pass alpha as a reduced fraction: <c>AlphaNum / AlphaDen</c>.</summary>
        public int AlphaNum => _alphaNum;

        /// <summary>Low-pass alpha denominator.</summary>
        public int AlphaDen => _alphaDen;

        /// <summary>True while the stick is outside the exit radius (output is being emitted).</summary>
        public bool IsDeflected => _deflected;

        /// <summary>Filter one sample. Returns the signed-16-bit output for X and Y.</summary>
        public (int X, int Y) Filter(int targetX, int targetY)
        {
            targetX = Clamp(targetX);
            targetY = Clamp(targetY);

            double magnitude = Math.Sqrt((double)targetX * targetX + (double)targetY * targetY);

            // ── Band 1: at rest (magnitude <= ExitRadius) ─────────────────────────
            if (magnitude <= _exitRadius)
            {
                // Output is exactly zero and the accumulator is NOT run: integrating
                // resting noise here would bias the state away from centre and make
                // the next excursion start from a wrong place. Leaving the state
                // alone is safe because activation re-seeds it.
                _deflected = false;
                _outX = 0;
                _outY = 0;
                return (_outX, _outY);
            }

            // ── Band 3: deflected (magnitude >= EnterRadius) ─────────────────────
            if (magnitude >= _enterRadius)
            {
                if (_deflected)
                {
                    Step(targetX, targetY);
                }
                else
                {
                    // Activation from "at rest / frozen": re-seed straight onto the
                    // target instead of ramping up from a stale zero. A stick pushed
                    // just past EnterRadius has a target of only a few thousand
                    // counts, and ramping from zero at the low-pass alpha per step
                    // makes it visibly creep out of the centre for a sample or two.
                    // Re-seeding is a one-sample step on a movement the user has
                    // only just started.
                    _x = targetX;
                    _y = targetY;
                    _remX = 0;
                    _remY = 0;
                }

                _deflected = true;
                _outX = _x;
                _outY = _y;
                return (_outX, _outY);
            }

            // ── Band 2: frozen (ExitRadius < magnitude < EnterRadius) ─────────────
            // Advance the filter state so it tracks the real input, but hold the
            // last emitted output. This is what removes the boundary chatter: a
            // stick parked anywhere inside the band produces exactly one constant
            // output instead of flapping between 0 and non-zero.
            _deflected = false;
            Step(targetX, targetY);
            return (_outX, _outY);
        }

        /// <summary>Drops all accumulated state and returns the filter to at-rest.</summary>
        public void Reset()
        {
            _x = 0;
            _y = 0;
            _remX = 0;
            _remY = 0;
            _outX = 0;
            _outY = 0;
            _deflected = false;
        }

        /// <summary>
        /// Re-scales the range that survives a deadzone back out to full scale,
        /// i.e. maps [deadzone, short.MaxValue] onto [0, short.MaxValue].
        ///
        /// TRADE-OFF (deliberate; do not "simplify" this away):
        ///   + It buys about 22% extra resolution in the first 18% of stick
        ///     travel, which is exactly where fine aiming lives.
        ///   - The slope changes discontinuously at |value| == deadzone: 1.22
        ///     output counts per input count below it, 1.00 above. A stick swept
        ///     very slowly across the boundary shows a small kink.
        ///   - It has to stay in lockstep with JoyConVisualizerPanel, which
        ///     re-derives this same curve from ViGEmBridge.AxisOutputDeadzone so
        ///     the on-screen dot never disagrees with the emulated output. That
        ///     cross-assembly dependency is why the constant stays public.
        ///
        /// The alternative (leave the surviving range un-rescaled) throws away
        /// 22% of the usable travel and makes small aim adjustments quantise to
        /// ~26 XInput counts near centre, which is worse than a kink.
        /// </summary>
        public static short RemapPastDeadzone(int value, int deadzone)
        {
            int magnitude = Math.Abs(value);
            if (magnitude <= deadzone) return 0;

            int sign = value < 0 ? -1 : 1;
            int range = short.MaxValue - deadzone;
            if (range <= 0) return (short)(sign * short.MaxValue);

            // 64-bit + round-half-up: no truncation bias at the low end.
            long rescaled = ((long)(magnitude - deadzone) * short.MaxValue + range / 2) / range;
            if (rescaled > short.MaxValue) rescaled = short.MaxValue;
            return (short)(sign * rescaled);
        }

        /// <summary>One error-feedback low-pass step, per axis.</summary>
        private void Step(int targetX, int targetY)
        {
            // long, not int, so an absurdly large alphaNum cannot overflow the
            // product. The stored state and remainder stay int per spec.
            long dX = ((long)targetX - _x) * _alphaNum + _remX;
            _x = Clamp(_x + (int)(dX / _alphaDen));
            _remX = (int)(dX % _alphaDen);

            long dY = ((long)targetY - _y) * _alphaNum + _remY;
            _y = Clamp(_y + (int)(dY / _alphaDen));
            _remY = (int)(dY % _alphaDen);
        }

        private static int Clamp(int value)
        {
            if (value > short.MaxValue) return short.MaxValue;
            if (value < short.MinValue) return short.MinValue;
            return value;
        }
    }
}
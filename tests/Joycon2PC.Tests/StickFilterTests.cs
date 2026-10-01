using Joycon2PC.Core;
using Xunit;

namespace Joycon2PC.Tests;

public class StickFilterTests
{
    // The measured reality this suite is written against: the Joy-Con 2 BLE link
    // delivers a fresh input report every 60 ms (measured p95/p99 interval
    // 60.0/60.1 ms => ~16.7 Hz). The 120 Hz output pump used to hand the filter
    // seven identical targets per fresh sample; ViGEmBridge now gates
    // UpdateFromState on change, so a sample that reaches the filter is fresh.
    // All sample counts below are therefore "16.7 Hz samples", i.e. 60 ms apart.
    private const int ReportIntervalMs = 60;

    // A filter with the deadzone collapsed to 0, so the low-pass accumulator itself
    // is what is under test: "at rest" then only means "target is exactly 0".
    // Uses the production alpha so these tests track the real tuning instead of
    // silently drifting away from it (the naive-EMA demonstration below keeps its
    // own historical 7/20 on purpose -- see its comment).
    private static StickFilter RawFilter()
        => new(enterRadius: 0, exitRadius: 0,
               alphaNum: StickFilter.DefaultAlphaNum, alphaDen: StickFilter.DefaultAlphaDen);

    private static StickFilter DeadzoneFilter()
        => new(enterRadius: 6000, exitRadius: 3300,
               alphaNum: StickFilter.DefaultAlphaNum, alphaDen: StickFilter.DefaultAlphaDen);

    // ─────────────────────────────────────────────────────────────────────────
    //  NO STICKING -- regression for the reverted smoothing attempt
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Filter_NaiveIntegerEmaSticks_WhichIsWhyErrorFeedbackExists()
    {
        // This test is the justification for the whole design, so it asserts both
        // halves of the comparison rather than only describing one in a comment.
        //
        // 1. The naive integer EMA `state += (target - state) * alphaNum / alphaDen`
        //    TRAPS at a non-zero residual. This is the regression: the previous
        //    (reverted) smoothing attempt had the same shape, via
        //    Math.Round(-0.44) == 0.
        // 2. The error-feedback form used by StickFilter converges exactly.
        //
        // 7/20 is kept here ON PURPOSE even though the production alpha is now
        // 63/100: it is the alpha the reverted attempt actually shipped, and the
        // point is that the trap is caused by the truncation, not by the tuning --
        // 63/100 traps identically on a residual of 1 ((1*63)/100 == 0).
        const int AlphaNum = 7;
        const int AlphaDen = 20;

        int naive = 2;
        for (int i = 0; i < 500; i++) naive += (1 - naive) * AlphaNum / AlphaDen;
        Assert.Equal(2, naive); // stuck forever at a value that is not the target

        var filter = RawFilter();
        for (int i = 0; i < 64; i++) filter.Filter(2, 0);
        Assert.Equal(2, filter.Filter(2, 0).X);

        int x = 0;
        for (int i = 0; i < 500; i++) x = filter.Filter(1, 0).X;
        Assert.Equal(1, x);
    }

    [Fact]
    public void Filter_SmallResidualStillConvergesExactly()
    {
        // A naive integer EMA, `state += (target - state) * 7 / 20`, truncates to 0
        // whenever the residual is below 3 counts. Parked at state = 3 with
        // target = 1 it would report 3 forever. That is the same disease as the
        // old `previous = 2, target = 0 -> Math.Round(-0.44) = 0` trap.
        // Error feedback must carry the discarded remainder forward and arrive.
        var filter = RawFilter();

        for (int i = 0; i < 64; i++) filter.Filter(3, 0);
        Assert.Equal(3, filter.Filter(3, 0).X); // state is parked at 3

        int x = 0;
        for (int i = 0; i < 500; i++) x = filter.Filter(1, 0).X;
        Assert.Equal(1, x);
    }

    [Fact]
    public void Filter_ConstantNonZeroTarget_ReachesTargetExactly()
    {
        var filter = RawFilter();

        int x = 0;
        for (int i = 0; i < 2000; i++) x = filter.Filter(12345, -23456).X;

        Assert.Equal(12345, x);
    }

    [Fact]
    public void Filter_ConstantNegativeTarget_ReachesTargetExactly()
    {
        var filter = RawFilter();

        int y = 0;
        for (int i = 0; i < 2000; i++) y = filter.Filter(-30000, -30000).Y;

        Assert.Equal(-30000, y);
    }

    [Fact]
    public void Filter_SlowRamp_TracksTargetWithoutDrift()
    {
        // A long ramp exercises the accumulator right around the truncation
        // boundary far more thoroughly than a single jump does.
        var filter = RawFilter();

        for (int target = 0; target <= 2000; target++)
        {
            for (int i = 0; i < 3; i++) filter.Filter(target, target);
        }

        // Let the lag close, then require an exact match.
        for (int i = 0; i < 64; i++) filter.Filter(2000, 2000);

        Assert.Equal(2000, filter.Filter(2000, 2000).X);
        Assert.Equal(2000, filter.Filter(2000, 2000).Y);
    }

    [Fact]
    public void Filter_AlternatingTargets_StaysBoundedAndConvergesAfterHold()
    {
        // Alternating between two nearby targets is exactly the case a low-pass
        // must NOT snap to: it correctly lags, so it may never reach either target
        // on a given sample. What it must never do is drift outside the range the
        // target visits, nor fail to converge once the target holds steady.
        var filter = RawFilter();

        int x = 0;
        for (int i = 0; i < 2000; i++)
        {
            x = filter.Filter(i % 2 == 0 ? 100 : 101, 0).X;
            Assert.InRange(x, 100, 101);
        }

        // Hold steady and it must land exactly, with no residue from the toggling.
        for (int i = 0; i < 500; i++) x = filter.Filter(101, 0).X;
        Assert.Equal(101, x);

        for (int i = 0; i < 500; i++) x = filter.Filter(100, 0).X;
        Assert.Equal(100, x);
    }

    [Fact]
    public void Filter_ReleasingStick_OutputIsExactlyZero()
    {
        // With a live deadzone the at-rest branch short-circuits to 0, which is the
        // guarantee the user actually sees: no residual offset at rest.
        var filter = DeadzoneFilter();

        for (int i = 0; i < 200; i++) filter.Filter(20000, 20000);
        Assert.NotEqual(0, filter.Filter(20000, 20000).X);

        for (int i = 0; i < 50; i++)
        {
            var released = filter.Filter(0, 0);
            Assert.Equal(0, released.X);
            Assert.Equal(0, released.Y);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  DEADZONE HYSTERESIS
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Filter_InsideExitRadius_OutputIsExactlyZero()
    {
        var filter = DeadzoneFilter();

        // Axial, right on the threshold.
        var axial = filter.Filter(3300, 0);
        Assert.Equal(0, axial.X);
        Assert.Equal(0, axial.Y);

        // Radial: 2000/2000 has magnitude ~2828, still inside the radius.
        var radial = filter.Filter(2000, 2000);
        Assert.Equal(0, radial.X);
        Assert.Equal(0, radial.Y);
    }

    [Fact]
    public void Filter_OscillatingInsideHysteresisBand_OutputDoesNotOscillate()
    {
        var filter = DeadzoneFilter();

        // Get genuinely deflected first, so there is a non-zero output to hold.
        var deflected = filter.Filter(20000, 0);
        Assert.Equal(20000, deflected.X); // re-seed lands on the target in one step

        // Now straddle the band: every sample has 3300 < magnitude < 6000.
        var outputs = new List<int>();
        for (int i = 0; i < 400; i++)
        {
            int radius = (i % 2 == 0) ? 3400 : 5800;
            outputs.Add(filter.Filter(radius, 0).X);
        }

        // Band 2 never emits, so every sample must report the held value. A
        // single-threshold gate would alternate between 20000 and something else.
        Assert.All(outputs, v => Assert.Equal(20000, v));
    }

    [Fact]
    public void Filter_ReenteringDeflectedBand_ReseedsInsteadOfJumping()
    {
        var filter = DeadzoneFilter();

        Assert.Equal(20000, filter.Filter(20000, 0).X);

        // Dip into the frozen band; the output is held at 20000 while the state
        // runs down towards 5000.
        Assert.Equal(20000, filter.Filter(5000, 0).X);

        // Come back out. Because every entry into the deflected band re-seeds,
        // the output lands on the target instead of jumping to wherever the
        // filter drifted while frozen.
        Assert.Equal(24000, filter.Filter(24000, 0).X);
    }

    [Fact]
    public void Filter_BelowExitRadiusAfterDeflection_OutputDropsToExactlyZero()
    {
        var filter = DeadzoneFilter();

        for (int i = 0; i < 100; i++) filter.Filter(25000, -25000);
        Assert.NotEqual(0, filter.Filter(25000, -25000).X);

        // Releasing the stick: once the magnitude is back inside ExitRadius the
        // output is hard 0, with no residue from the last filtered value.
        var released = filter.Filter(0, 0);
        Assert.Equal(0, released.X);
        Assert.Equal(0, released.Y);
    }

    [Fact]
    public void Filter_SmallRestingNoise_OutputStaysAtZero()
    {
        // The most common real-world case: a stick at rest with a few counts of
        // noise. It must never produce a non-zero output.
        var filter = DeadzoneFilter();

        for (int i = 0; i < 1000; i++)
        {
            int jitter = (i % 2 == 0) ? 1980 : 2016; // +/- 18 counts around centre
            var out0 = filter.Filter(jitter, 1998);
            Assert.Equal(0, out0.X);
            Assert.Equal(0, out0.Y);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  RE-SEED ON ACTIVATION
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Filter_LeavingDeadzone_ReachesTargetInOneStep()
    {
        var filter = DeadzoneFilter();

        // Sitting at rest.
        Assert.Equal(0, filter.Filter(0, 0).X);

        // Nudge just past the enter radius: the output must be the target itself on
        // the FIRST sample, not a ramp. Ramping from zero is what produced the
        // "stick slowly creeps out of the centre" feel.
        var first = filter.Filter(6500, -6500);
        Assert.Equal(6500, first.X);
        Assert.Equal(-6500, first.Y);

        // And it stays put while the target is constant (no overshoot from a stale
        // state).
        Assert.Equal(6500, filter.Filter(6500, -6500).X);
    }

    [Fact]
    public void Filter_ExitRadiusIsClampedToEnterRadius()
    {
        var filter = new StickFilter(
            enterRadius: 4000, exitRadius: 9000,
            alphaNum: StickFilter.DefaultAlphaNum, alphaDen: StickFilter.DefaultAlphaDen);
        Assert.Equal(4000, filter.EnterRadius);
        Assert.Equal(4000, filter.ExitRadius);
    }

    [Fact]
    public void Filter_Defaults_MatchTheDocumentedConstants()
    {
        var filter = new StickFilter();
        Assert.Equal(StickFilter.DefaultEnterRadius, filter.EnterRadius);
        Assert.Equal(StickFilter.DefaultExitRadius, filter.ExitRadius);
        Assert.Equal(StickFilter.DefaultAlphaNum, filter.AlphaNum);
        Assert.Equal(StickFilter.DefaultAlphaDen, filter.AlphaDen);
        Assert.False(filter.IsDeflected);
    }

    [Fact]
    public void Filter_DefaultAlpha_IsTheRetuned16_7HzValue()
    {
        // UPDATED (was: Assert.Equal(7, ...) / Assert.Equal(20, ...)).
        // This test did encode the old constant, and it encoded it deliberately
        // -- pinning the tuning so nobody could quietly retune the feel without
        // updating a test. The value is now pinned again, with the derivation it
        // is supposed to satisfy:
        //
        //   T  = 60 ms   (measured BLE input interval, ~16.7 Hz)
        //   tau = 60 ms  (target time constant = one input sample, so the added
        //                  group delay stays sub-sample)
        //   alpha = 1 - exp(-T/tau) = 1 - e^-1 = 0.632121  ->  stored as 63/100
        //
        // 63/100 = 0.63 is 0.34% off the exact pole; the error-feedback
        // accumulator is what makes a coarse fraction acceptable here.
        Assert.Equal(63, StickFilter.DefaultAlphaNum);
        Assert.Equal(100, StickFilter.DefaultAlphaDen);

        double alpha = (double)StickFilter.DefaultAlphaNum / StickFilter.DefaultAlphaDen;
        double target = 1.0 - Math.Exp(-1.0);
        Assert.True(Math.Abs(alpha - target) < 0.005, $"alpha {alpha} is not within 0.005 of 1 - e^-1 = {target}");

        // Cutoff must stay under fs/4 -- a 16.7 Hz input cannot represent more,
        // and anything above that is just extra lag bought with no extra fidelity.
        double cutoffHz = -Math.Log(1.0 - alpha) / (2.0 * Math.PI * (ReportIntervalMs / 1000.0));
        Assert.True(cutoffHz < (1000.0 / ReportIntervalMs) / 4.0, $"cutoff {cutoffHz:F2} Hz is too high for the sample rate");

        // And the whole point of the retune: the added ramp lag is sub-sample.
        // The old 7/20 gave 1.86 samples here.
        double rampLagSamples = (1.0 - alpha) / alpha;
        Assert.True(rampLagSamples < 1.0, $"ramp lag {rampLagSamples:F2} samples is not sub-sample");
    }

    [Fact]
    public void Filter_Reset_ReturnsToAtRest()
    {
        var filter = DeadzoneFilter();
        for (int i = 0; i < 100; i++) filter.Filter(25000, 25000);

        filter.Reset();

        Assert.False(filter.IsDeflected);
        Assert.Equal(0, filter.Filter(0, 0).X);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  RETUNED ALPHA -- the filter is a 16.7 Hz filter, not a 120 Hz one
    // ─────────────────────────────────────────────────────────────────────────
    //
    // Everything in this section feeds the filter at the measured BLE report
    // interval (60 ms => ~16.7 Hz), which is what ViGEmBridge's change gate now
    // guarantees: a sample that gets through the gate is a genuinely fresh one.

    /// <summary>
    /// Puts a RawFilter into the deflected band with its state exactly at
    /// <paramref name="seed"/>, so the next samples exercise the low-pass itself
    /// rather than the re-seed-on-activation shortcut.
    /// </summary>
    private static StickFilter DeflectedAt(int seedX, int seedY = 0)
    {
        var filter = RawFilter();
        var out0 = filter.Filter(seedX, seedY);
        Assert.Equal(seedX, out0.X); // band 3 re-seed lands on the target
        return filter;
    }

    [Fact]
    public void Filter_RetunedAlpha_SettlesWithinThreeSamples_OfAStepAt16_7Hz()
    {
        // 5000 -> 30000 is a near-full-scale jump (both ends inside the signed-16
        // domain, which is what the filter clamps to). alpha = 63/100 closes
        // 1 - 0.37^3 = 94.9% of it inside 3 samples = 180 ms at the measured 60 ms
        // interval. The bound below is 6% rather than 5% because 0.37^3 = 5.07%
        // lands marginally on the wrong side of an exact 95% claim.
        const int From = 5000;
        const int To = 30000;
        var filter = DeflectedAt(From);

        var outputs = new List<int> { From };
        for (int i = 0; i < 3; i++) outputs.Add(filter.Filter(To, 0).X);

        int after3 = outputs[^1];
        double errorPercent = 100.0 * Math.Abs(To - after3) / Math.Abs(To - From);
        Assert.True(errorPercent < 6.0,
            $"after 3 samples ({3 * ReportIntervalMs} ms) still {errorPercent:F2}% away: {after3}");

        // Monotone approach, never past the target.
        for (int i = 1; i < outputs.Count; i++)
        {
            Assert.True(outputs[i] > outputs[i - 1], $"not increasing at index {i}: {outputs[i - 1]} -> {outputs[i]}");
            Assert.True(outputs[i] <= To, $"overshot the target at index {i}: {outputs[i]} > {To}");
        }
    }

    [Fact]
    public void Filter_RetunedAlpha_SettlesWithinFiveSamples_AndThenLandsExactly()
    {
        // The other half of "the settled response is correct at the actual rate":
        // it must actually arrive, not merely approach. 1 - 0.37^5 = 99.31%.
        // Error feedback then guarantees exact convergence, so 100 samples is far
        // more than enough.
        const int From = 5000;
        const int To = 30000;
        var filter = DeflectedAt(From);

        int x = From;
        for (int i = 0; i < 5; i++) x = filter.Filter(To, 0).X;

        double errorPercent = 100.0 * Math.Abs(To - x) / Math.Abs(To - From);
        Assert.True(errorPercent < 1.0,
            $"after 5 samples ({5 * ReportIntervalMs} ms) still {errorPercent:F2}% away: {x}");

        for (int i = 0; i < 100; i++) x = filter.Filter(To, 0).X;
        Assert.Equal(To, x);
    }

    [Fact]
    public void Filter_RetunedAlpha_StepInputNeverOvershoots()
    {
        // A one-pole cannot overshoot on its own, but the integer accumulator and
        // the carried remainder are where an overshoot would actually be
        // introduced. Sweep both step directions on both axes.
        var steps = new[]
        {
            (From: 32767,  To: -32768),
            (From: -32768, To: 32767),
            (From: 20000,  To: -20000),
            (From: 30000,  To: -30000),
            (From: 1,      To: 30000),
        };

        foreach (var (From, To) in steps)
        {
            var filter = DeflectedAt(From, From);
            int low = Math.Min(From, To);
            int high = Math.Max(From, To);

            for (int i = 0; i < 200; i++)
            {
                var out0 = filter.Filter(To, To);
                Assert.InRange(out0.X, low, high);
                Assert.InRange(out0.Y, low, high);
            }

            // ...and it still arrives exactly.
            Assert.Equal(To, filter.Filter(To, To).X);
            Assert.Equal(To, filter.Filter(To, To).Y);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  REGRESSION GUARD -- a repeated identical target is a no-op
    // ─────────────────────────────────────────────────────────────────────────
    //
    // This is what makes the change gate safe to put *in front of* the filter.
    // If the pump ever does hand the filter the same target twice, the filter must
    // not invent motion: it may only close the remaining gap toward the target,
    // never oscillate, and once it has landed it must be bit-stable.

    [Fact]
    public void Filter_RepeatedIdenticalTarget_ConvergesMonotonicallyAndThenIsBitStable()
    {
        const int From = 5000;
        const int To = 30000;
        var filter = DeflectedAt(From);

        var outputs = new List<int>();
        for (int i = 0; i < 400; i++) outputs.Add(filter.Filter(To, 0).X);

        // Converging phase: strictly increasing towards the target and never past
        // it. This is the assertion that rules out the old 120 Hz ripple, which was
        // non-monotonic across the seven repeats of a single input sample.
        for (int i = 1; i < outputs.Count; i++)
        {
            Assert.True(outputs[i] >= outputs[i - 1],
                $"output went backwards at repeat {i}: {outputs[i - 1]} -> {outputs[i]}");
            Assert.True(outputs[i] <= To, $"overshot the target at repeat {i}: {outputs[i]} > {To}");
        }

        Assert.Equal(To, outputs[^1]);

        // Settled phase: once it has landed, more identical samples change nothing.
        int settled = outputs[^1];
        for (int i = 0; i < 100; i++)
            Assert.Equal(settled, filter.Filter(To, 0).X);
    }

    [Fact]
    public void Filter_RepeatedIdenticalTarget_LeavesAlmostNoResidualAfterSevenRepeats()
    {
        // The exact regression for the ripple defect. The old pump handed the
        // filter seven identical targets per fresh input sample, so this is the
        // shape the filter has to survive. Residual after seven repeats of one
        // target is (1 - alpha)^7 of the step.
        //
        //   alpha = 63/100 -> 0.37^7 = 0.095%   (negligible)
        //   alpha =  7/20  -> 0.65^7 = 4.90%   (the ripple that was measured)
        //
        // Asserted as a comparison against a filter built with the old constant, so
        // the "before" number is produced by the same code path rather than by a
        // comment nobody can check.
        const int From = 5000;
        const int To = 30000;
        const double Step = To - From;

        int ResidualAfterSevenRepeats(int alphaNum, int alphaDen)
        {
            var filter = new StickFilter(enterRadius: 0, exitRadius: 0, alphaNum, alphaDen);
            Assert.Equal(From, filter.Filter(From, 0).X); // re-seed into the deflected band
            int x = From;
            for (int i = 0; i < 7; i++) x = filter.Filter(To, 0).X;
            return To - x;
        }

        int retuned = ResidualAfterSevenRepeats(StickFilter.DefaultAlphaNum, StickFilter.DefaultAlphaDen);
        int legacy = ResidualAfterSevenRepeats(alphaNum: 7, alphaDen: 20);

        Assert.True(100.0 * retuned / Step < 0.2,
            $"retuned residual after 7 repeats is {retuned} counts ({100.0 * retuned / Step:F3}% of the step), expected < 0.2%");
        Assert.True(100.0 * legacy / Step > 4.0,
            $"legacy 7/20 residual is only {legacy} counts ({100.0 * legacy / Step:F3}%); if this fails the 'old alpha' side of the comparison is no longer representative");
        Assert.True(retuned * 20 < legacy, "retuned residual should be far smaller than the legacy one");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  OUTPUT DEADZONE REMAP
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RemapPastDeadzone_AtOrBelowThreshold_IsZero()
    {
        Assert.Equal(0, StickFilter.RemapPastDeadzone(0, 6000));
        Assert.Equal(0, StickFilter.RemapPastDeadzone(6000, 6000));
        Assert.Equal(0, StickFilter.RemapPastDeadzone(-6000, 6000));
    }

    [Fact]
    public void RemapPastDeadzone_AboveThreshold_RestoresFullRange()
    {
        Assert.Equal(short.MaxValue, StickFilter.RemapPastDeadzone(short.MaxValue, 6000));
        Assert.Equal(-short.MaxValue, StickFilter.RemapPastDeadzone(short.MinValue, 6000));

        int previous = 0;
        for (int v = 6001; v <= short.MaxValue; v += 257)
        {
            int mapped = StickFilter.RemapPastDeadzone(v, 6000);
            Assert.True(mapped > previous, $"not monotonic at {v}");
            previous = mapped;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  CALIBRATION
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Calibration_UnknownDevice_ReturnsFactoryDefaults()
    {
        var calibration = new StickCalibration();

        var axis = calibration.GetAxis("no-such-device", StickAxis.LeftX);

        Assert.Equal(AxisCalibration.DefaultCenter, axis.Center);   // 1998
        Assert.Equal(AxisCalibration.DefaultMin, axis.Min);         // 746
        Assert.Equal(AxisCalibration.DefaultMax, axis.Max);         // 3249
        Assert.False(axis.IsCalibrated);
    }

    [Fact]
    public void Calibration_NormalizesAsymmetricallyAboveAndBelowCenter()
    {
        // Deliberately asymmetric: 900 counts below centre, 1500 above. A single
        // symmetric half-range would saturate one direction before the other.
        var profile = new AxisCalibration(center: 2000, min: 1100, max: 3500, isCalibrated: true);

        Assert.Equal(0, profile.Normalize(2000));

        // Above centre: (v - Center) / (Max - Center).
        Assert.Equal(32767, profile.Normalize(3500));
        Assert.Equal(16384, profile.Normalize(2750)); // exactly halfway
        Assert.Equal(8192, profile.Normalize(2375));  // one quarter

        // Below centre: (Center - v) / (Center - Min).
        Assert.Equal(-32767, profile.Normalize(1100));
        Assert.Equal(-16384, profile.Normalize(1550)); // exactly halfway
        Assert.Equal(-8192, profile.Normalize(1775));  // one quarter
    }

    [Fact]
    public void Calibration_DefaultProfileNormalizesToFullScaleAtTheEnds()
    {
        var profile = AxisCalibration.Default;

        Assert.Equal(0, profile.Normalize(AxisCalibration.DefaultCenter));
        Assert.Equal(short.MaxValue, profile.Normalize(AxisCalibration.DefaultMax));
        Assert.Equal(-short.MaxValue, profile.Normalize(AxisCalibration.DefaultMin));

        // One raw count above centre is no longer quantised to zero: this is the
        // truncation bug the old `centered * 32767 / halfRange` had.
        Assert.NotEqual(0, profile.Normalize(AxisCalibration.DefaultCenter + 1));
        Assert.NotEqual(0, profile.Normalize(AxisCalibration.DefaultCenter - 1));
    }

    [Fact]
    public void Calibration_NormalizeUsesTheAxisSpecificProfile()
    {
        var calibration = new StickCalibration();
        const string device = "abc123";

        Assert.True(calibration.SetAxis(device, StickAxis.LeftX, center: 2000, min: 1100, max: 3500));
        Assert.True(calibration.SetAxis(device, StickAxis.LeftY, center: 1900, min: 900, max: 3300));

        Assert.Equal(32767, calibration.Normalize(device, StickAxis.LeftX, 3500));
        Assert.Equal(32767, calibration.Normalize(device, StickAxis.LeftY, 3300));

        // The two axes really are independent.
        Assert.Equal(0, calibration.Normalize(device, StickAxis.LeftX, 2000));
        Assert.Equal(0, calibration.Normalize(device, StickAxis.LeftY, 1900));
    }

    [Fact]
    public void Calibration_DegenerateProfile_FallsBackToDefaults()
    {
        var calibration = new StickCalibration();
        const string device = "degenerate";

        // Max <= Center: SetAxis reports failure and GetAxis keeps handing out the
        // factory defaults, so Normalize never divides by a non-positive range.
        Assert.False(calibration.SetAxis(device, StickAxis.RightX, center: 1998, min: 746, max: 1998));

        var axis = calibration.GetAxis(device, StickAxis.RightX);
        Assert.Equal(AxisCalibration.DefaultCenter, axis.Center);
        Assert.Equal(AxisCalibration.DefaultMin, axis.Min);
        Assert.Equal(AxisCalibration.DefaultMax, axis.Max);

        // And normalization still behaves.
        Assert.Equal(0, calibration.Normalize(device, StickAxis.RightX, 1998));
        Assert.Equal(32767, calibration.Normalize(device, StickAxis.RightX, AxisCalibration.DefaultMax));
    }

    [Fact]
    public void Calibration_ProfileOutOfRawRange_FallsBackToDefaults()
    {
        var calibration = new StickCalibration();
        const string device = "out-of-range";

        // Centre above the 12-bit maximum.
        Assert.False(calibration.SetAxis(device, StickAxis.LeftY, center: 5000, min: 1000, max: 4000));
        Assert.Equal(AxisCalibration.DefaultCenter, calibration.GetAxis(device, StickAxis.LeftY).Center);

        // Negative minimum.
        Assert.False(calibration.SetAxis(device, StickAxis.LeftY, center: 2000, min: -5, max: 3500));
        Assert.Equal(AxisCalibration.DefaultMin, calibration.GetAxis(device, StickAxis.LeftY).Min);

        // Center <= Min.
        Assert.False(calibration.SetAxis(device, StickAxis.LeftY, center: 1000, min: 1000, max: 3500));
        Assert.Equal(AxisCalibration.DefaultCenter, calibration.GetAxis(device, StickAxis.LeftY).Center);
    }

    [Fact]
    public void Calibration_CaptureCommitsEverySweptAxis()
    {
        var calibration = new StickCalibration();
        const string device = "swept";

        calibration.BeginCapture(device);
        for (int v = 800; v <= 3200; v += 10)
        {
            calibration.RecordSample(device, v, v + 100, v + 200, v + 300);
        }

        // Centre comes from the frame the caller saw at rest, NOT from any single
        // swept sample -- that is the joycon2cpp bug being avoided.
        Assert.True(calibration.EndCapture(device, restLX: 2000, restLY: 2100, restRX: 2200, restRY: 2300));

        Assert.Equal(2000, calibration.GetAxis(device, StickAxis.LeftX).Center);
        Assert.Equal(800, calibration.GetAxis(device, StickAxis.LeftX).Min);
        Assert.Equal(3200, calibration.GetAxis(device, StickAxis.LeftX).Max);

        Assert.Equal(2100, calibration.GetAxis(device, StickAxis.LeftY).Center);
        Assert.Equal(2200, calibration.GetAxis(device, StickAxis.RightX).Center);
        Assert.Equal(2300, calibration.GetAxis(device, StickAxis.RightY).Center);

        Assert.All(
            new[] { StickAxis.LeftX, StickAxis.LeftY, StickAxis.RightX, StickAxis.RightY },
            axis => Assert.True(calibration.GetAxis(device, axis).IsCalibrated));
    }

    [Fact]
    public void Calibration_UnsweptAxis_StaysOnFactoryDefaults()
    {
        var calibration = new StickCalibration();
        const string device = "partial";

        calibration.BeginCapture(device);
        for (int v = 800; v <= 3200; v += 10)
        {
            calibration.RecordSample(device, v, 1998, 1998, 1998);
        }

        // The three unswept axes never moved, so their extrema do not strictly
        // bracket the rest value: EndCapture reports incomplete.
        Assert.False(calibration.EndCapture(device, restLX: 2000, restLY: 1998, restRX: 1998, restRY: 1998));

        Assert.Equal(2000, calibration.GetAxis(device, StickAxis.LeftX).Center);
        Assert.Equal(800, calibration.GetAxis(device, StickAxis.LeftX).Min);
        Assert.Equal(3200, calibration.GetAxis(device, StickAxis.LeftX).Max);

        Assert.Equal(AxisCalibration.DefaultCenter, calibration.GetAxis(device, StickAxis.LeftY).Center);
        Assert.False(calibration.GetAxis(device, StickAxis.LeftY).IsCalibrated);
    }

    [Fact]
    public void Calibration_RecordSampleWithoutCapture_IsIgnored()
    {
        var calibration = new StickCalibration();
        calibration.RecordSample("nobody", 100, 100, 100, 100);
        Assert.False(calibration.EndCapture("nobody", 100, 100, 100, 100));
    }

    [Fact]
    public void Calibration_SaveThenLoad_RoundTrips()
    {
        string dir = Path.Combine(Path.GetTempPath(), "joycon2pc-cal-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, StickCalibration.FileName);

        try
        {
            var writer = new StickCalibration(path);
            Assert.True(writer.SetAxis("dev-l", StickAxis.LeftX, center: 2100, min: 900, max: 3300));
            Assert.True(writer.TrySave());

            var reader = new StickCalibration(path);
            Assert.True(reader.Load());

            var axis = reader.GetAxis("dev-l", StickAxis.LeftX);
            Assert.Equal(2100, axis.Center);
            Assert.Equal(900, axis.Min);
            Assert.Equal(3300, axis.Max);
            Assert.True(axis.IsCalibrated);

            // Untouched axes stay on the factory defaults after a round trip.
            Assert.Equal(AxisCalibration.DefaultCenter, reader.GetAxis("dev-l", StickAxis.LeftY).Center);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Calibration_DefaultPath_IsUnderAppData()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Joycon2PC",
            "calibration.json");

        Assert.Equal(expected, StickCalibration.DefaultPath());
    }

    [Fact]
    public void Calibration_LoadMissingFile_ReturnsFalseAndKeepsDefaults()
    {
        var calibration = new StickCalibration(
            Path.Combine(Path.GetTempPath(), "joycon2pc-missing-" + Guid.NewGuid().ToString("N"), "calibration.json"));

        Assert.False(calibration.Load());
        Assert.Equal(AxisCalibration.DefaultCenter, calibration.GetAxis("dev", StickAxis.LeftX).Center);
    }

    [Fact]
    public void Calibration_LoadMalformedFile_ReturnsFalseAndKeepsDefaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "joycon2pc-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, StickCalibration.FileName);
        File.WriteAllText(path, "{ this is not json ");

        try
        {
            var calibration = new StickCalibration(path);
            Assert.False(calibration.Load());
            Assert.Equal(AxisCalibration.DefaultCenter, calibration.GetAxis("dev", StickAxis.LeftX).Center);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
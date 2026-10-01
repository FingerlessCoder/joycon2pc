using Joycon2PC.Core;
using Xunit;

namespace Joycon2PC.Tests;

public class StickFilterTests
{
    // A filter with the deadzone collapsed to 0, so the low-pass accumulator itself
    // is what is under test: "at rest" then only means "target is exactly 0".
    private static StickFilter RawFilter()
        => new(enterRadius: 0, exitRadius: 0, alphaNum: 7, alphaDen: 20);

    private static StickFilter DeadzoneFilter()
        => new(enterRadius: 6000, exitRadius: 3300, alphaNum: 7, alphaDen: 20);

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
        Assert.Equal(7, filter.AlphaNum);
        Assert.Equal(20, filter.AlphaDen);
        Assert.False(filter.IsDeflected);
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
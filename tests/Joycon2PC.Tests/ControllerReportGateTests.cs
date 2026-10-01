using Joycon2PC.Core;
using Joycon2PC.ViGEm;
using Xunit;

namespace Joycon2PC.Tests;

/// <summary>
/// Tests for the output-pump change gate.
///
/// THE DEFECT
/// -----------
/// The output pump runs at 120 Hz but the Joy-Con 2 only delivers a fresh BLE
/// input report every ~60 ms (measured p95/p99 interval 60.0/60.1 ms => ~16.7 Hz).
/// UpdateFromState used to be called unconditionally and ended in an
/// unconditional SubmitReport, so the game received 7.2 byte-identical reports per
/// genuinely fresh sample.
///
/// WHY THE TESTS ARE STRUCTURED THIS WAY
/// -------------------------------------
/// A ViGEmBridge cannot be constructed in a unit test: Connect() needs a live
/// ViGEmBus, and that is not available on a build agent. So the gate itself is
/// tested directly through the pure predicate
/// <see cref="ControllerReportGate.HasChanged"/>, which deliberately mentions no
/// ViGEm type and therefore needs no bus. The two "pump" tests below then drive
/// that predicate through a miniature of the pump loop -- 120 Hz ticks over a
/// 16.7 Hz report stream -- and count "submits". That mirrors the production
/// structure (gate first, write only when the gate opens) without pretending to be
/// an integration test against the driver.
///
/// THREAD SAFETY NOTE
/// ------------------
/// ControllerReportGate is pure and static: no instance state, no locks. The
/// _lastSubmittedState baseline that the bridge actually keeps is written only
/// from UpdateFromState, whose single caller is the output pump thread -- the
/// same confinement the StickFilter instances already rely on.
/// </summary>
public class ControllerReportGateTests
{
    private static JoyconState State(
        uint buttons = 0,
        int lx = 1998, int ly = 1998,
        int rx = 1998, int ry = 1998)
        => new()
        {
            Buttons = buttons,
            LeftStickX = lx,
            LeftStickY = ly,
            RightStickX = rx,
            RightStickY = ry,
        };

    // ─────────────────────────────────────────────────────────────────────────
    //  PURE PREDICATE
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HasChanged_NoBaseline_AlwaysSubmits()
    {
        // Fresh connect, or just after Dispose(): the controller has never seen a
        // report, so the very first sample has to go out even if the stick is at
        // rest. Without this the app could come up with a dead controller.
        Assert.True(ControllerReportGate.HasChanged(null, State()));
    }

    [Fact]
    public void HasChanged_NullBaselineIsIndependentOfValues()
    {
        Assert.True(ControllerReportGate.HasChanged(null, State(buttons: 0xFFFF, lx: 0, ly: -32768, rx: 32767, ry: 1)));
        Assert.True(ControllerReportGate.HasChanged(null, State()));
    }

    [Fact]
    public void HasChanged_IdenticalValues_ReportsNoChange()
    {
        // Deliberately two DIFFERENT instances with equal contents. MergeDeviceStates
        // allocates a fresh JoyconState on every pump tick, so a reference-based
        // comparison would report "changed" here and silently disable the whole
        // optimisation (every one of the 7 ticks per input sample would submit).
        var previouslySubmitted = State(buttons: 0x0F, lx: 2000, ly: 1990, rx: 3000, ry: 3000);
        var next = State(buttons: 0x0F, lx: 2000, ly: 1990, rx: 3000, ry: 3000);

        Assert.NotSame(previouslySubmitted, next);
        Assert.False(ControllerReportGate.HasChanged(previouslySubmitted, next));
    }

    [Fact]
    public void HasChanged_EveryRepeatedCopyOfTheSameState_ReportsNoChange()
    {
        // Exactly the production shape: the pump re-merges from scratch every tick
        // and hands the bridge an equal-but-distinct object seven or eight times.
        JoyconState? baseline = null;
        int suppressed = 0;
        int submitted = 0;

        for (int tick = 0; tick < 40; tick++)
        {
            var merged = State(buttons: 0x1234, lx: 1990, ly: 2005, rx: 1998, ry: 1998);
            if (ControllerReportGate.HasChanged(baseline, merged))
            {
                baseline = ControllerReportGate.Copy(merged);
                submitted++;
            }
            else
            {
                suppressed++;
            }
        }

        Assert.Equal(1, submitted);
        Assert.Equal(39, suppressed);
    }

    [Theory]
    [InlineData("LeftStickX")]
    [InlineData("LeftStickY")]
    [InlineData("RightStickX")]
    [InlineData("RightStickY")]
    [InlineData("Buttons")]
    public void HasChanged_SingleChangedField_ReportsChange(string field)
    {
        var baseline = State(buttons: 0x0F, lx: 2000, ly: 1990, rx: 3000, ry: 3000);

        JoyconState changed = field switch
        {
            "LeftStickX" => State(buttons: 0x0F, lx: 2001, ly: 1990, rx: 3000, ry: 3000),
            "LeftStickY" => State(buttons: 0x0F, lx: 2000, ly: 1989, rx: 3000, ry: 3000),
            "RightStickX" => State(buttons: 0x0F, lx: 2000, ly: 1990, rx: 3001, ry: 3000),
            "RightStickY" => State(buttons: 0x0F, lx: 2000, ly: 1990, rx: 3000, ry: 2999),
            // A single button bit is enough: the word is what feeds the trigger /
            // shoulder / D-pad / face-button mapping in ViGEmBridge.
            "Buttons" => State(buttons: 0x0F ^ 0x0001, lx: 2000, ly: 1990, rx: 3000, ry: 3000),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.True(ControllerReportGate.HasChanged(baseline, changed));
    }

    [Fact]
    public void HasChanged_OneRawStickCount_IsEnoughToResubmit()
    {
        // One raw 12-bit count is ~26 XInput counts after normalisation, so the
        // gate must not be coarser than that.
        var baseline = State(lx: 2000);
        Assert.True(ControllerReportGate.HasChanged(baseline, State(lx: 2001)));
        Assert.True(ControllerReportGate.HasChanged(baseline, State(lx: 1999)));
        Assert.False(ControllerReportGate.HasChanged(baseline, State(lx: 2000)));
    }

    [Fact]
    public void HasChanged_SameInstanceMutatedInPlace_IsStillDetected()
    {
        // JoyconState is a mutable class and the merged state is also handed to
        // RequestInputDisplayRefresh, so a baseline that aliased the caller's
        // object could be mutated underneath itself and go undetected. Value
        // comparison (not reference comparison) is what prevents that; this is the
        // test that pins the "never by reference" rule from the other direction.
        var state = State(lx: 2000, ly: 1990);
        var baseline = ControllerReportGate.Copy(state);
        Assert.False(ControllerReportGate.HasChanged(baseline, state));

        state.LeftStickX = 2001;
        Assert.True(ControllerReportGate.HasChanged(baseline, state));
    }

    [Fact]
    public void Copy_ProducesAnIndependentSnapshot()
    {
        var source = State(buttons: 0xFF, lx: 111, ly: 222, rx: 333, ry: 444);
        var copy = ControllerReportGate.Copy(source);

        Assert.NotSame(source, copy);
        Assert.Equal(source.Buttons, copy.Buttons);
        Assert.Equal(source.LeftStickX, copy.LeftStickX);
        Assert.Equal(source.LeftStickY, copy.LeftStickY);
        Assert.Equal(source.RightStickX, copy.RightStickX);
        Assert.Equal(source.RightStickY, copy.RightStickY);

        source.Buttons = 0;
        source.RightStickY = 1998;
        Assert.Equal(0xFFu, copy.Buttons);
        Assert.Equal(444, copy.RightStickY);
    }

    [Fact]
    public void HasChanged_NullNext_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ControllerReportGate.HasChanged(State(), null!));
        Assert.Throws<ArgumentNullException>(() => ControllerReportGate.Copy(null!));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  PUMP-LEVEL BEHAVIOUR
    // ─────────────────────────────────────────────────────────────────────────
    //
    // Miniature of OutputPumpLoopAsync + ViGEmBridge.UpdateFromState: 120 Hz ticks
    // over a stream of 16.7 Hz BLE reports, counting how many reports the game
    // would actually receive.

    private const int PumpHz = 120;
    private const int ReportIntervalMs = 60;

    // 120 Hz * 60 ms = 7.2 pump ticks per fresh input sample, i.e. 7 duplicate
    // reports per real one (that is the "7.2 identical reports" in the defect
    // report, truncated to whole ticks). Integer division, deliberately: this is
    // a count of discrete ticks, not an average.
    private const int TicksPerReport = PumpHz * ReportIntervalMs / 1000; // 7

    /// <summary>
    /// Returns (submittedReports, ticksThatWereSuppressed).
    /// <paramref name="reports"/> is the per-tick merged state: one entry per BLE
    /// report, repeated TicksPerReport times, with MergeDeviceStates' fresh
    /// allocation on every tick.
    /// </summary>
    private static (int Submitted, int Suppressed) RunPump(IReadOnlyList<JoyconState> reports)
    {
        JoyconState? lastSubmitted = null;
        int submitted = 0;
        int suppressed = 0;

        for (int tick = 0; tick < reports.Count; tick++)
        {
            // MergeDeviceStates allocates a brand new object on every tick.
            var merged = ControllerReportGate.Copy(reports[tick]);

            if (!ControllerReportGate.HasChanged(lastSubmitted, merged))
            {
                suppressed++;
                continue;
            }

            lastSubmitted = ControllerReportGate.Copy(merged);
            submitted++;
        }

        return (submitted, suppressed);
    }

    private static List<JoyconState> PumpStream(params JoyconState[] reports)
    {
        var ticks = new List<JoyconState>();
        foreach (var report in reports)
        {
            for (int i = 0; i < TicksPerReport; i++)
                ticks.Add(ControllerReportGate.Copy(report));
        }
        return ticks;
    }

    [Fact]
    public void Pump_IdenticalStateAcrossTicks_SubmitsOnce()
    {
        // The headline case: the 7 pump ticks that carry one fresh BLE sample must
        // produce ONE submit, not seven. Before the gate this was 7 byte-identical
        // reports to the game per input sample.
        var ticks = PumpStream(State(lx: 2100, ly: 1900));
        Assert.Equal(TicksPerReport, ticks.Count);

        var (submitted, suppressed) = RunPump(ticks);

        Assert.Equal(1, submitted);
        Assert.Equal(TicksPerReport - 1, suppressed);
    }

    [Fact]
    public void Pump_OneChangedAxisAcrossTicks_SubmitsExactlyOnce()
    {
        // A single raw count of movement on one axis, held for 7 ticks, must open
        // the gate exactly once -- not zero times (a missed report, i.e. dropped
        // input) and not seven times (the defect).
        var ticks = PumpStream(
            State(lx: 2000),
            State(lx: 2001));
        Assert.Equal(2 * TicksPerReport, ticks.Count);

        var (submitted, suppressed) = RunPump(ticks);

        Assert.Equal(2, submitted); // one for the rest state, one for the moved state
        Assert.Equal(2 * TicksPerReport - 2, suppressed);
    }

    [Fact]
    public void Pump_ThirtySecondsOfAStillStick_Collapses3600TicksToOneSubmit()
    {
        // End-to-end shape of the measured defect. Ungated, a 30 s session at
        // 120 Hz submits 3600 reports; with the stick genuinely still, the game
        // only ever needs to be told once.
        const int CaptureSeconds = 30;
        const int Ticks = PumpHz * CaptureSeconds;      // 3600
        const int Reports = CaptureSeconds * 1000 / ReportIntervalMs; // 500

        var still = new List<JoyconState>(Ticks);
        for (int t = 0; t < Ticks; t++) still.Add(State()); // exactly at rest

        var (submitted, suppressed) = RunPump(still);
        Assert.Equal(1, submitted);
        Assert.Equal(Ticks - 1, suppressed);

        // With one count of BLE jitter per report the stick is NOT still, so the
        // gate must stay open for every fresh report -- a suppressed report would
        // be a dropped input, which is the failure mode that matters. The win is
        // that it is exactly one submit per report and never one per tick.
        var jittering = new List<JoyconState>(Reports * TicksPerReport);
        for (int r = 0; r < Reports; r++)
        {
            for (int i = 0; i < TicksPerReport; i++)
                jittering.Add(State(lx: 1998 + (r % 2)));
        }

        var (jitterSubmitted, jitterSuppressed) = RunPump(jittering);
        Assert.Equal(Reports, jitterSubmitted);
        Assert.Equal(jittering.Count - Reports, jitterSuppressed);
    }

    [Fact]
    public void Pump_ButtonPressAndRelease_EachProduceOneReport()
    {
        // Buttons are the part of the gate a player would notice most: a missed
        // submit is a dropped input, a duplicate submit is harmless but wasteful.
        // Both directions must pass the gate.
        var ticks = PumpStream(
            State(buttons: 0),
            State(buttons: (uint)SW2Button.A),
            State(buttons: (uint)SW2Button.A | (uint)SW2Button.ZR),
            State(buttons: 0));

        var (submitted, suppressed) = RunPump(ticks);

        Assert.Equal(4, submitted);
        Assert.Equal(4 * TicksPerReport - 4, suppressed);
    }
}

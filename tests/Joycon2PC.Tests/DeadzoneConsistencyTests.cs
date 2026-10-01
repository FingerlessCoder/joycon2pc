using Joycon2PC.Core;
using Joycon2PC.ViGEm;
using Xunit;

namespace Joycon2PC.Tests;

/// <summary>
/// Cross-assembly consistency guard.
///
/// ViGEmBridge.AxisOutputDeadzone is a single number that two different assemblies
/// depend on, and nothing but a comment ties them together:
///
///   Core (Joycon2PC.ViGEm -> Joycon2PC.Core)  StickFilter.DefaultEnterRadius
///       the magnitude gate that decides whether a stick is considered "deflected";
///       below it the filter output is frozen/held, at or above it the filter runs.
///
///   App (Joycon2PC.App)  JoyConVisualizerPanel
///       re-derives the identical remap curve so the on-screen stick dot matches
///       what the game receives.
///
/// If the two constants drift apart, the visualizer and the emulated output
/// silently disagree — the stick appears deflected while the game sees nothing,
/// or vice versa. These tests fail the build the moment they diverge.
///
/// Note: ViGEmBridge.AxisOutputDeadzone is a compile-time constant, so it is
/// inlined by the compiler and no ViGEmBridge instance is created — these tests
/// do not load ViGEmBus.dll or touch the virtual controller.
/// </summary>
public class DeadzoneConsistencyTests
{
    [Fact]
    public void StickFilterDefaultEnterRadius_EqualsViGEmAxisOutputDeadzone()
    {
        Assert.Equal((int)ViGEmBridge.AxisOutputDeadzone, StickFilter.DefaultEnterRadius);
    }

    [Fact]
    public void DefaultConstructedStickFilter_UsesTheSameDeflectionGate()
    {
        // Guards against the constant matching while the default-constructed
        // filter (what the app actually uses) is wired to a different radius.
        var filter = new StickFilter();

        Assert.Equal((int)ViGEmBridge.AxisOutputDeadzone, filter.EnterRadius);
    }

    [Fact]
    public void ExitRadius_RemainsBelowTheEnterGate()
    {
        // Hysteresis sanity: the release band must be strictly inside the
        // deflection gate, otherwise the filter would flap.
        Assert.True(StickFilter.DefaultExitRadius < StickFilter.DefaultEnterRadius);
        Assert.True(StickFilter.DefaultEnterRadius == (int)ViGEmBridge.AxisOutputDeadzone);
    }
}
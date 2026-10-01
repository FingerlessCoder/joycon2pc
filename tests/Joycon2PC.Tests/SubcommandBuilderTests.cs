using Joycon2PC.Core;
using Xunit;

namespace Joycon2PC.Tests;

public class SubcommandBuilderTests
{
    [Fact]
    public void BuildNS2Rumble_Off_ReturnsCorrectFormat()
    {
        var result = SubcommandBuilder.BuildNS2Rumble(on: false);

        Assert.Equal(8, result.Length);
        Assert.Equal(0x50, result[0]); // Command ID
        // Byte 1 should be 0x50 | counter
        Assert.Equal(0x00, result[2]); // Rumble off
    }

    [Fact]
    public void BuildNS2Rumble_On_ReturnsCorrectFormat()
    {
        var result = SubcommandBuilder.BuildNS2Rumble(on: true);

        Assert.Equal(8, result.Length);
        Assert.Equal(0x50, result[0]);
        Assert.Equal(0x01, result[2]); // Rumble on
    }

    [Fact]
    public void BuildNS2KeepAlive_ReturnsCorrectFormat()
    {
        var result = SubcommandBuilder.BuildNS2KeepAlive();

        Assert.Equal(8, result.Length);
        Assert.Equal(0x50, result[0]);
        Assert.Equal(0x00, result[2]); // No rumble
    }

    [Fact]
    public void BuildNS2KeepAlive_WithCounter_UsesProvidedCounter()
    {
        byte counter = 5;
        var result = SubcommandBuilder.BuildNS2KeepAlive(counter);

        Assert.Equal(8, result.Length);
        Assert.Equal(0x50, result[0]);
        Assert.Equal(0x50 | (counter & 0x0F), result[1]);
    }

    [Fact]
    public void BuildNS2PlayerLed_ReturnsCorrectFormat()
    {
        var result = SubcommandBuilder.BuildNS2PlayerLed(1);

        Assert.Equal(16, result.Length);
        Assert.Equal(0x30, result[0]); // Output report type
        Assert.Equal(0x01, result[8]); // Player 1 LED pattern
    }

    [Fact]
    public void BuildNS2PlayerLed_Player2_ReturnsCorrectPattern()
    {
        var result = SubcommandBuilder.BuildNS2PlayerLed(2);

        Assert.Equal(0x02, result[8]); // Player 2 LED pattern
    }

    [Fact]
    public void BuildNS2Sound_ReturnsCorrectFormat()
    {
        var result = SubcommandBuilder.BuildNS2Sound(0x04);

        Assert.Equal(16, result.Length);
        Assert.Equal(0x30, result[0]); // Output report type
        Assert.Equal(0x0A, result[3]); // Sound subcommand
        Assert.Equal(0x04, result[8]); // Preset ID
    }

    [Fact]
    public void BuildNS2SetInputMode_ReturnsCorrectFormat()
    {
        var result = SubcommandBuilder.BuildNS2SetInputMode(0x3F);

        Assert.Equal(16, result.Length);
        Assert.Equal(0x30, result[0]); // Output report type
        Assert.Equal(0x03, result[3]); // SetInputReportMode subcommand
        Assert.Equal(0x3F, result[8]); // Mode
    }

    [Fact]
    public void BuildNS2PlayerLedCompat_ReturnsCorrectFormat()
    {
        var result = SubcommandBuilder.BuildNS2PlayerLedCompat(1);

        Assert.Equal(16, result.Length);
        Assert.Equal(0x09, result[0]); // CMD 0x09
        Assert.Equal(0x91, result[1]); // Subcommand header
        Assert.Equal(0x07, result[3]); // SUB 0x07
    }

    [Fact]
    public void BuildNS2SoundCompat_ReturnsCorrectFormat()
    {
        var result = SubcommandBuilder.BuildNS2SoundCompat(0x04);

        Assert.Equal(16, result.Length);
        Assert.Equal(0x0A, result[0]); // CMD 0x0A
        Assert.Equal(0x91, result[1]); // Subcommand header
        Assert.Equal(0x02, result[3]); // SUB 0x02
    }

    [Fact]
    public void BuildNS2CustomInitCommands_ReturnsTwoCommands()
    {
        var result = SubcommandBuilder.BuildNS2CustomInitCommands();

        Assert.Equal(2, result.Length);
        Assert.Equal(12, result[0].Length); // First command is 12 bytes
        Assert.Equal(12, result[1].Length); // Second command is 12 bytes
    }

    [Fact]
    public void BuildRumble_ReturnsCorrectFormat()
    {
        var result = SubcommandBuilder.BuildRumble(0.5f, 0.5f);

        Assert.Equal(8, result.Length);
    }

    [Fact]
    public void BuildRumbleOff_ReturnsCorrectFormat()
    {
        var result = SubcommandBuilder.BuildRumbleOff();

        Assert.Equal(8, result.Length);
        Assert.Equal(0x00, result[0]); // HF freq low byte
        Assert.Equal(0x01, result[1]); // HF freq high bit
        Assert.Equal(0x40, result[2]); // LF freq
        Assert.Equal(0x40, result[3]); // LF amp
    }

    [Fact]
    public void BuildSubcommand_ReturnsCorrectFormat()
    {
        var data = new byte[] { 0x01, 0x02, 0x03 };
        var result = SubcommandBuilder.BuildSubcommand(0x40, data);

        Assert.Equal(4, result.Length);
        Assert.Equal(0x40, result[0]); // Subcommand ID
        Assert.Equal(0x01, result[1]); // Data byte 1
        Assert.Equal(0x02, result[2]); // Data byte 2
        Assert.Equal(0x03, result[3]); // Data byte 3
    }

    [Fact]
    public void BuildSubcommand_EmptyData_ReturnsCorrectFormat()
    {
        var result = SubcommandBuilder.BuildSubcommand(0x40, Array.Empty<byte>());

        Assert.Single(result);
        Assert.Equal(0x40, result[0]); // Subcommand ID only
    }
}

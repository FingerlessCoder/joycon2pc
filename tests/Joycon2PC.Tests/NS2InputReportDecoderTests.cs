using Joycon2PC.Core;
using Xunit;

namespace Joycon2PC.Tests;

/// <summary>
/// Coverage for the LIVE decode path: Joycon2PC.Core.NS2InputReportDecoder.TryDecode,
/// called from MainForm.RawReportReceived for every BLE input notification.
///
/// Expectations are derived from the decoder source, not from assumptions:
///
///   off            = (data[0] == 0xA1) ? 1 : 0            // HID-over-GATT prefix strip
///   reject         = data.Length < 8 || data.Length < off + 8 || data[off] == 0x21
///   Buttons        = data[off+4] | data[off+5]<<8 | data[off+6]<<16 | data[off+7]<<24
///   sticks present = data.Length >= off + 16
///   LX = data[off+10] | ((data[off+11] & 0x0F) << 8)      // low byte | HIGH nibble << 8
///   LY = (data[off+11] >> 4) | (data[off+12] << 4)        // LOW nibble | next byte << 4
///   RX = data[off+13] | ((data[off+14] & 0x0F) << 8)
///   RY = (data[off+14] >> 4) | (data[off+15] << 4)
///   neutralised    = raw == 0 || raw == 2047 ? 1998 : raw
///
/// Note bytes [off+11] and [off+14] are SHARED: the low nibble belongs to the
/// X axis and the high nibble to the Y axis of the same stick. The tests below
/// pin that split down for all four axes independently.
/// </summary>
public class NS2InputReportDecoderTests
{
    // ────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a report by inverting the decoder's packing. Used for the
    /// per-axis matrix; the literal-byte tests below deliberately avoid this
    /// helper so a shared misunderstanding cannot hide.
    /// </summary>
    private static byte[] Pack(uint buttons, int lx, int ly, int rx, int ry, int length = 63, bool withPrefix = false)
    {
        var d = new byte[length];
        int off = withPrefix ? 1 : 0;

        if (withPrefix) d[0] = 0xA1;
        d[off + 0] = 0x3F;                       // rolling counter, never 0x21

        d[off + 4] = (byte)(buttons & 0xFF);
        d[off + 5] = (byte)((buttons >> 8) & 0xFF);
        d[off + 6] = (byte)((buttons >> 16) & 0xFF);
        d[off + 7] = (byte)((buttons >> 24) & 0xFF);

        if (length >= off + 16)
        {
            d[off + 10] = (byte)(lx & 0xFF);
            d[off + 11] = (byte)(((ly & 0x0F) << 4) | ((lx >> 8) & 0x0F));
            d[off + 12] = (byte)(ly >> 4);
            d[off + 13] = (byte)(rx & 0xFF);
            d[off + 14] = (byte)(((ry & 0x0F) << 4) | ((rx >> 8) & 0x0F));
            d[off + 15] = (byte)(ry >> 4);
        }

        return d;
    }

    private static void AssertIsDefault(NS2InputReportDecoder.DecodedInput d)
    {
        Assert.Equal(0u, d.Buttons);
        Assert.Equal(0, d.RawLeftStickX);
        Assert.Equal(0, d.RawLeftStickY);
        Assert.Equal(0, d.RawRightStickX);
        Assert.Equal(0, d.RawRightStickY);
        Assert.Equal(0, d.LeftStickX);
        Assert.Equal(0, d.LeftStickY);
        Assert.Equal(0, d.RightStickX);
        Assert.Equal(0, d.RightStickY);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Valid 63-byte input report (literal bytes, no encoder helper)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryDecode_Valid63ByteReport_DecodesButtonsFromBytes4To7AndSticksFromBytes10To15()
    {
        // Hand-packed so the test does not depend on Pack() being correct.
        // Buttons: 0x01 at [4]  -> 0x00000001
        // LX = 0x2A5 =  677 : [10] = 0xA5, [11] low nibble = 0x2
        // LY = 0x1B3 =  435 : [11] high nibble = 0x3, [12] = 0x1B
        // RX = 0x3C4 =  964 : [13] = 0xC4, [14] low nibble = 0x3
        // RY = 0x0F5 =  245 : [14] high nibble = 0x5, [15] = 0x0F
        //   => [11] = 0x32, [14] = 0x53
        var report = new byte[63];
        report[0] = 0x3F;   // rolling counter (must not be 0xA1 or 0x21)
        report[4] = 0x01;
        report[10] = 0xA5;
        report[11] = 0x32;
        report[12] = 0x1B;
        report[13] = 0xC4;
        report[14] = 0x53;
        report[15] = 0x0F;

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));

        Assert.Equal(0x00000001u, d.Buttons);

        Assert.Equal(677, d.RawLeftStickX);
        Assert.Equal(435, d.RawLeftStickY);
        Assert.Equal(964, d.RawRightStickX);
        Assert.Equal(245, d.RawRightStickY);

        // None of these are 0 or 2047, so they must pass through untouched.
        Assert.Equal(677, d.LeftStickX);
        Assert.Equal(435, d.LeftStickY);
        Assert.Equal(964, d.RightStickX);
        Assert.Equal(245, d.RightStickY);
    }

    [Fact]
    public void TryDecode_ButtonWordIsLittleEndianAndMatchesSW2ButtonBits()
    {
        // A (bit 3) | ZR (bit 7) | Home (bit 12) | GripLeft (bit 25) = 0x02001088
        // Little-endian bytes: 0x88, 0x10, 0x00, 0x02
        var report = Pack(
            buttons: (uint)(SW2Button.A | SW2Button.ZR | SW2Button.Home | SW2Button.GripLeft),
            lx: 1998, ly: 1998, rx: 1998, ry: 1998);

        Assert.Equal(0x88, report[4]);
        Assert.Equal(0x10, report[5]);
        Assert.Equal(0x00, report[6]);
        Assert.Equal(0x02, report[7]);

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));
        Assert.Equal(0x02001088u, d.Buttons);
    }

    [Fact]
    public void TryDecode_AllButtonsClear_YieldsZeroButtonWord()
    {
        Assert.True(NS2InputReportDecoder.TryDecode(Pack(0u, 1998, 1998, 1998, 1998), out var d));
        Assert.Equal(0u, d.Buttons);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  0xA1 HID-over-GATT prefix
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryDecode_HidOverGattPrefix_ShiftsEveryOffsetByOne()
    {
        // Same logical payload as the literal test above, plus the 0xA1 prefix byte.
        // Logical offset n now lives at physical byte n+1.
        var report = new byte[64];
        report[0] = 0xA1;
        report[1] = 0x3F;   // rolling counter at logical [0]
        report[5] = 0x01;   // buttons at logical [4]
        report[11] = 0xA5;
        report[12] = 0x32;
        report[13] = 0x1B;
        report[14] = 0xC4;
        report[15] = 0x53;
        report[16] = 0x0F;

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));

        Assert.Equal(0x00000001u, d.Buttons);
        Assert.Equal(677, d.LeftStickX);
        Assert.Equal(435, d.LeftStickY);
        Assert.Equal(964, d.RightStickX);
        Assert.Equal(245, d.RightStickY);
    }

    [Fact]
    public void TryDecode_PrefixedAndUnprefixedVersions_OfTheSamePayload_Agree()
    {
        var plain = Pack(0x01234567u, 1000, 2000, 3000, 4000);
        var prefixed = Pack(0x01234567u, 1000, 2000, 3000, 4000, length: 64, withPrefix: true);

        Assert.True(NS2InputReportDecoder.TryDecode(plain, out var a));
        Assert.True(NS2InputReportDecoder.TryDecode(prefixed, out var b));

        Assert.Equal(a.Buttons, b.Buttons);
        Assert.Equal(a.LeftStickX, b.LeftStickX);
        Assert.Equal(a.LeftStickY, b.LeftStickY);
        Assert.Equal(a.RightStickX, b.RightStickX);
        Assert.Equal(a.RightStickY, b.RightStickY);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  0x21 subcommand replies must not be decoded as input
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryDecode_SubcommandReply0x21_WithoutPrefix_IsRejected()
    {
        var report = Pack(0xFFFFFFFFu, 4095, 4095, 4095, 4095);
        report[0] = 0x21;   // subcommand ACK frame, not an input report

        Assert.False(NS2InputReportDecoder.TryDecode(report, out var d));
        AssertIsDefault(d);
    }

    [Fact]
    public void TryDecode_SubcommandReply0x21_AfterPrefixStrip_IsRejected()
    {
        // The 0x21 marker sits at LOGICAL offset 0, i.e. physical byte 1 once the
        // HID-over-GATT prefix is present. The decoder tests data[off], so this
        // frame is rejected too.
        var report = Pack(0xFFFFFFFFu, 4095, 4095, 4095, 4095, length: 64, withPrefix: true);
        report[1] = 0x21;

        Assert.False(NS2InputReportDecoder.TryDecode(report, out var d));
        AssertIsDefault(d);
    }

    [Fact]
    public void TryDecode_Byte0x21OutsideTheCounterPosition_IsStillDecoded()
    {
        // Guards against an over-broad rejection: 0x21 appearing in the button or
        // stick bytes must NOT reject the frame. Only data[off] == 0x21 rejects.
        var report = Pack(0x00000008u, 1000, 2000, 3000, 4000);
        report[6] = 0x21;   // inside the button word, not the counter byte

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));
        Assert.Equal(0x00210008u, d.Buttons);   // byte[6] is the third button byte -> << 16
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Truncated / null buffers
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryDecode_Null_ReturnsFalseWithoutThrowing()
    {
        Assert.False(NS2InputReportDecoder.TryDecode(null!, out var d));
        AssertIsDefault(d);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void TryDecode_BufferShorterThanEightBytes_IsRejected(int length)
    {
        Assert.False(NS2InputReportDecoder.TryDecode(new byte[length], out var d));
        AssertIsDefault(d);
    }

    [Fact]
    public void TryDecode_ExactlyEightBytes_SucceedsWithNeutralSticksAndNoRawValues()
    {
        // Minimum accepted length: buttons decodable, stick bytes absent.
        var report = new byte[8];
        report[0] = 0x3F;
        report[4] = 0x34;
        report[5] = 0x12;

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));
        Assert.Equal(0x1234u, d.Buttons);

        Assert.Equal(0, d.RawLeftStickX);
        Assert.Equal(0, d.RawLeftStickY);
        Assert.Equal(0, d.RawRightStickX);
        Assert.Equal(0, d.RawRightStickY);

        // Absent stick data must read as centred, never as 0.
        Assert.Equal(NS2InputReportDecoder.NeutralStickValue, d.LeftStickX);
        Assert.Equal(NS2InputReportDecoder.NeutralStickValue, d.LeftStickY);
        Assert.Equal(NS2InputReportDecoder.NeutralStickValue, d.RightStickX);
        Assert.Equal(NS2InputReportDecoder.NeutralStickValue, d.RightStickY);
    }

    [Fact]
    public void TryDecode_FifteenBytes_StillHasNoStickData()
    {
        var report = Pack(0u, 1000, 2000, 3000, 4000, length: 15);

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));
        Assert.Equal(0, d.RawLeftStickX);
        Assert.Equal(NS2InputReportDecoder.NeutralStickValue, d.LeftStickX);
        Assert.Equal(NS2InputReportDecoder.NeutralStickValue, d.RightStickY);
    }

    [Fact]
    public void TryDecode_ExactlySixteenBytes_IsTheFirstLengthThatCarriesSticks()
    {
        var report = Pack(0u, 1000, 2000, 3000, 4000, length: 16);

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));
        Assert.Equal(1000, d.RawLeftStickX);
        Assert.Equal(2000, d.RawLeftStickY);
        Assert.Equal(3000, d.RawRightStickX);
        Assert.Equal(4000, d.RawRightStickY);
    }

    [Fact]
    public void TryDecode_PrefixedBufferTooShortAfterStrippingPrefix_IsRejected()
    {
        // 8 physical bytes pass the first length check, but the prefix consumes
        // one of them so only 7 logical bytes remain -> must be rejected.
        var report = new byte[8];
        report[0] = 0xA1;

        Assert.False(NS2InputReportDecoder.TryDecode(report, out var d));
        AssertIsDefault(d);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Sentinel neutralisation: raw 0 and raw 2047 -> 1998
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NeutralisationConstants_AreTheDocumentedValues()
    {
        Assert.Equal(1998, NS2InputReportDecoder.NeutralStickValue);
        Assert.Equal(2047, NS2InputReportDecoder.SentinelStickValue);
    }

    [Fact]
    public void TryDecode_Sentinel2047OnAllAxes_IsNeutralisedButRawIsPreserved()
    {
        // 0x7FF = 2047 for every axis:
        //   [10] = 0xFF, [11] low nibble = 0x7   -> 0xFF | (0x7 << 8)   = 0x7FF
        //   [11] high nibble = 0xF, [12] = 0x7F -> 0xF | (0x7F << 4)  = 0x7FF
        //   [13] = 0xFF, [14] low nibble = 0x7
        //   [14] high nibble = 0xF, [15] = 0x7F
        // => [11] = 0xF7, [14] = 0xF7
        var report = new byte[63];
        report[0] = 0x3F;
        report[10] = 0xFF;
        report[11] = 0xF7;
        report[12] = 0x7F;
        report[13] = 0xFF;
        report[14] = 0xF7;
        report[15] = 0x7F;

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));

        Assert.Equal(2047, d.RawLeftStickX);
        Assert.Equal(2047, d.RawLeftStickY);
        Assert.Equal(2047, d.RawRightStickX);
        Assert.Equal(2047, d.RawRightStickY);

        Assert.Equal(1998, d.LeftStickX);
        Assert.Equal(1998, d.LeftStickY);
        Assert.Equal(1998, d.RightStickX);
        Assert.Equal(1998, d.RightStickY);
    }

    [Fact]
    public void TryDecode_ZeroRawValues_AreNeutralisedButRawIsPreserved()
    {
        var report = new byte[63];
        report[0] = 0x3F;   // stick bytes [10..15] all zero

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));

        Assert.Equal(0, d.RawLeftStickX);
        Assert.Equal(0, d.RawLeftStickY);
        Assert.Equal(0, d.RawRightStickX);
        Assert.Equal(0, d.RawRightStickY);

        Assert.Equal(1998, d.LeftStickX);
        Assert.Equal(1998, d.LeftStickY);
        Assert.Equal(1998, d.RightStickX);
        Assert.Equal(1998, d.RightStickY);
    }

    [Fact]
    public void TryDecode_SentinelOnOneAxisOnly_NeutralisesOnlyThatAxis()
    {
        // Right stick absent (Joy-Con L reports 0x7FF there); left stick real.
        var report = Pack(0u, 1000, 2000, 2047, 2047);

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));

        Assert.Equal(1000, d.LeftStickX);
        Assert.Equal(2000, d.LeftStickY);

        Assert.Equal(2047, d.RawRightStickX);
        Assert.Equal(2047, d.RawRightStickY);
        Assert.Equal(1998, d.RightStickX);
        Assert.Equal(1998, d.RightStickY);
    }

    [Fact]
    public void TryDecode_ZeroOnOneAxisOnly_NeutralisesOnlyThatAxis()
    {
        var report = Pack(0u, 0, 1000, 2000, 3000);

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));

        Assert.Equal(0, d.RawLeftStickX);
        Assert.Equal(1998, d.LeftStickX);

        Assert.Equal(1000, d.LeftStickY);
        Assert.Equal(2000, d.RightStickX);
        Assert.Equal(3000, d.RightStickY);
    }

    [Theory]
    // 2046 and 2048 sit either side of the 2047 sentinel; 1 sits either side of
    // the "0 means no data" rule. None of them may be neutralised.
    [InlineData(2046)]
    [InlineData(2048)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(746)]     // NS2 factory minimum
    [InlineData(3249)]    // NS2 factory maximum
    public void TryDecode_ValuesAdjacentToTheSentinels_AreNotNeutralised(int value)
    {
        var report = Pack(0u, value, value, value, value);

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));

        Assert.Equal(value, d.LeftStickX);
        Assert.Equal(value, d.LeftStickY);
        Assert.Equal(value, d.RightStickX);
        Assert.Equal(value, d.RightStickY);
    }

    [Fact]
    public void TryDecode_FullScaleMaximum4095_IsNotTreatedAsSentinel()
    {
        // 0xFFF is the 12-bit all-ones pattern; only 0x7FF is the sentinel.
        var report = Pack(0u, 4095, 4095, 4095, 4095);

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));
        Assert.Equal(4095, d.LeftStickX);
        Assert.Equal(4095, d.RightStickY);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Nibble split, both halves, all four axes
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    // Each row gives all four axes a distinct value, and the interesting values
    // (1, 2046, 2048, 4095) are rotated so every axis sees each of them once.
    [InlineData(0x2A5, 0x1B3, 0x3C4, 0x0F5)]   // 677, 435, 964, 245
    [InlineData(0xFFF, 0x001, 0x7FE, 0x800)]   // 4095, 1, 2046, 2048
    [InlineData(0x800, 0xFFF, 0x001, 0x7FE)]
    [InlineData(0x001, 0x7FE, 0x800, 0xFFF)]
    [InlineData(0x0F0, 0xF00, 0x00F, 0xF0F)]   // nibble-boundary values
    [InlineData(0xABC, 0xDEF, 0x123, 0x456)]
    public void TryDecode_AllFourAxes_AreUnpackedIndependently(int lx, int ly, int rx, int ry)
    {
        var report = Pack(0u, lx, ly, rx, ry);

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));

        Assert.Equal(lx, d.RawLeftStickX);
        Assert.Equal(ly, d.RawLeftStickY);
        Assert.Equal(rx, d.RawRightStickX);
        Assert.Equal(ry, d.RawRightStickY);

        Assert.Equal(lx, d.LeftStickX);
        Assert.Equal(ly, d.LeftStickY);
        Assert.Equal(rx, d.RightStickX);
        Assert.Equal(ry, d.RightStickY);
    }

    [Fact]
    public void TryDecode_LeftStickSharedNibbleByte_LowNibbleMovesOnlyX()
    {
        // byte[11] carries BOTH left-stick axes: low nibble -> LX (<< 8),
        // high nibble -> LY. Only the low nibble is set here, so only LX moves.
        var lowNibbleOnly = new byte[63];
        lowNibbleOnly[0]  = 0x3F;
        lowNibbleOnly[10] = 0x05;   // LX low byte
        lowNibbleOnly[11] = 0x0A;   // high nibble 0x0 (LY), low nibble 0xA (LX)
        lowNibbleOnly[12] = 0x00;   // LY second byte
        RightStickBytes(lowNibbleOnly);

        // Only the high nibble is set here, so only LY moves.
        var highNibbleOnly = new byte[63];
        highNibbleOnly[0]  = 0x3F;
        highNibbleOnly[10] = 0x00;  // LX low byte
        highNibbleOnly[11] = 0xB0;  // high nibble 0xB (LY), low nibble 0x0 (LX)
        highNibbleOnly[12] = 0x01;  // LY = (0xB0 >> 4) | (0x01 << 4) = 0x1B = 27
        RightStickBytes(highNibbleOnly);

        Assert.True(NS2InputReportDecoder.TryDecode(lowNibbleOnly, out var a));
        Assert.True(NS2InputReportDecoder.TryDecode(highNibbleOnly, out var b));

        // LX takes the low nibble shifted up: 0x05 | (0xA << 8) = 0xA05 = 2565
        Assert.Equal(2565, a.RawLeftStickX);
        Assert.Equal(0, a.RawLeftStickY);

        // LY takes the high nibble: 0xB | (0x1 << 4) = 27
        Assert.Equal(0, b.RawLeftStickX);
        Assert.Equal(27, b.RawLeftStickY);

        // Normalised values follow the sentinel rule (0 -> 1998), not the nibble role.
        Assert.Equal(2565, a.LeftStickX);
        Assert.Equal(NS2InputReportDecoder.NeutralStickValue, a.LeftStickY);
        Assert.Equal(NS2InputReportDecoder.NeutralStickValue, b.LeftStickX);
        Assert.Equal(27, b.LeftStickY);

        // The opposite stick is untouched by byte[11]: RX = 0x11 | (0x2 << 8) = 529,
        // RY = (0x22 >> 4) | (0x03 << 4) = 50.
        Assert.Equal(529, a.RightStickX);
        Assert.Equal(50, a.RightStickY);
        Assert.Equal(a.RightStickX, b.RightStickX);
        Assert.Equal(a.RightStickY, b.RightStickY);
    }

    [Fact]
    public void TryDecode_RightStickSharedNibbleByte_LowNibbleMovesOnlyX()
    {
        // Same split on byte[14] for the right stick.
        var lowNibbleOnly = new byte[63];
        lowNibbleOnly[0]  = 0x3F;
        lowNibbleOnly[13] = 0x05;
        lowNibbleOnly[14] = 0x0A;
        lowNibbleOnly[15] = 0x00;
        LeftStickBytes(lowNibbleOnly);

        var highNibbleOnly = new byte[63];
        highNibbleOnly[0]  = 0x3F;
        highNibbleOnly[13] = 0x00;
        highNibbleOnly[14] = 0xB0;
        highNibbleOnly[15] = 0x01;
        LeftStickBytes(highNibbleOnly);

        Assert.True(NS2InputReportDecoder.TryDecode(lowNibbleOnly, out var a));
        Assert.True(NS2InputReportDecoder.TryDecode(highNibbleOnly, out var b));

        Assert.Equal(2565, a.RawRightStickX);
        Assert.Equal(0, a.RawRightStickY);
        Assert.Equal(0, b.RawRightStickX);
        Assert.Equal(27, b.RawRightStickY);

        // Left stick unchanged: LX = 0x34 | (0x7 << 8) = 1844, LY = 0x5 | (0x07 << 4) = 117
        Assert.Equal(1844, a.LeftStickX);
        Assert.Equal(117, a.LeftStickY);
        Assert.Equal(a.LeftStickX, b.LeftStickX);
        Assert.Equal(a.LeftStickY, b.LeftStickY);
    }

    // Real (non-sentinel) right-stick bytes: [13] = 0x11, [14] = 0x22, [15] = 0x03
    private static void RightStickBytes(byte[] d)
    {
        d[13] = 0x11;
        d[14] = 0x22;
        d[15] = 0x03;
    }

    // Real (non-sentinel) left-stick bytes: [10] = 0x34, [11] = 0x5X, [12] = 0x07
    private static void LeftStickBytes(byte[] d)
    {
        d[10] = 0x34;
        d[11] = 0x57;
        d[12] = 0x07;
    }

    [Fact]
    public void TryDecode_EachStickBytePair_IsIndependentAcrossSticks()
    {
        // Left stick at rest, right stick fully deflected: a swap of the X and Y
        // nibble roles on either stick would be visible here.
        var report = Pack(0u, lx: 1998, ly: 1998, rx: 4095, ry: 4095);

        Assert.True(NS2InputReportDecoder.TryDecode(report, out var d));

        Assert.Equal(1998, d.LeftStickX);
        Assert.Equal(1998, d.LeftStickY);
        Assert.Equal(4095, d.RightStickX);
        Assert.Equal(4095, d.RightStickY);

        // Right stick fully high => [13] = 0xFF, [14] = 0xFF, [15] = 0xFF
        Assert.Equal(0xFF, report[13]);
        Assert.Equal(0xFF, report[14]);
        Assert.Equal(0xFF, report[15]);
    }
}
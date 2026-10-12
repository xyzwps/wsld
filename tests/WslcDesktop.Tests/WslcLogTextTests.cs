using System.Globalization;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Tests;

/// <summary>
/// 功能 005 · AC3/AC4/AC5:把 `wslc logs` 的原始文本整理成界面可显示的行。
/// 样例夹具来自 wslc 3.0.1 实测输出(行尾只有 LF;-t 前缀是 UTC 纳秒串)。
/// </summary>
public class WslcLogTextTests
{
    private const string TimestampedLine = "2026-10-12T11:48:46.899395500Z log line 741 at 1791805726";

    // ---------- AC3 · 拆行 ----------

    [Fact]
    public void Split_RealWslcOutput_LfOnlySixLines()
    {
        var lines = WslcLogText.Split(FixtureLoader.Read("wslc-logs.txt"));

        Assert.Equal(6, lines.Count);
        Assert.Equal("log line 741 at 1791805726", lines[0]);
        Assert.DoesNotContain(lines, line => line.Contains('\r'));
    }

    [Fact]
    public void Split_MixedLineEndings_Normalized()
    {
        var lines = WslcLogText.Split("a\r\nb\nc");

        Assert.Equal(new[] { "a", "b", "c" }, lines);
    }

    [Fact]
    public void Split_DropsTrailingBlankLine_KeepsInnerBlanks()
    {
        // 末尾空行是换行符的产物;中间空行是日志本身的内容,必须保留。
        var lines = WslcLogText.Split("a\n\nb\n");

        Assert.Equal(new[] { "a", "", "b" }, lines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\n\n")]
    public void Split_EmptyishInput_ReturnsEmptyArray(string? raw)
    {
        Assert.Empty(WslcLogText.Split(raw));
    }

    // ---------- AC4 · 拼接与截断 ----------

    [Fact]
    public void Join_UsesCrlfForReadonlyTextBox()
    {
        Assert.Equal("a\r\nb", WslcLogText.Join(new[] { "a", "b" }));
        Assert.Equal("", WslcLogText.Join(Array.Empty<string>()));
    }

    [Fact]
    public void Pretty_LineAtLimit_IsUntouched()
    {
        var exact = new string('x', WslcLogText.MaxLineChars);

        Assert.Equal(exact, WslcLogText.Pretty(exact, timestamps: false));
    }

    [Fact]
    public void Pretty_OverlongLine_TruncatedWithMarker()
    {
        var longLine = new string('x', WslcLogText.MaxLineChars + 500);

        var pretty = WslcLogText.Pretty(longLine, timestamps: false);

        Assert.Equal(WslcLogText.MaxLineChars + WslcLogText.TruncatedSuffix.Length, pretty.Length);
        Assert.EndsWith(WslcLogText.TruncatedSuffix, pretty, StringComparison.Ordinal);
    }

    // ---------- AC5 · 时间戳可读化 ----------

    [Fact]
    public void Pretty_TimestampsOn_UtcNanoPrefixBecomesLocalClock()
    {
        var utc = DateTimeOffset.Parse("2026-10-12T11:48:46.899395500Z", CultureInfo.InvariantCulture);
        var expected = utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

        var pretty = WslcLogText.Pretty(TimestampedLine, timestamps: true);

        Assert.Equal(expected + " log line 741 at 1791805726", pretty);
    }

    [Fact]
    public void Pretty_TimestampsOn_OffsetSuffixAlsoBecomesLocalClock()
    {
        var line = "2026-10-12T19:48:46.899+08:00 hello";

        var pretty = WslcLogText.Pretty(line, timestamps: true);

        Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3} hello$", pretty);
    }

    [Fact]
    public void Pretty_TimestampsOn_UnparseablePrefix_KeepsLineVerbatim()
    {
        // 形状像时间戳但日历上不存在 —— 绝不猜测,原样保留。
        var line = "2026-13-45T99:99:99.000Z 乱码行";

        Assert.Equal(line, WslcLogText.Pretty(line, timestamps: true));
    }

    [Theory]
    [InlineData(TimestampedLine)]
    [InlineData("2026-10-12T11:48:46.899395500Z")] // 只有时间戳没有空格 → 不算前缀
    public void Pretty_TimestampsOff_NeverTouchesTheLine(string line)
    {
        Assert.Equal(line, WslcLogText.Pretty(line, timestamps: false));
    }

    [Fact]
    public void Prepare_TimestampedFixture_AllLinesGetReadableClockPrefix()
    {
        var lines = WslcLogText.Prepare(FixtureLoader.Read("wslc-logs-timestamps.txt"), timestamps: true);

        Assert.Equal(6, lines.Count);
        Assert.All(lines, line => Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3} log line \d+ at \d+$", line));
        Assert.All(lines, line => Assert.DoesNotContain("2026-10-12T", line, StringComparison.Ordinal));
    }

    [Fact]
    public void Prepare_PlainFixture_TimestampLikeContentSurvivesUntouched()
    {
        var lines = WslcLogText.Prepare(FixtureLoader.Read("wslc-logs.txt"), timestamps: false);

        Assert.Equal("log line 741 at 1791805726", lines[0]);
    }
}

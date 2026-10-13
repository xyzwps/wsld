using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Tests;

/// <summary>
/// 功能 011 · AC3:把 wslc 在伪终端里写出的 ANSI/VT 字节流还原成"屏幕上此刻可见的行"。
/// 下面的帧序列取自真机(wslc 3.0.1 拉 ubuntu 24.04)抓到的原样转义,不凭印象编:
/// 每一帧都是"ESC[行;列H 定位 → 写一行 → ESC[NNX 擦掉行尾 → CR LF 前进",
/// 所以同一层的刷新是回写同一行,屏幕行数只随层数增长,不会随刷新次数无限变长。
/// </summary>
public class VtScreenTrackerTests
{
    /// <summary>ESC 一律写成 \u001b:十六进制转义 \x1b 会把紧跟其后的字符也当成数字吃掉。</summary>
    private const string Esc = "\u001b";

    /// <summary>实测的启动噪声:终端模式开关 + 清屏 + 设标题 + 藏光标,一行文本都没有。</summary>
    private const string StartupSequence =
        Esc + "[?9001h" + Esc + "[?1004h" +
        Esc + "[2J" + Esc + "[m" + Esc + "]0;C:\\Users\\dev\\wslc.exe" + "\u0007" + Esc + "[?25l";

    // ---------- 基本还原 ----------

    [Fact]
    public void StartupNoise_LeavesNothingOnScreen()
    {
        var tracker = new VtScreenTracker();

        Assert.False(tracker.Feed(StartupSequence));   // 没有字符落到屏幕上
        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public void HeadAndLayerLine_BecomeTwoRows()
    {
        var tracker = new VtScreenTracker();
        tracker.Feed(StartupSequence);

        // 实测头行:ESC[H 回到 (1,1),写完 ESC[65X 擦行尾,再 CR LF
        tracker.Feed(Esc + "[H24.04: Pulling from library/ubuntu" + Esc + "[65X\r\n" + Esc + "[?25h");
        tracker.Feed("a60ef8ec184e: Pulling fs layer" + Esc + "[69X\r\n" + Esc + "[?25h");

        Assert.Equal(
            new[] { "24.04: Pulling from library/ubuntu", "a60ef8ec184e: Pulling fs layer" },
            tracker.Snapshot());
    }

    // ---------- 回写:进度刷新不增长行 ----------

    [Fact]
    public void AbsoluteRewrite_KeepsOneRowPerLayer()
    {
        var tracker = new VtScreenTracker();
        tracker.Feed(Esc + "[H24.04: Pulling from library/ubuntu" + Esc + "[65X\r\n");
        tracker.Feed("a60ef8ec184e: Pulling fs layer" + Esc + "[69X\r\n");

        // 实测刷新帧:ESC[2;1H 定位回第 2 行行首,把这一层的最新状态盖上去
        tracker.Feed(Esc + "[2;1Ha60ef8ec184e: Downloading 544.5kB" + Esc + "[66X\r\n");
        Assert.Equal(2, tracker.Snapshot().Count);
        Assert.Equal("a60ef8ec184e: Downloading 544.5kB", tracker.Snapshot()[1]);

        tracker.Feed(Esc + "[2;1Ha60ef8ec184e: Downloading 1.073MB" + Esc + "[66X\r\n");
        tracker.Feed(Esc + "[2;1Ha60ef8ec184e: Verifying Checksum" + Esc + "[66X\r\n");
        tracker.Feed(Esc + "[2;1Ha60ef8ec184e: Pull complete" + Esc + "[66X\r\n");

        Assert.Equal(2, tracker.Snapshot().Count);                       // 刷了四轮,屏幕还是两行
        Assert.Equal("a60ef8ec184e: Pull complete", tracker.Snapshot()[1]);
    }

    [Fact]
    public void MultipleLayers_CoexistAsSeparateRows()
    {
        var tracker = new VtScreenTracker();
        tracker.Feed(Esc + "[Hlatest: Pulling from library/nginx" + Esc + "[65X\r\n");
        tracker.Feed("ecc510c1e359: Pulling fs layer" + Esc + "[69X\r\n");
        tracker.Feed("04bdbc7ea6b5: Pulling fs layer" + Esc + "[69X\r\n");
        tracker.Feed("807ab930f3a0: Pulling fs layer" + Esc + "[69X\r\n");

        // wslc 是按"层"回写的:第 4 行是 807ab…,第 2 行是 ecc51…
        tracker.Feed(Esc + "[4;1H807ab930f3a0: Download complete" + Esc + "[66X\r\n");
        tracker.Feed(Esc + "[2;1Hecc510c1e359: Downloading 2.14MB" + Esc + "[66X\r\n");

        Assert.Equal(
            new[]
            {
                "latest: Pulling from library/nginx",
                "ecc510c1e359: Downloading 2.14MB",
                "04bdbc7ea6b5: Pulling fs layer",
                "807ab930f3a0: Download complete",
            },
            tracker.Snapshot());
    }

    // ---------- 擦除 ----------

    [Fact]
    public void EchDropsTheStaleTailWhenTheNewTextIsShorter()
    {
        var tracker = new VtScreenTracker();
        tracker.Feed(Esc + "[2;1Ha60ef8ec184e: Downloading 12.345MB" + Esc + "[63X\r\n");
        tracker.Feed(Esc + "[2;1Ha60ef8ec184e: Extracting" + Esc + "[100X\r\n");

        // 新文本比旧文本短,尾部必须被擦掉,否则屏幕上留着 "...12.345MB" 的残渣
        Assert.Equal("a60ef8ec184e: Extracting", Assert.Single(tracker.Snapshot()));
    }

    [Fact]
    public void ElTruncatesTheLineAtTheCursor()
    {
        var tracker = new VtScreenTracker();
        tracker.Feed("hello world\r\n");
        tracker.Feed(Esc + "[1;6H" + Esc + "[0K");   // 回到第 6 列,擦到行尾

        Assert.Equal("hello", Assert.Single(tracker.Snapshot()));
    }

    [Fact]
    public void EraseScreenStartsOver()
    {
        var tracker = new VtScreenTracker();
        tracker.Feed("leftover line\r\n");
        Assert.True(tracker.Feed(Esc + "[2J" + Esc + "[m"));
        Assert.Empty(tracker.Snapshot());

        tracker.Feed("fresh\r\n");
        Assert.Equal("fresh", Assert.Single(tracker.Snapshot()));
    }

    // ---------- 光标与控制字符 ----------

    [Fact]
    public void CarriageReturnAloneOverwritesTheSameRow()
    {
        var tracker = new VtScreenTracker();

        tracker.Feed("abc\rdef\r");   // 纯 CR 只回行首,不换行

        Assert.Equal("def", Assert.Single(tracker.Snapshot()));
    }

    [Fact]
    public void LineFeedMovesDownButKeepsTheColumn()
    {
        var tracker = new VtScreenTracker();

        tracker.Feed("ab\r\n");       // CR LF 组合:回到列首再下行
        tracker.Feed("cd\r\n");

        Assert.Equal(new[] { "ab", "cd" }, tracker.Snapshot());
    }

    [Fact]
    public void BackspaceMovesTheCursorLeft()
    {
        var tracker = new VtScreenTracker();

        tracker.Feed("abc\bX");   // 退一格再写,把 c 盖成 X

        Assert.Equal("abX", Assert.Single(tracker.Snapshot()));
    }

    [Fact]
    public void TabJumpsToTheNextStop()
    {
        var tracker = new VtScreenTracker();

        tracker.Feed("a\tb");

        Assert.Equal("a       b", Assert.Single(tracker.Snapshot()));   // 制表位在第 9 列(0 基第 8)
    }

    [Fact]
    public void BlankRowsAreSkippedAndTrailingSpacesTrimmed()
    {
        var tracker = new VtScreenTracker();
        tracker.Feed(Esc + "[1;1Hfirst" + Esc + "[3X\r\n");
        tracker.Feed(Esc + "[9;1Hninth   " + Esc + "[3X\r\n");   // 中间空了七行

        Assert.Equal(new[] { "first", "ninth" }, tracker.Snapshot());
    }

    // ---------- 序列的边角 ----------

    [Fact]
    public void OneFrameSplitAcrossTwoChunksIsStillReassembled()
    {
        var tracker = new VtScreenTracker();

        // 实测:ConPTY 常把一帧拆成两块,前一块以 ESC[?25l 结尾,定位序列被劈在中间
        tracker.Feed(Esc + "[?25l" + Esc + "[2;1");
        tracker.Feed("Ha60ef8ec184e: Downloading 544.5kB" + Esc + "[66X\r\n" + Esc + "[?25h");

        Assert.Equal("a60ef8ec184e: Downloading 544.5kB", Assert.Single(tracker.Snapshot()));
    }

    [Fact]
    public void ColorAndUnknownSequencesAreIgnoredWithoutEatingText()
    {
        var tracker = new VtScreenTracker();

        tracker.Feed(Esc + "[38;5;12m");   // 256 色前景,最终以 m 收尾 → 忽略
        tracker.Feed("colored\r\n");
        tracker.Feed(Esc + "[S");          // 整屏上卷,本实现不认识 → 丢弃,后面的字照写
        tracker.Feed("next\r\n");

        Assert.Equal(new[] { "colored", "next" }, tracker.Snapshot());
    }

    [Fact]
    public void FeedReportsWhetherTheScreenActuallyChanged()
    {
        var tracker = new VtScreenTracker();

        Assert.False(tracker.Feed(""));                    // 空输入
        Assert.False(tracker.Feed(Esc + "[?25l"));          // 藏光标:不动屏幕
        Assert.False(tracker.Feed(Esc + "[1;1H"));          // 只移动光标
        Assert.True(tracker.Feed("x"));                     // 落到屏幕上
        Assert.False(tracker.Feed("\r"));                   // 回行首:内容没变
    }

    // ---------- 行数封顶 ----------

    [Fact]
    public void WritesBeyondTheTerminalHeightScrollTheOldestRowsOff()
    {
        var tracker = new VtScreenTracker(height: 24);

        for (var i = 1; i <= 60; i++) tracker.Feed($"layer-{i:00}\r\n");

        var rows = tracker.Snapshot();
        Assert.True(rows.Count <= 24, $"行数必须被终端高度封顶,实际 {rows.Count} 行");
        Assert.Equal("layer-60", rows[^1]);                 // 最新的还在
        Assert.DoesNotContain("layer-01", rows);            // 最早的被滚掉
    }
}
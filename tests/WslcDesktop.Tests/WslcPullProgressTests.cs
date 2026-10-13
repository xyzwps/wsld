using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Tests;

/// <summary>
/// 功能 011 · AC2:把"屏幕上此刻可见的行"解析成汇总数字(层数 / 完成数 / 已下载字节)。
/// 行形态取自真机 wslc 3.0.1 的伪终端输出:字节数写作 <c>544.5kB</c> / <c>1.073MB</c> / <c>2.14MB</c>
/// (1000 进制、小数位会变),解压阶段写作 <c>Extracting 5.5MB/27MB</c>,
/// 终态只有 <c>Pull complete</c> 与 <c>Already exists</c> 两种 —— <c>Download complete</c> 只是下载完了,层还没解压完。
/// </summary>
public class WslcPullProgressTests
{
    // ---------- 计数 ----------

    [Fact]
    public void Parse_CountsLayersDoneAndBytes()
    {
        var snapshot = WslcPullProgress.Parse(
        [
            "24.04: Pulling from library/ubuntu",
            "a60ef8ec184e: Downloading 1.073MB",
            "b2f3c9d1e4a5: Pull complete",
            "c4d5e6f7a8b9: Verifying Checksum",
            "d5e6f7a8b9c0: Extracting 5.5MB/27MB",
        ]);

        Assert.Equal(4, snapshot.TotalLayers);   // 头行不算层
        Assert.Equal(1, snapshot.DoneLayers);
        Assert.Equal(1_073_000L + 27_000_000L, snapshot.DownloadedBytes);
        Assert.Equal(5, snapshot.Lines.Count);   // 原始行要原样带着,界面靠它逐行显示(AC3)
    }

    [Fact]
    public void Parse_EmptyScreen_IsEmpty()
    {
        var snapshot = WslcPullProgress.Parse(Array.Empty<string>());

        Assert.True(snapshot.IsEmpty);
        Assert.Equal(0, snapshot.TotalLayers);
        Assert.Equal(0, snapshot.DoneLayers);
        Assert.Equal(0, snapshot.DownloadedBytes);
    }

    [Fact]
    public void Parse_HeaderDigestStatusAndReferenceLinesAreNotLayers()
    {
        var snapshot = WslcPullProgress.Parse(
        [
            "24.04: Pulling from library/ubuntu",
            "Digest: sha256:f9ea18bfa4fad859e1ed38259d711da7ccad2c3516e875cec3351a57c859f571",
            "Status: Downloaded newer image for docker.1ms.run/library/nginx:latest",
            "docker.1ms.run/library/nginx:latest",
        ]);

        // 头部行的 "24.04" 太短、Digest 的 "Digest" 不是十六进制,都不该被当成层
        Assert.Equal(0, snapshot.TotalLayers);
    }

    // ---------- 一层的最终状态 = 最后出现的那一行 ----------

    [Fact]
    public void Parse_LatestLineWinsForOneLayer()
    {
        var snapshot = WslcPullProgress.Parse(
        [
            "a60ef8ec184e: Pulling fs layer",
            "a60ef8ec184e: Downloading 544.5kB",
            "a60ef8ec184e: Download complete",
            "a60ef8ec184e: Extracting",
            "a60ef8ec184e: Pull complete",
        ]);

        Assert.Equal(1, snapshot.TotalLayers);    // 同一层不重复计数
        Assert.Equal(1, snapshot.DoneLayers);
    }

    [Fact]
    public void Parse_LayerIdIsCaseInsensitive()
    {
        var snapshot = WslcPullProgress.Parse(
        [
            "A60EF8EC184E: Downloading 1kB",
            "a60ef8ec184e: Pull complete",
        ]);

        Assert.Equal(1, snapshot.TotalLayers);
        Assert.Equal(1, snapshot.DoneLayers);
    }

    // ---------- 终态判定要保守 ----------

    [Theory]
    [InlineData("Pull complete", true)]
    [InlineData("Already exists", true)]
    [InlineData("Download complete", false)]   // 只是下载完,还在解压
    [InlineData("Verifying Checksum", false)]
    [InlineData("Extracting 5.5MB/27MB", false)]
    [InlineData("Pulling fs layer", false)]
    [InlineData("Downloading 1.073MB", false)]
    public void Parse_OnlyTerminalStatesCountAsDone(string status, bool done)
    {
        var snapshot = WslcPullProgress.Parse(["a60ef8ec184e: " + status]);

        Assert.Equal(done ? 1 : 0, snapshot.DoneLayers);
        Assert.Equal(1, snapshot.TotalLayers);
    }

    // ---------- 字节数 ----------

    [Fact]
    public void Parse_KeepsTheLargestByteCountPerLayer()
    {
        var snapshot = WslcPullProgress.Parse(
        [
            "a60ef8ec184e: Downloading 544.5kB",
            "a60ef8ec184e: Downloading 1.073MB",
            "a60ef8ec184e: Downloading 2.14MB",
            "b2f3c9d1e4a5: Downloading 3kB",
        ]);

        // 同一层的历次刷新取最大值(屏幕快照本来就是最新值,这里防的是把中间行也喂进来的场景)
        Assert.Equal(2_140_000L + 3_000L, snapshot.DownloadedBytes);
    }

    // ---------- 跨帧台账:真机上"已下载"会倒退 ----------

    [Fact]
    public void Parse_WithLedger_DownloadedNeverGoesBackwardsAcrossFrames()
    {
        // 实测现场:某层进入 Download complete 之后,屏幕上那一行不再带字节数。
        // 只看当前帧的话,总量会先退回只剩另一层,最后归零 —— 界面上数字倒退比不显示更糟。
        var ledger = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var first = WslcPullProgress.Parse(
        [
            "a60ef8ec184e: Downloading 1.6MB",
            "b2f3c9d1e4a5: Downloading 545.6kB",
        ], ledger);

        var second = WslcPullProgress.Parse(
        [
            "a60ef8ec184e: Download complete",
            "b2f3c9d1e4a5: Downloading 1.1MB",
        ], ledger);

        var third = WslcPullProgress.Parse(
        [
            "a60ef8ec184e: Pull complete",
            "b2f3c9d1e4a5: Extracting",
        ], ledger);

        Assert.Equal(1_600_000L + 545_600L, first.DownloadedBytes);
        Assert.Equal(1_600_000L + 1_100_000L, second.DownloadedBytes);
        Assert.Equal(second.DownloadedBytes, third.DownloadedBytes);
        Assert.True(second.DownloadedBytes >= first.DownloadedBytes);   // 只增不减
        Assert.True(third.DownloadedBytes >= second.DownloadedBytes);
        Assert.Equal(2, third.TotalLayers);
        Assert.Equal(1, third.DoneLayers);
    }

    [Fact]
    public void Parse_WithoutLedger_OnlySeesTheCurrentFrame()
    {
        var snapshot = WslcPullProgress.Parse(
        [
            "a60ef8ec184e: Download complete",
            "b2f3c9d1e4a5: Downloading 545.6kB",
        ]);

        // 不传台账 = 纯当前帧(真机上会倒退的那种行为),留给一次性解析的调用方
        Assert.Equal(545_600L, snapshot.DownloadedBytes);
    }

    [Fact]
    public void Parse_ExtractingFraction_TakesTheDenominator()
    {
        var snapshot = WslcPullProgress.Parse(["a60ef8ec184e: Extracting 5.5MB/27MB"]);

        Assert.Equal(27_000_000, snapshot.DownloadedBytes);   // 这一层的全量,不是已完成的部分
    }

    [Theory]
    [InlineData("a60ef8ec184e: Downloading 544.5kB", 544_500)]
    [InlineData("a60ef8ec184e: Downloading 1.073MB", 1_073_000)]
    [InlineData("a60ef8ec184e: Downloading 2.14GB", 2_140_000_000)]
    [InlineData("a60ef8ec184e: Downloading 812b", 812)]
    public void Parse_ReadsWslcByteUnitsAsPowersOfTen(string line, long expected)
    {
        Assert.Equal(expected, WslcPullProgress.Parse([line]).DownloadedBytes);
    }

    [Fact]
    public void Parse_StatesWithoutBytesContributeNothing()
    {
        var snapshot = WslcPullProgress.Parse(
        [
            "a60ef8ec184e: Downloading",       // 实测旧版本/快网络下确实没有数字
            "b2f3c9d1e4a5: Extracting 33.3%",  // 百分比不是字节
            "c4d5e6f7a8b9: Pulling fs layer",
        ]);

        Assert.Equal(0, snapshot.DownloadedBytes);
        Assert.Equal(3, snapshot.TotalLayers);
    }

    // ---------- 真机整段输出 ----------

    [Fact]
    public void Parse_RealPullLog_AllSevenLayersDone()
    {
        var lines = WslcLogText
            .Split(FixtureLoader.Read("wslc-pull-new.txt"));
        Assert.NotEmpty(lines);

        var snapshot = WslcPullProgress.Parse(lines);

        Assert.Equal(7, snapshot.TotalLayers);
        Assert.Equal(7, snapshot.DoneLayers);
        Assert.Equal(0, snapshot.DownloadedBytes);   // 这段夹具里 wslc 没写字节数,不能瞎猜
    }

    [Fact]
    public void Parse_UpToDateLog_HasNoLayers()
    {
        var lines = WslcLogText
            .Split(FixtureLoader.Read("wslc-pull-uptodate.txt"));

        Assert.Equal(0, WslcPullProgress.Parse(lines).TotalLayers);
    }

    // ---------- 界面用它区分"进度"和"报错" ----------

    [Theory]
    [InlineData("a60ef8ec184e: Downloading 544.5kB", true)]
    [InlineData("a60ef8ec184e: Pull complete", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef: Pulling fs layer", true)]
    [InlineData("24.04: Pulling from library/ubuntu", false)]
    [InlineData("Digest: sha256:f9ea18bfa4fad859e1ed38259d711da7ccad2c3516e875cec3351a57c859f571", false)]
    [InlineData("Status: Downloaded newer image for nginx:latest", false)]
    [InlineData("错误代码： E_FAIL", false)]
    [InlineData("docker.1ms.run/library/nginx:latest", false)]
    public void IsLayerLine_ClassifiesRealScreenRows(string line, bool expected)
    {
        Assert.Equal(expected, WslcPullProgress.IsLayerLine(line));
    }

    // ---------- 给人看的字节数 ----------

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(812, "812 B")]
    [InlineData(1_000, "1 kB")]
    [InlineData(544_500, "544.5 kB")]
    [InlineData(1_000_000, "1 MB")]
    [InlineData(1_073_000, "1.1 MB")]
    [InlineData(12_345_678, "12.3 MB")]
    [InlineData(2_140_000_000, "2.1 GB")]
    public void FormatBytes_UsesWslcStylePowersOfTen(long bytes, string expected)
    {
        Assert.Equal(expected, WslcPullProgress.FormatBytes(bytes));
    }

    [Fact]
    public void FormatBytes_ClampsNegativeToZero()
    {
        Assert.Equal("0 B", WslcPullProgress.FormatBytes(-5));
    }
}

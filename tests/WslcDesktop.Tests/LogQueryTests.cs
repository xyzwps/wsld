using WslcDesktop.Core.Models;

namespace WslcDesktop.Tests;

/// <summary>
/// 功能 005 · AC1/AC2:一次日志请求的参数拼装与"启动进程之前"的输入校验。
/// 实测依据:`wslc logs -n 0` 只会换回 8 行 usage(退出码 1),所以非法值必须在应用侧拦下。
/// </summary>
public class LogQueryTests
{
    [Fact]
    public void BuildArguments_Default_FixedOrderWithTail()
    {
        var query = new LogQuery { ContainerRef = "probe1" };

        Assert.Equal(new[] { "logs", "-n", "200", "probe1" }, query.BuildArguments());
    }

    [Fact]
    public void BuildArguments_FollowTimestampsTail_OrderIsFlagsThenCountThenRef()
    {
        var query = new LogQuery
        {
            ContainerRef = "f47fdddf9219",
            Follow = true,
            Timestamps = true,
            TailLines = 500,
        };

        Assert.Equal(new[] { "logs", "-f", "-t", "-n", "500", "f47fdddf9219" }, query.BuildArguments());
    }

    [Fact]
    public void BuildArguments_AllLines_OmitsTailFlagEntirely()
    {
        var query = new LogQuery { ContainerRef = "probe1", TailLines = LogQuery.AllLines };

        var args = query.BuildArguments();
        Assert.Equal(new[] { "logs", "probe1" }, args);
        Assert.DoesNotContain("-n", args);
    }

    [Fact]
    public void BuildArguments_TrimsContainerRef()
    {
        // 实测名称/短 ID/长 ID 都可以,但空格不行 —— 先 Trim 再发。
        var query = new LogQuery { ContainerRef = "  probe1  " };

        Assert.Equal("probe1", query.BuildArguments()[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildArguments_BlankContainerRef_Throws(string? reference)
    {
        var query = new LogQuery { ContainerRef = reference! };

        Assert.Throws<ArgumentException>(() => query.BuildArguments());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(100_001)]
    [InlineData(int.MaxValue)]
    public void BuildArguments_TailOutOfRange_Throws(int tail)
    {
        var query = new LogQuery { ContainerRef = "probe1", TailLines = tail };

        var ex = Assert.Throws<ArgumentException>(() => query.BuildArguments());
        Assert.Contains("行数", ex.Message);
    }

    [Theory]
    [InlineData(0)]   // "全部"是合法值(只是不发 -n)
    [InlineData(1)]
    [InlineData(100_000)] // 上限本身合法
    public void BuildArguments_TailWithinBounds_IsAccepted(int tail)
    {
        var query = new LogQuery { ContainerRef = "probe1", TailLines = tail };

        Assert.NotEmpty(query.BuildArguments());
    }

    [Fact]
    public void TailChoices_IsTheContractTheUiBindsTo()
    {
        // 界面直接绑这个序列;默认值必须在候选里,顺序也不能漂。
        Assert.Equal(new[] { 100, 200, 500, 1_000, 2_000, LogQuery.AllLines }, LogQuery.TailChoices);
        Assert.Contains(LogQuery.DefaultTailLines, LogQuery.TailChoices);
    }

    [Fact]
    public void DescribeTail_ZeroIsAllLines_OtherwiseShowsCount()
    {
        Assert.Equal("全部", LogQuery.DescribeTail(LogQuery.AllLines));
        Assert.Equal("最近 200 行", LogQuery.DescribeTail(200));
        Assert.Equal("全部", new LogQuery { TailLines = 0 }.TailLabel);
    }
}

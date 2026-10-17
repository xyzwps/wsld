using wsld.Core.Models;
using wsld.Core.Services;
using wsld.Core.WslcCli;

namespace wsld.Tests;

/// <summary>
/// 册子 LG-001~LG-009、LG-011、LG-013、LG-014、LG-017、LG-022、LG-025:
/// 一次日志请求怎么变成 wslc 参数,以及两条读取路径
/// (一次性「查看日志」/ 只收新行的「实时日志」)怎么把结果交回来。
/// </summary>
public class LogsServiceTests
{
    // ---------- 参数拼装:两种模式各一条,顺序固定 ----------

    [Fact]
    [Trait("case", "LG-001")]
    [Trait("case", "LG-004")]
    public void View_BuildsLogsTailReference_InFixedOrder()
    {
        var args = LogQuery.View("web", 200, timestamps: false).BuildArguments();

        Assert.Equal(new[] { "logs", "-n", "200", "web" }, args);
    }

    [Fact]
    [Trait("case", "LG-001")]
    public void View_WithTimestamps_AddsDashT()
    {
        var args = LogQuery.View("web", 100, timestamps: true).BuildArguments();

        Assert.Equal(new[] { "logs", "-t", "-n", "100", "web" }, args);
    }

    [Fact]
    [Trait("case", "LG-002")]
    public void View_AllLines_OmitsDashN()
    {
        // 实测不带 -n 会把全部历史冲出来,这正是"全部"这一档想要的。
        var args = LogQuery.View("web", LogQuery.AllLines, timestamps: false).BuildArguments();

        Assert.Equal(new[] { "logs", "web" }, args);
    }

    [Fact]
    [Trait("case", "LG-003")]
    public void Live_BuildsFollowSinceAndNeverTail()
    {
        // 实时日志的硬契约:-f + --since,且**不发 -n** —— 起点之后的行不该被行数截断。
        var args = LogQuery.Live("web", 1_700_000_000, timestamps: true).BuildArguments();

        Assert.Equal(new[] { "logs", "-f", "-t", "--since", "1700000000", "web" }, args);
        Assert.DoesNotContain("-n", args);
    }

    [Fact]
    [Trait("case", "LG-005")]
    public void Live_WithoutSince_FailsBeforeStartingProcess()
    {
        // 没有起点的 -f 会先把全部历史冲出来(实测),那就成了"实时"装着"历史"。
        var query = new LogQuery { ContainerRef = "web", Follow = true };

        Assert.Throws<ArgumentException>(() => query.BuildArguments());
    }

    [Theory]
    [Trait("case", "LG-008")]
    [InlineData(-1)]
    [InlineData(100_001)]
    public void View_RejectsOutOfRangeTail_Early(int lines)
    {
        // 实测非法 -n 只会换回 8 行 usage(退出码 1),对用户毫无价值,所以在这里就拦掉。
        var query = LogQuery.View("web", lines, timestamps: false);

        Assert.Throws<ArgumentException>(() => query.BuildArguments());
    }

    [Fact]
    [Trait("case", "LG-007")]
    public void View_RejectsEmptyReference()
    {
        var query = LogQuery.View("   ", 200, timestamps: false);

        Assert.Throws<ArgumentException>(() => query.BuildArguments());
    }

    // ---------- 查看日志:一次性读取 ----------

    [Fact]
    [Trait("case", "LG-009")]
    public async Task Read_ReturnsPreparedLines_AndKeepsInteriorBlankLines()
    {
        var cli = new FakeWslcCli().Enqueue(0, "first\n\nthird\n");
        var logs = new LogsService(cli);

        var snapshot = await logs.ReadAsync(LogQuery.View("web", 200, timestamps: false));

        Assert.Equal(new[] { "first", "", "third" }, snapshot.Lines);   // 中间空行是日志的一部分,末尾空行是换行符产物
        Assert.Equal(3, snapshot.LineCount);
        Assert.Equal("first\r\n\r\nthird", snapshot.Text);
        Assert.Equal(new[] { "logs", "-n", "200", "web" }, Assert.Single(cli.Calls));
    }

    [Fact]
    [Trait("case", "LG-017")]
    public async Task Read_EmptyOutput_IsAValidEmptySnapshot_NotAnError()
    {
        // 实测:已停止或从未打印过东西的容器是"退出码 0 + 空输出"。
        var logs = new LogsService(new FakeWslcCli().Enqueue(0, ""));

        var snapshot = await logs.ReadAsync(LogQuery.View("web", 200, timestamps: false));

        Assert.True(snapshot.IsEmpty);
        Assert.Empty(snapshot.Lines);
    }

    [Fact]
    [Trait("case", "LG-013")]
    public async Task Read_NonZeroExit_QuietlyCarriesStdErrIntoMessage()
    {
        var stderr = FixtureLoader.Read("wslc-logs-not-found.txt");
        var logs = new LogsService(new FakeWslcCli().Enqueue(1, "", stderr));

        var error = await Assert.ThrowsAsync<WslcCliException>(
            () => logs.ReadAsync(LogQuery.View("nosuchcontainer", 200, timestamps: false)));

        Assert.Equal(LogsService.CommandName, error.Command);
        Assert.Contains("WSLC_E_CONTAINER_NOT_FOUND", error.Message);
    }

    [Fact]
    [Trait("case", "LG-006")]
    public async Task Read_RefusesFollowQuery_SoModesCannotBeMixedUp()
    {
        // 拿"要跟随的请求"去走一次性读取,只会得到一个永远不返回的进程 —— 直接拒绝。
        var logs = new LogsService(new FakeWslcCli());

        await Assert.ThrowsAsync<ArgumentException>(
            () => logs.ReadAsync(LogQuery.Live("web", 1_700_000_000, timestamps: false)));
    }

    // ---------- 实时日志:逐行回调 ----------

    [Fact]
    [Trait("case", "LG-014")]
    [Trait("case", "LG-004")]
    public async Task Follow_CallsBackEveryLineAsItArrives()
    {
        var cli = new FakeWslcCli().EnqueueStream(0, "one\ntwo\nthree");
        var logs = new LogsService(cli);
        var seen = new List<string>();

        var snapshot = await logs.FollowAsync(
            LogQuery.Live("web", 1_700_000_000, timestamps: false),
            seen.Add);

        Assert.Equal(new[] { "one", "two", "three" }, seen);
        Assert.Equal(seen, snapshot.Lines);
        var args = Assert.Single(cli.StreamCalls);
        Assert.Equal(new[] { "logs", "-f", "--since", "1700000000", "web" }, args);
    }

    [Fact]
    [Trait("case", "LG-011")]
    public async Task Follow_PrettiesTimestampsOnlyWhenAsked()
    {
        var raw = FixtureLoader.Read("wslc-logs-timestamps.txt");
        var cli = new FakeWslcCli().Enqueue(0, raw).Enqueue(0, raw);   // 两次读取各排一份结果
        var logs = new LogsService(cli);

        var plain = await logs.ReadAsync(LogQuery.View("web", 200, timestamps: false));
        var stamped = await logs.ReadAsync(LogQuery.View("web", 200, timestamps: true));

        Assert.StartsWith("2026-10-12T11:48:46", plain.Lines.First());   // 没勾选就不动行首
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3} log line 741", stamped.Lines.First());
    }

    [Fact]
    [Trait("case", "LG-022")]
    public async Task Follow_CanceledKeepsLinesAlreadyHandedOut()
    {
        // 取消时已回调出去的行不回滚 —— 界面因此能留住用户已经看到的日志。
        var gate = new TaskCompletionSource<WslcCliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = new FakeWslcCli
        {
            StreamHandler = (_, onLine, token) =>
            {
                onLine?.Invoke("kept-1");
                onLine?.Invoke("kept-2");
                token.Register(() => gate.TrySetCanceled(token));
                return gate.Task;
            },
        };
        var logs = new LogsService(cli);
        var seen = new List<string>();
        using var cts = new CancellationTokenSource();

        var pending = logs.FollowAsync(LogQuery.Live("web", 1_700_000_000, timestamps: false), seen.Add, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(new[] { "kept-1", "kept-2" }, seen);
    }

    [Fact]
    [Trait("case", "LG-025")]
    public async Task Follow_NonZeroExit_ThrowsAfterStreaming()
    {
        var cli = new FakeWslcCli().EnqueueStream(1, "partial", "boom");
        var logs = new LogsService(cli);

        await Assert.ThrowsAsync<WslcCliException>(
            () => logs.FollowAsync(LogQuery.Live("web", 1_700_000_000, timestamps: false), _ => { }));
    }
}

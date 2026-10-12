using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>
/// 功能 005 · AC1/AC2/AC6/AC7/AC8 的服务层:
/// 命令拼装严格匹配、非法输入不启动进程、错误抛异常、跟随逐行立刻回调、取消不回滚已交付的行。
/// </summary>
public class LogsServiceTests
{
    private static LogQuery Read(string reference = "probe1", int tail = 200, bool timestamps = false) =>
        new() { ContainerRef = reference, TailLines = tail, Timestamps = timestamps };

    private static LogQuery Follow(string reference = "probe1", int tail = 200) =>
        new() { ContainerRef = reference, TailLines = tail, Follow = true };

    // ---------- 一次性读取 ----------

    [Fact]
    public async Task ReadAsync_BuildsExactArguments_AndReturnsPreparedLines()
    {
        var cli = new FakeWslcCli().Enqueue(0, "log line 1\nlog line 2\n");
        var service = new LogsService(cli);

        var snapshot = await service.ReadAsync(Read(" probe1 ", 100));

        var args = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "logs", "-n", "100", "probe1" }, args);
        Assert.Equal(2, snapshot.LineCount);
        Assert.Equal("log line 1\r\nlog line 2", snapshot.Text);
    }

    [Fact]
    public async Task ReadAsync_AllLines_OmitsTailFlag()
    {
        var cli = new FakeWslcCli().Enqueue(0, "a");

        await new LogsService(cli).ReadAsync(Read(tail: LogQuery.AllLines));

        Assert.Equal(new[] { "logs", "probe1" }, Assert.Single(cli.Calls));
    }

    [Fact]
    public async Task ReadAsync_EmptyOutputFromStoppedContainer_IsEmptySnapshotNotError()
    {
        // 实测:已退出 / 从未启动的容器 → 退出码 0 + 空输出,这是正常语义(AC10 的依据)。
        var cli = new FakeWslcCli().Enqueue(0, "");

        var snapshot = await new LogsService(cli).ReadAsync(Read());

        Assert.True(snapshot.IsEmpty);
        Assert.Equal(0, snapshot.LineCount);
    }

    [Fact]
    public async Task ReadAsync_NonZeroExit_ThrowsCliExceptionForLogsCommand()
    {
        var cli = new FakeWslcCli().Enqueue(1, "", FixtureLoader.Read("wslc-logs-not-found.txt"));

        var ex = await Assert.ThrowsAsync<WslcCliException>(
            () => new LogsService(cli).ReadAsync(Read("nosuchcontainer")));

        Assert.Equal("wslc logs", ex.Command);
        Assert.Equal(1, ex.Result.ExitCode);
        // AC6:界面用 WslcError.Describe 得到单句,样板句被去掉。
        var described = WslcError.Describe(ex);
        Assert.Contains("WSLC_E_CONTAINER_NOT_FOUND", described);
        Assert.DoesNotContain("如果此错误是意外错误", described);
    }

    [Fact]
    public async Task ReadAsync_FormatTimestamps_OnlyWhenRequested()
    {
        var raw = FixtureLoader.Read("wslc-logs-timestamps.txt");

        var withFlags = new LogsService(new FakeWslcCli().Enqueue(0, raw));
        var plain = new LogsService(new FakeWslcCli().Enqueue(0, raw));

        var pretty = await withFlags.ReadAsync(Read(timestamps: true));
        var untouched = await plain.ReadAsync(Read(timestamps: false));

        Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3} ", pretty.Lines[0]);
        Assert.StartsWith("2026-10-12T11:48:46", untouched.Lines[0]); // 没开选项就绝不改动
    }

    // ---------- AC2 · 校验发生在启动进程之前 ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReadAsync_BlankContainerRef_ThrowsWithoutInvokingCli(string reference)
    {
        var cli = new FakeWslcCli();

        await Assert.ThrowsAsync<ArgumentException>(() => new LogsService(cli).ReadAsync(Read(reference)));

        Assert.Empty(cli.Calls); // 实测非法参数只会换回 8 行 usage,必须提前拦住
    }

    [Fact]
    public async Task ReadAsync_NegativeTail_ThrowsWithoutInvokingCli()
    {
        var cli = new FakeWslcCli();

        await Assert.ThrowsAsync<ArgumentException>(() => new LogsService(cli).ReadAsync(Read(tail: -1)));

        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task ReadAsync_FollowQueryGoesToRead_ThrowsMismatch()
    {
        // 一次性读取与跟随必须是两个明确的入口,拼错直接拒绝,免得挂住进程。
        var cli = new FakeWslcCli();
        var service = new LogsService(cli);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadAsync(Follow()));

        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task FollowAsync_NonFollowQuery_ThrowsMismatch()
    {
        var cli = new FakeWslcCli();
        var service = new LogsService(cli);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.FollowAsync(Read(), _ => { }));

        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task FollowAsync_NullCallback_ThrowsBeforeInvokingCli()
    {
        var cli = new FakeWslcCli();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => new LogsService(cli).FollowAsync(Follow(), null!));

        Assert.Empty(cli.Calls);
    }

    // ---------- AC7 · 跟随逐行回调 ----------

    [Fact]
    public async Task FollowAsync_InvokesCallbackAsLinesArrive_NotAfterProcessExit()
    {
        var release = new TaskCompletionSource<WslcCliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<string>();
        var firstLineDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cli = new FakeWslcCli
        {
            StreamHandler = (args, onLine, token) =>
            {
                onLine!("log line 1");
                received.Add("invoked");
                firstLineDelivered.SetResult();
                return release.Task; // "进程"还挂着,没退出
            },
        };

        var delivered = new List<string>();
        var followTask = new LogsService(cli).FollowAsync(Follow(), delivered.Add);

        await firstLineDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // 行在进程结束前就到了
        Assert.Equal(new[] { "log line 1" }, delivered);
        Assert.False(followTask.IsCompleted);

        release.SetResult(new WslcCliResult(0, "log line 1", ""));
        var snapshot = await followTask;

        Assert.Equal(1, snapshot.LineCount);
        Assert.Equal(new[] { "logs", "-f", "-n", "200", "probe1" }, Assert.Single(cli.StreamCalls));
    }

    [Fact]
    public async Task FollowAsync_ScriptedStream_HandsOverEveryLineInOrder()
    {
        var cli = new FakeWslcCli().EnqueueStream(0, "a\nb\nc\n");
        var received = new List<string>();

        var snapshot = await new LogsService(cli).FollowAsync(Follow(), received.Add);

        Assert.Equal(new[] { "a", "b", "c" }, received);
        Assert.Equal(3, snapshot.LineCount);
    }

    [Fact]
    public async Task FollowAsync_CallbackFailure_SurfacesOnceAndStopsTheStream()
    {
        // AC7:调用方回调抛异常不能把读循环带崩(卡死),要作为一次错误交还调用方。
        var cli = new FakeWslcCli().EnqueueStream(0, "a\nb\nc\n");
        var received = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LogsService(cli).FollowAsync(Follow(), line =>
            {
                received.Add(line);
                if (line == "b") throw new InvalidOperationException("回调崩了");
            }));

        Assert.Equal(new[] { "a", "b" }, received); // c 没被处理,异常只上抛一次
    }

    [Fact]
    public async Task FollowAsync_NonZeroExit_ThrowsCliException()
    {
        var cli = new FakeWslcCli().EnqueueStream(1, "", FixtureLoader.Read("wslc-logs-not-found.txt"));

        var ex = await Assert.ThrowsAsync<WslcCliException>(
            () => new LogsService(cli).FollowAsync(Follow("nosuchcontainer"), _ => { }));

        Assert.Contains("WSLC_E_CONTAINER_NOT_FOUND", ex.Result.StdErr);
    }

    // ---------- AC8 · 取消 ----------

    [Fact]
    public async Task FollowAsync_Cancellation_ThrowsOperationCanceled_AndKeepsDeliveredLines()
    {
        var cli = new FakeWslcCli
        {
            StreamHandler = async (args, onLine, token) =>
            {
                onLine!("keep-me");
                await Task.Delay(Timeout.Infinite, token); // 模拟还在流的进程被取消带走
                return new WslcCliResult(0, "keep-me", "");
            },
        };
        using var cts = new CancellationTokenSource();
        var delivered = new List<string>();

        var followTask = new LogsService(cli).FollowAsync(Follow(), delivered.Add, cts.Token);
        await WaitUntilAsync(() => delivered.Count > 0);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => followTask);
        Assert.Equal(new[] { "keep-me" }, delivered); // 已交付的行不回滚(界面保留日志)
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), "等待条件超时");
    }
}

using System.Diagnostics;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>
/// 功能 005 针对真实 wslc 的集成测试。默认跳过,只在设置了环境变量
/// WSLC_DESKTOP_INTEGRATION=1 时运行(单元测试不应依赖本机是否装了 WSL,
/// 也不应在 CI 上创建容器)。
///
/// 运行:
///   $env:WSLC_DESKTOP_INTEGRATION = "1"
///   dotnet test --filter FullyQualifiedName~RealWslcLogsIntegrationTests
///
/// 每个测试都自建专用的日志容器并负责删除,绝不碰用户已有的容器。
/// 依据的都是规格里实测过的行为:停止/未启动的容器返回"退出码 0 + 空输出";
/// logs -f 是真流式;跟随时取消要整棵进程树终止且不残留。
/// </summary>
public class RealWslcLogsIntegrationTests
{
    private const string Enabled = "WSLC_DESKTOP_INTEGRATION";
    private const string ImageVariable = "WSLC_DESKTOP_IT_IMAGE";
    private const string DefaultImage = "docker.1ms.run/library/busybox:latest";

    private static readonly LogQuery NeverStartedQuery = new() { ContainerRef = "   " };

    [Fact]
    public async Task Logs_MissingContainer_ThrowsNotFound_AndNeverBlankRef()
    {
        if (Environment.GetEnvironmentVariable(Enabled) != "1") return;

        // AC2:空白容器标识在应用侧就被拦下,根本不启动进程。
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new LogsService(new ProcessWslcCli()).ReadAsync(NeverStartedQuery));

        var cli = new ProcessWslcCli();
        var service = new LogsService(cli);

        var ex = await Assert.ThrowsAsync<WslcCliException>(() =>
            service.ReadAsync(new LogQuery { ContainerRef = "wslcd-it-no-such-container" }));

        Assert.Equal("wslc logs", ex.Command);
        Assert.Contains("WSLC_E_CONTAINER_NOT_FOUND", ex.Result.StdErr);
        // AC6:清洗后的文案是单句,不含"欢迎提 issue"样板。
        var described = WslcError.Describe(ex);
        Assert.Contains("找不到容器", described);
        Assert.DoesNotContain("如果此错误是意外错误", described);
    }

    [Fact]
    public async Task Logs_NeverStartedContainer_IsEmptyOutputNotError()
    {
        if (Environment.GetEnvironmentVariable(Enabled) != "1") return;

        var cli = new ProcessWslcCli();
        var name = $"wslcd-logs-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            var created = await cli.RunAsync(new[] { "create", "--name", name, Image(), "true" });
            Assert.True(created.ExitCode == 0, $"wslc create 失败:{created.StdErr}");

            // 实测:从未启动的容器 logs → 退出码 0 + 空输出(AC10 的直接对应)。
            var snapshot = await new LogsService(cli).ReadAsync(
                new LogQuery { ContainerRef = name });
            Assert.True(snapshot.IsEmpty, $"预期空日志,实际 {snapshot.LineCount} 行");
        }
        finally
        {
            await cli.RunAsync(new[] { "remove", "-f", name });
        }
    }

    [Fact]
    public async Task Logs_TailAndTimestampsAndFollow_OnRealStreamingContainer()
    {
        if (Environment.GetEnvironmentVariable(Enabled) != "1") return;

        var cli = new ProcessWslcCli();
        var service = new LogsService(cli);
        var containers = new ContainersService(cli);
        var name = $"wslcd-logs-{Guid.NewGuid().ToString("N")[..8]}";

        // 和规格实测同款的日志发生器:每秒一行。
        var wslcBefore = WslcProcessCount();

        try
        {
            var created = await cli.RunAsync(new[]
            {
                "create", "--name", name, Image(),
                "sh", "-c", "i=0; while true; do echo log-$i; i=$((i+1)); sleep 1; done",
            });
            Assert.True(created.ExitCode == 0, $"wslc create 失败:{created.StdErr}");
            await containers.StartAsync(created.StdOut.Trim());

            // 一次性读取:等容器至少打出几行,再验证 -n 生效(实测 logs 有输出)。
            var deadline = DateTime.UtcNow.AddSeconds(40);
            LogSnapshot snapshot = LogSnapshot.Empty;
            while (DateTime.UtcNow < deadline)
            {
                snapshot = await service.ReadAsync(new LogQuery { ContainerRef = name, TailLines = 5 });
                if (!snapshot.IsEmpty) break;
                await Task.Delay(1000);
            }
            Assert.False(snapshot.IsEmpty, "40 秒内没读到任何日志行");
            Assert.True(snapshot.LineCount <= 5, $"-n 5 应最多 5 行,实际 {snapshot.LineCount}");

            // -t:UTC 纳秒前缀应被转成本地 时:分:秒.毫秒(AC5)。
            var stamped = await service.ReadAsync(new LogQuery
            {
                ContainerRef = name,
                TailLines = 3,
                Timestamps = true,
            });
            Assert.NotEmpty(stamped.Lines);
            Assert.All(stamped.Lines, line => Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3} log-\d+$", line));

            // 跟随:进程不会自己结束(容器一直活着),回调却能拿到行 → 确实是流式(AC7)。
            using var cts = new CancellationTokenSource();
            var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var followTask = service.FollowAsync(
                new LogQuery { ContainerRef = name, TailLines = 1, Follow = true },
                line => received.Enqueue(line),
                cts.Token);

            var followDeadline = DateTime.UtcNow.AddSeconds(40);
            var historySeen = received.Count;
            while (received.Count <= historySeen + 1 && DateTime.UtcNow < followDeadline)
            {
                await Task.Delay(250);
            }
            Assert.True(received.Count > historySeen + 1, "跟随期间没有新行到达(流式失效?)");

            // AC8:取消 → OperationCanceledException,已交付的行保留。
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => followTask);

            // 实测:整树终止后不应留下还在写日志的 wslc 进程(留 10 秒缓冲时间)。
            var cleanupDeadline = DateTime.UtcNow.AddSeconds(10);
            while (WslcProcessCount() > wslcBefore && DateTime.UtcNow < cleanupDeadline)
            {
                await Task.Delay(250);
            }
            Assert.True(WslcProcessCount() <= wslcBefore, "取消跟随后仍残留 wslc 进程");

            // 取消跟随不影响容器:还能读到日志。
            var afterCancel = await service.ReadAsync(new LogQuery { ContainerRef = name, TailLines = 2 });
            Assert.False(afterCancel.IsEmpty);
        }
        finally
        {
            await cli.RunAsync(new[] { "remove", "-f", name });
        }
    }

    private static string Image() =>
        Environment.GetEnvironmentVariable(ImageVariable) ?? DefaultImage;

    private static int WslcProcessCount()
    {
        try
        {
            return Process.GetProcessesByName("wslc").Length;
        }
        catch
        {
            return 0; // 数不了就不当作残留
        }
    }
}

using wsld.Core.Models;
using wsld.Core.Services;
using wsld.Core.WslcCli;

namespace wsld.Tests;

/// <summary>
/// 日志页签对真实 wslc 的端到端冒烟(册子 LG-001、LG-003、LG-011、LG-013、LG-017)。默认跳过,只在 WSLD_INTEGRATION=1 时运行。
/// 运行:
/// $env:WSLD_INTEGRATION = "1"
/// dotnet test --filter FullyQualifiedName~RealWslcLogsIntegrationTests
///
/// 它自建一个"每秒打印一行"的专用容器,跑完就删,不碰用户已有的容器。
/// 之所以要真容器:两条读法的差别(实时**不加载历史**)只在真流上才看得出来。
/// </summary>
public class RealWslcLogsIntegrationTests
{
    private const string Enabled = "WSLD_INTEGRATION";
    private const string ImageVariable = "WSLD_IT_IMAGE";
    private const string DefaultImage = "docker.1ms.run/library/busybox:latest";

    [Fact]
    [Trait("case", "LG-001")]
    [Trait("case", "LG-003")]
    [Trait("case", "LG-011")]
    public async Task BothReadModes_OnRealWslc()
    {
        if (Environment.GetEnvironmentVariable(Enabled) != "1") return;

        var path = WslcLocator.Find();
        Assert.NotNull(path);

        var cli = new ProcessWslcCli(path);
        var containers = new ContainersService(cli);
        var logs = new LogsService(cli);
        var name = $"wslcd-it-logs-{Guid.NewGuid().ToString("N")[..8]}";
        var image = Environment.GetEnvironmentVariable(ImageVariable) ?? DefaultImage;

        try
        {
            var created = await cli.RunAsync(new[]
            {
                "create", "--name", name, image,
                "sh", "-c", "i=0; while true; do echo \"it log line $i\"; i=$((i+1)); sleep 1; done",
            });
            Assert.True(created.ExitCode == 0, $"wslc create 失败:{created.StdErr}");

            var shortId = TrimId(created.StdOut.Trim());
            var started = await cli.RunAsync(new[] { "start", shortId });
            Assert.True(started.ExitCode == 0, $"wslc start 失败:{started.StdErr}");

            // 先让容器产出几行,否则"读到历史"和"没读到"分不清。
            await Task.Delay(3_500);

            // ---------- 查看日志:一次性读,带 -n 只看尾部几行 ----------
            var tail = await logs.ReadAsync(LogQuery.View(shortId, 2, timestamps: false));
            Assert.Equal(2, tail.LineCount);
            Assert.All(tail.Lines, line => Assert.StartsWith("it log line", line));

            var stamped = await logs.ReadAsync(LogQuery.View(shortId, 3, timestamps: true));
            Assert.Equal(3, stamped.LineCount);
            Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3} it log line ", stamped.Lines[0]);

            // ---------- 实时日志:起点之后的行,一行历史都不该有 ----------
            var seen = new List<string>();
            using var cts = new CancellationTokenSource();
            var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var following = logs.FollowAsync(LogQuery.Live(shortId, since, timestamps: false), seen.Add, cts.Token);

            await Task.Delay(2_500);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => following);

            Assert.NotEmpty(seen);
            Assert.All(seen, line => Assert.StartsWith("it log line", line));

            // ---------- 取消后不该留下半死的 wslc 进程 ----------
            await Task.Delay(1_000);
            var leftovers = System.Diagnostics.Process.GetProcessesByName("wslc")
                .Where(p => p.Id != Environment.ProcessId)
                .ToList();
            Assert.Empty(leftovers);
            foreach (var process in leftovers) process.Dispose();
        }
        finally
        {
            await cli.RunAsync(new[] { "remove", "-f", name }); // 尽力清理,失败不掩盖测试结论
        }
    }

    [Fact]
    [Trait("case", "LG-013")]
    public async Task ReadLogs_OfUnknownContainer_ReportsEngineError()
    {
        if (Environment.GetEnvironmentVariable(Enabled) != "1") return;

        var path = WslcLocator.Find();
        Assert.NotNull(path);

        var logs = new LogsService(new ProcessWslcCli(path));

        var error = await Assert.ThrowsAsync<WslcCliException>(
            () => logs.ReadAsync(LogQuery.View("wslcd-it-does-not-exist", 10, timestamps: false)));

        Assert.Contains("WSLC_E_CONTAINER_NOT_FOUND", error.Result.StdErr);
    }

    [Fact]
    [Trait("case", "LG-017")]
    public async Task StoppedContainer_WithNoOutput_IsEmptyNotFailure()
    {
        if (Environment.GetEnvironmentVariable(Enabled) != "1") return;

        var path = WslcLocator.Find();
        Assert.NotNull(path);

        var cli = new ProcessWslcCli(path);
        var logs = new LogsService(cli);
        var name = $"wslcd-it-empty-{Guid.NewGuid().ToString("N")[..8]}";
        var image = Environment.GetEnvironmentVariable(ImageVariable) ?? DefaultImage;

        try
        {
            var created = await cli.RunAsync(new[] { "create", "--name", name, image, "sleep", "1" });
            Assert.True(created.ExitCode == 0, $"wslc create 失败:{created.StdErr}");

            var snapshot = await logs.ReadAsync(LogQuery.View(TrimId(created.StdOut.Trim()), 10, timestamps: false));

            Assert.True(snapshot.IsEmpty);   // 从未启动过的容器:退出码 0 + 空输出,界面要能说"没有输出"
        }
        finally
        {
            await cli.RunAsync(new[] { "remove", "-f", name });
        }
    }

    /// <summary>wslc list 用的是 12 位短 ID,后面拿它去 logs / remove 都对得上。</summary>
    private static string TrimId(string id) => id.Length > 12 ? id[..12] : id;
}

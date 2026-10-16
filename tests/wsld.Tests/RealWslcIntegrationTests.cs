using wsld.Core.Models;
using wsld.Core.Services;
using wsld.Core.WslcCli;

namespace wsld.Tests;

/// <summary>
/// 针对真实 wslc 的端到端冒烟测试。默认跳过,只在设置了环境变量
/// WSLD_INTEGRATION=1 时运行(单元测试不应该依赖本机是否装了 WSL,
/// 也不应该在 CI 上创建容器)。
///
/// 运行:
///   $env:WSLD_INTEGRATION = "1"
///   dotnet test --filter FullyQualifiedName~RealWslcIntegrationTests
///
/// 它会自己创建并删除一个专用容器,不碰用户已有的容器。
/// </summary>
public class RealWslcIntegrationTests
{
    private const string Enabled = "WSLD_INTEGRATION";
    private const string ImageVariable = "WSLD_IT_IMAGE";
    private const string DefaultImage = "docker.1ms.run/library/busybox:latest";

    [Fact]
    public async Task LifecycleRoundTrip_OnRealWslc()
    {
        if (Environment.GetEnvironmentVariable(Enabled) != "1") return;

        var path = WslcLocator.Find();
        Assert.NotNull(path);

        var cli = new ProcessWslcCli(path);
        var service = new ContainersService(cli);
        var name = $"wslcd-it-{Guid.NewGuid().ToString("N")[..8]}";
        var image = Environment.GetEnvironmentVariable(ImageVariable) ?? DefaultImage;

        try
        {
            var created = await cli.RunAsync(new[] { "create", "--name", name, image, "sleep", "3600" });
            Assert.True(created.ExitCode == 0, $"wslc create 失败:{created.StdErr}");

            var shortId = created.StdOut.Trim();
            Assert.NotEmpty(shortId);
            if (shortId.Length > 12) shortId = shortId[..12]; // wslc list 用的是 12 位短 ID

            Assert.True(await WaitUntilAsync(service, shortId, state => state == "created"),
                "新建后应为 created");

            await service.StartAsync(shortId);
            Assert.True(await WaitUntilAsync(service, shortId, state => state == "running"),
                "start 之后应变为 running");

            await service.StopAsync(shortId);
            Assert.True(await WaitUntilAsync(service, shortId, state => state != "running"),
                "stop 之后应不再是 running");

            await service.RestartAsync(shortId);
            Assert.True(await WaitUntilAsync(service, shortId, state => state == "running"),
                "restart 之后应为 running");

            // 运行中直接删除必须失败,这正是 UI 需要 -f 的实测依据(功能 002)。
            var refused = await Assert.ThrowsAsync<WslcCliException>(() => service.RemoveAsync(shortId));
            Assert.Contains("WSLC_E_CONTAINER_IS_RUNNING", refused.Result.StdErr);

            await service.RemoveAsync(shortId, force: true);
            Assert.True(await WaitUntilAsync(service, shortId, _ => true, expectPresent: false),
                "删除之后不应再出现在列表里");
        }
        finally
        {
            await cli.RunAsync(new[] { "remove", "-f", name }); // 尽力清理,失败不掩盖测试结论
        }
    }

    [Fact]
    public async Task ListAsync_OnRealWslc_ReturnsWithoutError()
    {
        if (Environment.GetEnvironmentVariable(Enabled) != "1") return;

        var path = WslcLocator.Find();
        Assert.NotNull(path);

        var containers = await new ContainersService(new ProcessWslcCli(path)).ListAsync();
        Assert.All(containers, c => Assert.NotEmpty(c.Id));
    }

    /// <summary>轮询列表,直到指定容器的 State 满足条件(或按 expectPresent 消失)。</summary>
    private static async Task<bool> WaitUntilAsync(
        ContainersService service,
        string shortId,
        Func<string, bool> statePredicate,
        bool expectPresent = true,
        int timeoutSeconds = 25)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var container = (await service.ListAsync()).FirstOrDefault(c => c.Id == shortId);
            if (expectPresent)
            {
                if (container is not null && statePredicate(container.State)) return true;
            }
            else if (container is null)
            {
                return true;
            }
            await Task.Delay(500);
        }
        return false;
    }
}
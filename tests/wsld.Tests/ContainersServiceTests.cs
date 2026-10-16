using wsld.Core.Services;
using wsld.Core.WslcCli;

namespace wsld.Tests;

/// <summary>功能 001:服务层的命令参数与错误传播。</summary>
public class ContainersServiceTests
{
    [Fact]
    public async Task ListAsync_InvokesWslcWithMachineReadableArgs()
    {
        var cli = new FakeWslcCli().Enqueue(0, "[]");
        var service = new ContainersService(cli);

        await service.ListAsync();

        var args = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "list", "-a", "--format", "json" }, args);
    }

    [Fact]
    public async Task ListAsync_EmptyOutput_ReturnsEmptyList()
    {
        var cli = new FakeWslcCli().Enqueue(0, "");
        var service = new ContainersService(cli);

        Assert.Empty(await service.ListAsync());
    }

    [Fact]
    public async Task ListAsync_NonZeroExit_ThrowsWithStderrMessage()
    {
        var cli = new FakeWslcCli().Enqueue(1, "", "错误代码： E_FAIL");
        var service = new ContainersService(cli);

        var ex = await Assert.ThrowsAsync<WslcCliException>(() => service.ListAsync());
        Assert.Contains("E_FAIL", ex.Message);   // AC9:文案包含 stderr
        Assert.Contains("退出码 1", ex.Message);
    }

    [Fact]
    public async Task ListAsync_ReturnsParsedContainers()
    {
        var cli = new FakeWslcCli().Enqueue(0, FixtureLoader.Read("wslc-list-running.ndjson"));
        var service = new ContainersService(cli);

        var items = await service.ListAsync();

        var probe = Assert.Single(items);
        Assert.Equal("probe1", probe.DisplayName);
    }
}
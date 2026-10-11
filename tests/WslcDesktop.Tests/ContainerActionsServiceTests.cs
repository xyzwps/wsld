using WslcDesktop.Core.Services;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>功能 002 · AC1–AC4:ContainersService 与错误文案清洗。</summary>
public class ContainerActionsServiceTests
{
    [Fact]
    public async Task Start_InvokesWslcStartWithId()
    {
        var (svc, cli) = Create();
        await svc.StartAsync("f47fdddf9219");
        Assert.Equal(new[] { "start", "f47fdddf9219" }, cli.Calls.Single());
    }

    [Fact]
    public async Task Stop_InvokesWslcStopWithId()
    {
        var (svc, cli) = Create();
        await svc.StopAsync("f47fdddf9219");
        Assert.Equal(new[] { "stop", "f47fdddf9219" }, cli.Calls.Single());
    }

    [Fact]
    public async Task Restart_InvokesWslcRestartWithId()
    {
        var (svc, cli) = Create();
        await svc.RestartAsync("f47fdddf9219");
        Assert.Equal(new[] { "restart", "f47fdddf9219" }, cli.Calls.Single());
    }

    [Fact]
    public async Task Remove_InvokesWslcRemoveWithId()
    {
        var (svc, cli) = Create();
        await svc.RemoveAsync("f47fdddf9219");
        Assert.Equal(new[] { "remove", "f47fdddf9219" }, cli.Calls.Single());
    }

    [Fact]
    public async Task Remove_Force_AddsForceFlagBeforeId()
    {
        var (svc, cli) = Create();
        await svc.RemoveAsync("f47fdddf9219", force: true);
        Assert.Equal(new[] { "remove", "-f", "f47fdddf9219" }, cli.Calls.Single());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyId_Throws_AndNeverCallsWslc(string id)
    {
        var (svc, cli) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => svc.StartAsync(id));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.StopAsync(id));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RestartAsync(id));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RemoveAsync(id, force: true));

        Assert.Empty(cli.Calls); // AC2:防护必须发生在调用 wslc 之前
    }

    [Fact]
    public async Task NonZeroExit_ThrowsWslcCliExceptionCarryingStderr()
    {
        var cli = new FakeWslcCli().AlwaysReturn(1, "", "容器 'x' 正在运行,无法删除。");
        var svc = new ContainersService(cli);

        var ex = await Assert.ThrowsAsync<WslcCliException>(() => svc.RemoveAsync("x"));

        Assert.Equal("wslc remove", ex.Command);
        Assert.Contains("正在运行,无法删除", ex.Message); // AC3
        Assert.Equal(1, ex.Result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_DispatchesEveryAction()
    {
        var (svc, cli) = Create();

        await svc.RunAsync(ContainerAction.Start, "abc");
        await svc.RunAsync(ContainerAction.Stop, "abc");
        await svc.RunAsync(ContainerAction.Restart, "abc");
        await svc.RunAsync(ContainerAction.Remove, "abc", force: true);

        Assert.Equal(4, cli.Calls.Count);
        Assert.Equal("start", cli.Calls[0][0]);
        Assert.Equal("stop", cli.Calls[1][0]);
        Assert.Equal("restart", cli.Calls[2][0]);
        Assert.Equal(new[] { "remove", "-f", "abc" }, cli.Calls[3]);
    }

    private static (ContainersService Service, FakeWslcCli Cli) Create()
    {
        var cli = new FakeWslcCli().AlwaysReturn(0);
        return (new ContainersService(cli), cli);
    }
}

/// <summary>功能 002 · AC4:wslc stderr 的清洗规则,样例取自 wslc 3.0.1 真实输出。</summary>
public class WslcErrorTests
{
    public const string NotFoundStderr =
        "找不到容器 'no-such-container-xyz'。" + "\n" +
        "错误代码： WSLC_E_CONTAINER_NOT_FOUND" + "\n" +
        "如果此错误是意外错误，请考虑搜索现有问题或在 https://github.com/microsoft/WSL/issues 提交新问题。" + "\n";

    public const string RunningStderr =
        "容器 'f47fdddf9219' 正在运行，无法删除。在删除容器之前停止容器或使用强制删除 (-f)。" + "\n" +
        "错误代码： WSLC_E_CONTAINER_IS_RUNNING" + "\n" +
        "如果此错误是意外错误，请考虑搜索现有问题或在 https://github.com/microsoft/WSL/issues 提交新问题。" + "\n";

    [Fact]
    public void Describe_RemovesBoilerplateAndJoinsLines()
    {
        Assert.Equal(
            "找不到容器 'no-such-container-xyz'。 错误代码： WSLC_E_CONTAINER_NOT_FOUND",
            WslcError.Describe(NotFoundStderr));
    }

    [Fact]
    public void Describe_KeepsErrorCode()
    {
        var text = WslcError.Describe(RunningStderr);
        Assert.Contains("WSLC_E_CONTAINER_IS_RUNNING", text);
        Assert.DoesNotContain("提交新问题", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n")]
    public void Describe_FallsBackWhenStderrIsEmpty(string? stderr)
    {
        Assert.Equal(WslcError.FallbackMessage, WslcError.Describe(stderr));
    }

    [Fact]
    public void Describe_OnlyBoilerplate_FallsBack()
    {
        Assert.Equal(WslcError.FallbackMessage, WslcError.Describe("如果此错误是意外错误，请提交问题。"));
    }

    [Fact]
    public void Describe_OtherExceptionTypes_UsesMessage()
    {
        Assert.Equal("第 1 行 无法解析", WslcError.Describe(new FormatException("第 1 行 无法解析")));
    }
}
using wsld.Core.Models;
using wsld.Core.Services;
using wsld.Core.ViewModels;
using wsld.Core.WslcCli;

namespace wsld.Tests;

/// <summary>册子 CA-009、CA-011、CA-015–CA-017:详情头部四颗操作键的可用性与操作收尾。</summary>
public class ContainerDetailViewModelTests
{
    private const string ExitedJson = """{"ID":"aaaa11112222","Names":"web","Image":"nginx:latest","State":"exited","Status":"Exited (137)","Command":"nginx","Networks":"bridge"}""";

    [Fact]
    [Trait("case", "CA-009")]
    [Trait("case", "CA-011")]
    public async Task BusyIsClearedOnTheUiThreadAndEventsFireOnTheUiThread()
    {
        var cli = new GateCli();
        using var pump = new PumpUiThread();
        var detail = new ContainerDetailViewModel(new ContainersService(cli), pump);
        detail.InitializeCommands(_ => Task.CompletedTask);

        await detail.LoadAsync("aaaa11112222");

        var busyFalseThreads = new List<int>();
        var refreshRequestedThreads = new List<int>();
        detail.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName == nameof(ContainerDetailViewModel.IsBusy) && !detail.IsBusy)
            {
                busyFalseThreads.Add(Environment.CurrentManagedThreadId);
            }
        };
        detail.RefreshRequested += (_, _) => refreshRequestedThreads.Add(Environment.CurrentManagedThreadId);

        var pending = detail.StartCommand.ExecuteAsync(null);
        Assert.True(detail.IsBusy);
        cli.Release();
        await pending;

        Assert.Equal(pump.UiThreadId, Assert.Single(busyFalseThreads));
        Assert.Equal(pump.UiThreadId, Assert.Single(refreshRequestedThreads));
        Assert.False(detail.IsBusy);
    }

    // ---------- 四个操作键的可用性(行内只留启动,其余都在详情里) ----------

    [Theory]
    [Trait("case", "CA-015")]
    [Trait("case", "CA-016")]
    [Trait("case", "CA-017")]
    [InlineData("running", false, true, true, true)]
    [InlineData("exited", true, false, true, true)]
    public void ButtonAvailability_RunsOnState(string state, bool canStart, bool canStop, bool canRestart, bool canRemove)
    {
        var detail = new ContainerDetailViewModel(new ContainersService(new FakeWslcCli()));
        detail.InitializeCommands(_ => Task.CompletedTask);   // RaiseActionStates 要摸命令对象

        detail.ApplyModel(new ContainerInfo { Id = "aaaa11112222", Names = "web", State = state, Status = "s" });

        Assert.Equal(canStart, detail.CanStart);
        Assert.Equal(canStop, detail.CanStop);
        Assert.Equal(canRestart, detail.CanRestart);
        Assert.Equal(canRemove, detail.CanRemove);
    }

    /// <summary>list 立即返回(始终是同一个已停止容器),其它命令挂起直到 Release。</summary>
    private sealed class GateCli : IWslcCli
    {
        private readonly TaskCompletionSource<WslcCliResult> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            arguments[0] == "list"
                ? Task.FromResult(new WslcCliResult(0, ExitedJson, ""))
                : _gate.Task;

        public Task<WslcCliResult> RunLinesAsync(IReadOnlyList<string> arguments, Action<string>? onStdoutLine, CancellationToken cancellationToken = default) =>
            RunAsync(arguments, cancellationToken);

        public Task<WslcCliResult> RunWithConsoleAsync(IReadOnlyList<string> arguments, Action<IReadOnlyList<string>>? onVisibleLines, CancellationToken cancellationToken = default) =>
            RunAsync(arguments, cancellationToken);

        public void Release() => _gate.TrySetResult(new WslcCliResult(0, "", ""));
    }
}

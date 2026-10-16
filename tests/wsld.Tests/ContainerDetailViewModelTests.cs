using wsld.Core.Models;
using wsld.Core.Services;
using wsld.Core.ViewModels;
using wsld.Core.WslcCli;

namespace wsld.Tests;

/// <summary>容器详情面板:操作收尾(状态清零与对外事件)必须回到 UI 线程。</summary>
public class ContainerDetailViewModelTests
{
    private const string ExitedJson = """{"ID":"aaaa11112222","Names":"web","Image":"nginx:latest","State":"exited","Status":"Exited (137)","Command":"nginx","Networks":"bridge"}""";

    [Fact]
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

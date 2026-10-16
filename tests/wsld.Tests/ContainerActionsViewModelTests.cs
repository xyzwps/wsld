using wsld.Core.Models;
using wsld.Core.Services;
using wsld.Core.ViewModels;
using wsld.Core.WslcCli;

namespace wsld.Tests;

/// <summary>功能 002 · AC5–AC11:容器操作在页面 ViewModel 上的编排行为。</summary>
public class ContainerActionsViewModelTests
{
    private const string RunningJson = """{"ID":"aaaa11112222","Names":"web","Image":"nginx:latest","State":"running","Status":"Up","Command":"nginx","Networks":"bridge"}""";
    private const string ExitedJson = """{"ID":"bbbb22223333","Names":"db","Image":"postgres:16","State":"exited","Status":"Exited (0)","Command":"postgres","Networks":"bridge"}""";
    private const string TwoContainers = RunningJson + "\n" + ExitedJson;

    private const string RunningOnly = RunningJson;
    private const string ExitedOnly = """{"ID":"aaaa11112222","Names":"web","Image":"nginx:latest","State":"exited","Status":"Exited (137)","Command":"nginx","Networks":"bridge"}""";

    private static (ContainersPageViewModel Vm, FakeWslcCli Cli) Create(string listStdout = "")
    {
        var cli = new FakeWslcCli().AlwaysReturn(0, listStdout);
        return (new ContainersPageViewModel(new ContainersService(cli), uiThread: null), cli);
    }

    private static ContainerItemViewModel Item(ContainersPageViewModel vm, string name) =>
        vm.Items.Single(i => i.DisplayName == name);

    // ---------- AC1:命令映射(经由命令入口) ----------

    [Fact]
    public async Task Stop_CallsWslcStopThenRefreshes()
    {
        var (vm, cli) = Create(TwoContainers);

        await vm.ExecuteAsync(Item(vm, "web"), ContainerAction.Stop);

        Assert.Equal(
            new[] { new[] { "list", "-a", "--format", "json" }, new[] { "stop", "aaaa11112222" }, new[] { "list", "-a", "--format", "json" } }
                .Select(a => string.Join(' ', a)),
            cli.Calls.Select(a => string.Join(' ', a)));
    }

    [Fact]
    public async Task StartCommand_CallsWslcStartWithIdNotName()
    {
        var (vm, cli) = Create(TwoContainers);

        await Item(vm, "db").StartCommand.ExecuteAsync(null);

        Assert.Contains(new[] { "start", "bbbb22223333" }, cli.Calls.Select(a => a.ToArray()));
    }

    [Fact]
    public async Task Restart_CallsWslcRestart()
    {
        var (vm, cli) = Create(TwoContainers);

        await vm.ExecuteAsync(Item(vm, "db"), ContainerAction.Restart);

        Assert.Contains(new[] { "restart", "bbbb22223333" }, cli.Calls.Select(a => a.ToArray()));
    }

    // ---------- AC9/AC10:运行中的容器删除自动转强制 ----------

    [Fact]
    public async Task RemoveRunningContainer_UsesForceFlag()
    {
        var (vm, cli) = Create(TwoContainers);

        await vm.ExecuteAsync(Item(vm, "web"), ContainerAction.Remove, force: true);

        Assert.Contains(new[] { "remove", "-f", "aaaa11112222" }, cli.Calls.Select(a => a.ToArray()));
    }

    [Fact]
    public async Task RemoveStoppedContainer_DoesNotForce()
    {
        var (vm, cli) = Create(TwoContainers);

        await vm.ExecuteAsync(Item(vm, "db"), ContainerAction.Remove, force: false);

        Assert.Contains(new[] { "remove", "bbbb22223333" }, cli.Calls.Select(a => a.ToArray()));
    }

    // ---------- AC9:行内那个"启动"键的可用性 ----------

    [Fact]
    public void RowStartButton_AvailabilityRunsOnState()
    {
        var (vm, _) = Create(TwoContainers);

        Assert.False(Item(vm, "web").CanStart);   // 运行中不给启动
        Assert.True(Item(vm, "db").CanStart);     // 已停止才给
        // 停止 / 重启 / 删除不在行内,可用性由详情面板管(ContainerDetailViewModelTests)。
    }

    // ---------- AC7:成功后刷新一次 ----------

    [Fact]
    public async Task Success_ClearsErrorAndRefreshesOnce()
    {
        var (vm, cli) = Create(TwoContainers);
        var web = Item(vm, "web");

        await vm.ExecuteAsync(web, ContainerAction.Stop);

        Assert.False(vm.HasError);
        Assert.Equal(3, cli.Calls.Count); // 构造时 list + stop + 成功后补一次 list
    }

    [Fact]
    public async Task RemoveSuccess_ContainerDisappearsFromList()
    {
        var cli = new FakeWslcCli();
        cli.Enqueue(0, TwoContainers);      // 构造时的 list
        cli.Enqueue(0);                     // remove 成功
        cli.Enqueue(0, RunningOnly);        // 成功后的 list:db 已消失
        var vm = new ContainersPageViewModel(new ContainersService(cli), uiThread: null);

        await vm.ExecuteAsync(Item(vm, "db"), ContainerAction.Remove);

        Assert.Single(vm.Items);
        Assert.Equal("web", vm.Items[0].DisplayName);
        Assert.Equal(1, vm.TotalCount);
    }

    // ---------- AC8:失败不刷新、保留数据 ----------

    [Fact]
    public async Task Failure_DoesNotRefresh_SetsCleanedError_KeepsItems()
    {
        var cli = new FakeWslcCli();
        cli.Enqueue(0, TwoContainers);
        cli.Enqueue(1, "", WslcErrorTests.NotFoundStderr);
        var vm = new ContainersPageViewModel(new ContainersService(cli), uiThread: null);

        await vm.ExecuteAsync(Item(vm, "web"), ContainerAction.Stop);

        Assert.True(vm.HasError);
        Assert.Equal("找不到容器 'no-such-container-xyz'。 错误代码： WSLC_E_CONTAINER_NOT_FOUND", vm.Error);
        Assert.Equal(2, vm.Items.Count);   // 数据保留
        Assert.Equal(2, cli.Calls.Count);  // 没有再调用 list
    }

    // ---------- AC5:忙碌互斥 ----------

    [Fact]
    public async Task WhileOperationIsRunning_EverythingIsDisabledAndRefreshIsSkipped()
    {
        var cli = new ActionGateCli(TwoContainers);
        var vm = new ContainersPageViewModel(new ContainersService(cli), uiThread: null);
        var web = Item(vm, "web");
        var db = Item(vm, "db");

        var pending = vm.ExecuteAsync(web, ContainerAction.Stop);
        Assert.False(pending.IsCompleted);

        Assert.True(vm.IsBusy);
        Assert.True(web.IsBusy);
        Assert.False(db.IsBusy);

        // 本行的启动键灭掉
        Assert.False(web.CanStart);
        Assert.False(web.StartCommand.CanExecute(null));

        // 其它行同样不能操作(全局单操作)
        Assert.False(db.CanStart);
        Assert.False(db.StartCommand.CanExecute(null));

        // AC6:操作期间刷新被跳过
        var refresh = vm.RefreshAsync(CancellationToken.None);
        Assert.True(refresh.IsCompleted);
        Assert.Equal(2, cli.Calls.Count); // 构造时的 list + 挂起中的 stop

        // AC5:第二个操作被直接忽略
        var second = vm.ExecuteAsync(db, ContainerAction.Restart);
        Assert.True(second.IsCompleted);
        Assert.Equal(2, cli.Calls.Count);

        cli.Release();
        await pending;

        Assert.False(vm.IsBusy);
        Assert.False(web.IsBusy);
        Assert.True(db.CanStart);   // 忙碌解除,已停止那行重新给启动
        Assert.False(web.CanStart); // 假 CLI 里它始终是 running
        Assert.Equal(3, cli.Calls.Count); // 成功后补一次 list
    }

    // ---------- AC11:行对象复用 ----------

    [Fact]
    public async Task Refresh_ReusesRowInstancesAndUpdatesState()
    {
        var cli = new FakeWslcCli();
        cli.Enqueue(0, TwoContainers);
        cli.Enqueue(0, ExitedOnly + "\n" + ExitedJson);
        var vm = new ContainersPageViewModel(new ContainersService(cli), uiThread: null);

        var before = vm.Items.Single(i => i.Id == "aaaa11112222");
        Assert.True(before.IsRunning);

        await vm.RefreshAsync(CancellationToken.None);

        var after = vm.Items.Single(i => i.Id == "aaaa11112222");
        Assert.True(ReferenceEquals(before, after)); // 同一对象,不是重建
        Assert.False(after.IsRunning);               // 数据已更新
        Assert.True(after.CanStart);                 // 按钮状态跟着翻转
        Assert.Equal(0, vm.RunningCount); // 第二次刷新后两个容器都已停止
    }

    [Fact]
    public async Task Refresh_WhenNothingChanged_KeepsCollectionIntact()
    {
        var (vm, _) = Create(TwoContainers);
        var order = vm.Items.Select(i => i.Id).ToList();

        await vm.RefreshAsync(CancellationToken.None);

        Assert.Equal(order, vm.Items.Select(i => i.Id));
    }

    // ---------- UI 线程契约:IsBusy 清零必须由 UI 线程发出 ----------

    [Fact]
    public async Task BusyIsClearedOnTheUiThreadSoTheSpinnerCanStop()
    {
        // 缺陷原型:wslc start 已返回、容器已在跑,行内却永远转圈。
        // 根因是清零发生在线程池延续上,后台线程的 PropertyChanged 打不动 x:Bind,
        // 而 UI 线程的后续刷新又因"值早就变了"不再补发通知。
        var cli = new ActionGateCli(TwoContainers);
        using var pump = new PumpUiThread();
        var vm = new ContainersPageViewModel(new ContainersService(cli), pump);
        Assert.True(SpinWait.SpinUntil(() => vm.Items.Count == 2, TimeSpan.FromSeconds(5)));
        var web = Item(vm, "web");

        var busyFalseThreads = new List<int>();
        web.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName == nameof(ContainerItemViewModel.IsBusy) && !web.IsBusy)
            {
                busyFalseThreads.Add(Environment.CurrentManagedThreadId);
            }
        };

        var pending = vm.ExecuteAsync(web, ContainerAction.Stop);
        Assert.True(web.IsBusy); // 置位发生在调用线程(真实界面里就是 UI 线程)
        cli.Release();
        await pending;

        Assert.Equal(pump.UiThreadId, Assert.Single(busyFalseThreads));
        Assert.False(web.IsBusy);
        Assert.False(vm.IsBusy);
    }

    /// <summary>list 立即返回、其它命令挂起直到 Release。</summary>
    private sealed class ActionGateCli : IWslcCli
    {
        private readonly TaskCompletionSource<WslcCliResult> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _listStdout;

        public ActionGateCli(string listStdout) => _listStdout = listStdout;

        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Task<WslcCliResult> RunLinesAsync(IReadOnlyList<string> arguments, Action<string>? onStdoutLine, CancellationToken cancellationToken = default) =>
            RunAsync(arguments, cancellationToken);

        // 这些闸门假 CLI 只关心"调用有没有挂起",用不到实时上屏,所以伪终端路径同样转给一次性实现。
        public Task<WslcCliResult> RunWithConsoleAsync(IReadOnlyList<string> arguments, Action<IReadOnlyList<string>>? onVisibleLines, CancellationToken cancellationToken = default) =>
            RunAsync(arguments, cancellationToken);

        public Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments);
            return arguments[0] == "list"
                ? Task.FromResult(new WslcCliResult(0, _listStdout, ""))
                : _gate.Task;
        }

        public void Release() => _gate.TrySetResult(new WslcCliResult(0, "", ""));
    }
}
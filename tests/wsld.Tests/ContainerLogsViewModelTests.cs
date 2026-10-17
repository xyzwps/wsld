using System.ComponentModel;
using wsld.Core;
using wsld.Core.Services;
using wsld.Core.ViewModels;
using wsld.Core.WslcCli;

namespace wsld.Tests;

/// <summary>
/// 容器详情「日志」页签的编排层(册子 LG-001、LG-003、LG-013、LG-015~026)。钉住四件事:
/// ① 两种读法各发各的参数(实时必须带 --since、必须不带 -n,"不加载历史"就是这么实现的);
/// ② 行先进缓冲、攒够一批或手动催一次才上屏;
/// ③ 界面只留最近 2000 行,丢掉的要报数;
/// ④ 同一时刻只有一个日志进程,而且收尾文案说得清"为什么停了"。
/// </summary>
public class ContainerLogsViewModelTests
{
    private static ContainerLogsViewModel Create(FakeWslcCli cli, IUIThread? uiThread = null)
    {
        var vm = new ContainerLogsViewModel(new LogsService(cli), uiThread);
        vm.InitializeCommands();
        vm.ShowContainer("web", isRunning: true);
        return vm;
    }

    // ---------- ① 两种读法,两套参数 ----------

    [Fact]
    [Trait("case", "LG-001")]
    public async Task Read_SendsOneShotQuery_WithoutFollowOrSince()
    {
        var cli = new FakeWslcCli().Enqueue(0, "alpha\nbeta");
        var vm = Create(cli);

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "logs", "-n", "200", "web" }, Assert.Single(cli.Calls));
        Assert.Equal("alpha\r\nbeta", vm.LogText);
        Assert.Contains("共 2 行", vm.StatusText);
        Assert.Equal("", vm.Notice);
    }

    [Fact]
    [Trait("case", "LG-003")]
    [Trait("case", "LG-021")]
    public async Task Live_SendsFollowWithSinceAndNoTail_SoHistoryIsNotLoaded()
    {
        // 「实时日志」的定义就是"只看点下去之后":-f + --since,且不带 -n。
        var cli = new FakeWslcCli().Enqueue(0, "an-old-line").EnqueueStream(0, "just-arrived");
        var vm = Create(cli);

        await vm.ReadCommand.ExecuteAsync(null);        // 先跑一趟「查看日志」
        Assert.Equal("an-old-line", vm.LogText);

        await vm.FollowCommand.ExecuteAsync(null);

        var args = Assert.Single(cli.StreamCalls);
        Assert.Contains("-f", args);
        Assert.Contains("--since", args);
        Assert.DoesNotContain("-n", args);
        Assert.Equal("just-arrived", vm.LogText);       // 上一趟读到的历史不跟过来
    }

    [Fact]
    [Trait("case", "LG-001")]
    public async Task Read_KeepsSelectedTail_AndTimestampsFlag()
    {
        var cli = new FakeWslcCli().Enqueue(0, "x").Enqueue(0, "y");
        var vm = Create(cli);
        vm.SelectedTail = new LogTailOption(1_000, "最近 1000 行");
        vm.Timestamps = true;

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "logs", "-t", "-n", "1000", "web" }, Assert.Single(cli.Calls));
    }

    [Fact]
    [Trait("case", "LG-019")]
    public void Live_IsOnlyForRunningContainers()
    {
        var vm = Create(new FakeWslcCli());
        vm.ShowContainer("web", isRunning: false);

        Assert.False(vm.CanFollow);
        Assert.False(vm.FollowCommand.CanExecute(null));
        Assert.Contains("容器没在运行", vm.FollowTooltip);
    }

    // ---------- ② 攒批上屏 ----------

    [Fact]
    [Trait("case", "LG-015")]
    public void Enqueue_WaitsForBatchThreshold_BeforeAskingTheUiToDraw()
    {
        var ui = new RecordingUiThread();
        var vm = Create(new FakeWslcCli(), ui);

        for (var i = 0; i < ContainerLogsViewModel.BatchThresholdLines - 1; i++) vm.EnqueueLine($"line-{i}");
        Assert.Empty(ui.Posted);                       // 一批都没攒满,不该去动整块文本

        vm.EnqueueLine("last-of-batch");
        Assert.Single(ui.Posted);                       // 第 64 行才催这一次

        ui.RunAll();
        Assert.Equal(ContainerLogsViewModel.BatchThresholdLines, vm.LogText.Split("\r\n").Length);
    }

    [Fact]
    [Trait("case", "LG-015")]
    public void DrainPending_ShowsEvenASingleBufferedLine()
    {
        // 实测每秒只打印一行的容器,8 秒都攒不满 64 行:页面的催屏定时器必须能让"哪怕一行"上屏,
        // 否则状态栏停在加载那一刻,用户会以为实时日志根本没在收。
        var ui = new RecordingUiThread();
        var vm = Create(new FakeWslcCli(), ui);

        vm.EnqueueLine("only-one");
        Assert.Equal("", vm.LogText);

        vm.DrainPending();
        Assert.Equal("only-one", vm.LogText);
        Assert.Contains("共 1 行", vm.StatusText);
    }

    // ---------- ③ 显示上限与丢弃计数 ----------

    [Fact]
    [Trait("case", "LG-016")]
    public async Task Read_TrimsToMaxDisplayedLines_AndCountsDropped()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 2_500).Select(i => $"line-{i}"));
        var cli = new FakeWslcCli().Enqueue(0, lines);
        var vm = Create(cli);

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal(ContainerLogsViewModel.MaxDisplayedLines, vm.LogText.Split("\r\n").Length);
        Assert.StartsWith("line-501", vm.LogText);
        Assert.Contains("已丢弃最早的 500 行", vm.StatusText);
    }

    [Fact]
    [Trait("case", "LG-026")]
    public void ShowContainer_ClearsPreviousContainerAndItsLines()
    {
        var vm = Create(new FakeWslcCli());
        vm.EnqueueLine("from-old-container");
        vm.DrainPending();
        Assert.Equal("from-old-container", vm.LogText);

        vm.ShowContainer("other", isRunning: false);

        Assert.Equal("", vm.LogText);
        Assert.Equal("", vm.StatusText);
        Assert.Equal("other", vm.ContainerRef);
        Assert.False(vm.CanFollow);
    }

    // ---------- ④ 互斥、停止与收尾文案 ----------

    [Fact]
    [Trait("case", "LG-018")]
    public async Task Follow_BlocksOtherLogOperationsWhileItRuns()
    {
        var gate = new TaskCompletionSource<WslcCliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = new FakeWslcCli
        {
            StreamHandler = (_, _, _) => gate.Task,
        };
        var vm = Create(cli);

        var pending = vm.FollowCommand.ExecuteAsync(null);

        Assert.True(vm.IsBusy);
        Assert.False(vm.ReadCommand.CanExecute(null));
        Assert.False(vm.CanChangeOptions);

        // 重复触发不该另起一个进程,否则会留下两个 wslc 在抢同一个容器的日志。
        await vm.FollowCommand.ExecuteAsync(null);
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.DoesNotContain(cli.Calls, args => !args.Contains("-f"));   // 一次「查看日志」都没被启动

        gate.SetResult(new WslcCliResult(0, "done", ""));
        await pending;

        Assert.False(vm.IsBusy);
        Assert.True(vm.ReadCommand.CanExecute(null));
        Assert.Single(cli.StreamCalls);
    }

    [Fact]
    [Trait("case", "LG-022")]
    public async Task StopFollowing_KeepsShownLines_AndSaysTheUserStoppedIt()
    {
        var gate = new TaskCompletionSource<WslcCliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = new FakeWslcCli
        {
            StreamHandler = (_, onLine, token) =>
            {
                onLine?.Invoke("kept");
                token.Register(() => gate.TrySetCanceled(token));
                return gate.Task;
            },
        };
        var vm = Create(cli);

        var pending = vm.FollowCommand.ExecuteAsync(null);
        await vm.StopFollowCommand.ExecuteAsync(null);
        await pending;                      // 取消在 VM 里收尾成文案,不外抛
        vm.DrainPending();

        Assert.Equal("kept", vm.LogText);                       // 取消不回滚已经显示的行
        Assert.Equal("已停止实时日志。", vm.Notice);
        Assert.False(vm.IsFollowing);
    }

    [Fact]
    [Trait("case", "LG-023")]
    [Trait("case", "LG-024")]
    public async Task Follow_EndedByItself_TellsWhetherAnyLineArrived()
    {
        // 实测容器停止时 wslc 以退出码 0 自己走掉,不留一句"容器已结束",这句话得界面说。
        var withLines = Create(new FakeWslcCli().EnqueueStream(0, "one\ntwo"));
        await withLines.FollowCommand.ExecuteAsync(null);
        Assert.Equal("实时日志已结束(容器多半已停止)。", withLines.Notice);

        var withoutLines = Create(new FakeWslcCli().EnqueueStream(0, ""));
        await withoutLines.FollowCommand.ExecuteAsync(null);
        Assert.Equal("实时日志已结束,期间没有新日志。", withoutLines.Notice);
    }

    [Fact]
    [Trait("case", "LG-025")]
    [Trait("case", "LG-013")]
    public async Task Follow_Failure_GoesToNoticeNotToText()
    {
        var cli = new FakeWslcCli().EnqueueStream(1, "", "WSLC_E_SOMETHING_BAD");
        var vm = Create(cli);

        await vm.FollowCommand.ExecuteAsync(null);

        Assert.Contains("WSLC_E_SOMETHING_BAD", vm.Notice);
        Assert.Equal("", vm.LogText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    [Trait("case", "LG-017")]
    public async Task Read_EmptyContainerOutput_IsANotice_NotAnError()
    {
        var cli = new FakeWslcCli().Enqueue(0, "");
        var vm = Create(cli);

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal("这个容器目前没有任何输出。", vm.Notice);
        Assert.Equal("", vm.LogText);
    }

    [Fact]
    [Trait("case", "LG-031")]
    [Trait("case", "LG-020")]
    public async Task ContainerStopping_EndsTheLiveSession()
    {
        var gate = new TaskCompletionSource<WslcCliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = new FakeWslcCli
        {
            StreamHandler = (_, _, token) =>
            {
                token.Register(() => gate.TrySetCanceled(token));
                return gate.Task;
            },
        };
        var vm = Create(cli);

        var pending = vm.FollowCommand.ExecuteAsync(null);
        vm.UpdateRunning(isRunning: false);

        await pending;                      // 容器停了,这一路实时日志就地收尾
        Assert.Equal("已停止实时日志。", vm.Notice);
        Assert.False(vm.CanFollow);
        Assert.False(vm.IsFollowing);
    }

    // ---------- 状态清零必须在 UI 线程 ----------

    [Fact]
    public async Task Read_CompletesOnTheUiThread()
    {
        using var pump = new PumpUiThread();
        var cli = new FakeWslcCli().Enqueue(0, "alpha");
        var vm = new ContainerLogsViewModel(new LogsService(cli), pump);
        vm.InitializeCommands();

        var busyThreads = new List<int>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ContainerLogsViewModel.IsBusy) && !vm.IsBusy)
            {
                busyThreads.Add(Environment.CurrentManagedThreadId);
            }
        };

        vm.ShowContainer("web", isRunning: true);
        await vm.ReadCommand.ExecuteAsync(null);
        await DrainPumpAsync(pump);

        Assert.Equal(pump.UiThreadId, Assert.Single(busyThreads));
        Assert.Equal("alpha", vm.LogText);
    }

    /// <summary>往泵线程再投一件小事并等它做完:FIFO,所以它做完就说明前面排队的都执行过了。</summary>
    private static async Task DrainPumpAsync(PumpUiThread pump)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Post(() => tcs.TrySetResult());
        await tcs.Task;
    }

    /// <summary>只记录 Post 过来的动作,由测试决定什么时候真的执行。</summary>
    private sealed class RecordingUiThread : IUIThread
    {
        public List<Action> Posted { get; } = new();

        public void Post(Action action) => Posted.Add(action);

        public void RunAll()
        {
            foreach (var action in Posted.ToList()) action();
            Posted.Clear();
        }
    }
}

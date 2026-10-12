using WslcDesktop.Core;
using WslcDesktop.Core.Services;
using WslcDesktop.Core.ViewModels;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>
/// 功能 005 · AC9–AC15:"日志"页 ViewModel 的编排 ——
/// 单进程互斥、跟随的开始/停止、行先进缓冲批量上屏、显示上限、
/// 空日志不算错误、只有运行中可跟随、容器下拉按 Id 保留选择。
/// uiThread 传 null:所有更新同步落到属性上,断言不需要和时钟赛跑;
/// 攒批调度用记录的假 UI 线程单独验证(AC12)。
/// </summary>
public class LogsPageViewModelTests
{
    private const string RunningWeb = """{"ID":"aaaa11112222","Names":"web","Image":"nginx:latest","State":"running","Status":"Up 2 hours","Command":"nginx"}""";
    private const string StoppedDb = """{"ID":"bbbb22223333","Names":"db","Image":"postgres:16","State":"exited","Status":"Exited (0)","Command":"postgres"}""";
    private const string RunningApi = """{"ID":"cccc33334444","Names":"api","Image":"redis:7","State":"running","Status":"Up 1 hour","Command":"redis-server"}""";
    private const string TwoContainers = RunningWeb + "\n" + StoppedDb;

    private static (LogsPageViewModel Vm, RoutingWslcCli Cli) Create(string containersJson = TwoContainers)
    {
        var cli = new RoutingWslcCli { ContainersJson = containersJson };
        var vm = new LogsPageViewModel(new ContainersService(cli), new LogsService(cli), uiThread: null);
        return (vm, cli);
    }

    // ---------- AC15 · 容器下拉 ----------

    [Fact]
    public void Constructor_LoadsSources_RunningFirst_AndAutoSelects()
    {
        var (vm, cli) = Create();

        Assert.Equal(2, vm.Sources.Count);
        Assert.Equal("web · aaaa11112222 · 运行中", vm.Sources[0].Label);
        Assert.Equal("db · bbbb22223333 · 已停止", vm.Sources[1].Label);
        Assert.Same(vm.Sources[0], vm.SelectedSource);
        Assert.True(vm.CanFollow);
        Assert.True(vm.CanLoad);

        // AC1:选完自动加载一次,参数严格是 logs -n 200 <长ID>。
        Assert.Equal(new[] { "logs", "-n", "200", "aaaa11112222" }, cli.LastCall("logs"));
    }

    [Fact]
    public void Constructor_NoContainers_NoticeAndNothingLoadable()
    {
        var (vm, _) = Create("");

        Assert.Empty(vm.Sources);
        Assert.Null(vm.SelectedSource);
        Assert.False(vm.CanLoad);
        Assert.False(vm.LoadCommand.CanExecute(null));
        Assert.Contains("还没有容器", vm.Notice);
    }

    [Fact]
    public async Task Load_NoSourceSelected_ShowsHint()
    {
        var (vm, cli) = Create("");
        var callsBefore = cli.CountCalls("logs");

        await vm.LoadAsync();

        Assert.Equal(LogsPageViewModel.NoSourceHint, vm.Notice);
        Assert.Equal(callsBefore, cli.CountCalls("logs")); // 没选容器绝不发命令
    }

    [Fact]
    public async Task Refresh_PreservesSelection_AndReusesOptionObjects()
    {
        var (vm, cli) = Create();
        var before = vm.SelectedSource;
        cli.ContainersJson = RunningWeb + "\n" + RunningApi + "\n" + StoppedDb;

        await vm.LoadSourcesAsync(preserveSelection: true);

        Assert.Same(before, vm.SelectedSource);          // 按 Id 保留选择
        Assert.Equal(3, vm.Sources.Count);               // 运行中的在前 + 名称字母序
        Assert.Equal(new[] { "api", "web", "db" }, vm.Sources.Select(s => s.DisplayName));
    }

    [Fact]
    public async Task Refresh_DetectsSelectedStopped_UnchecksFollow()
    {
        var (vm, cli) = Create();
        vm.FollowRequested = true;
        cli.ContainersJson = """{"ID":"aaaa11112222","Names":"web","Image":"nginx:latest","State":"exited","Status":"Exited (0)","Command":"nginx"}""";

        await vm.LoadSourcesAsync(preserveSelection: true);

        Assert.False(vm.FollowRequested); // AC11:跟随的前提没了就自动取消
        Assert.False(vm.CanFollow);
    }

    [Fact]
    public async Task Refresh_Failure_SetsErrorAndKeepsOptions()
    {
        var (vm, cli) = Create();
        var before = vm.SelectedSource;
        cli.ContainersJson = "这不是 JSON";

        await vm.LoadSourcesAsync(preserveSelection: true);

        Assert.True(vm.HasError);
        Assert.Contains("不是有效的容器", vm.Error);
        Assert.Equal(2, vm.Sources.Count);   // 已有选项不清空
        Assert.Same(before, vm.SelectedSource);
    }

    // ---------- 一次性读取(AC1/AC6/AC10) ----------

    [Fact]
    public void Read_LinesLandInTheDisplayArea()
    {
        var cli = new RoutingWslcCli { ContainersJson = RunningWeb }
            .Enqueue(0, FixtureLoader.Read("wslc-logs.txt"));
        var vm = new LogsPageViewModel(new ContainersService(cli), new LogsService(cli));

        Assert.Equal(6, vm.LineCount);
        Assert.Equal("log line 741 at 1791805726\r\nlog line 742 at 1791805727",
            string.Join("\r\n", vm.LogText.Split("\r\n").Take(2)));
        Assert.False(vm.HasError);
        Assert.False(vm.HasNotice); // 读到内容就是正常结果,不抢提示条
        Assert.NotEqual("尚未加载", vm.LastLoadedText);
    }

    [Fact]
    public void Read_EmptyOutput_IsNoticeNotError()
    {
        // AC10 的实测依据:已退出 / 从未启动 → 退出码 0 + 空输出,界面不能说这是错误。
        var (vm, _) = Create();

        Assert.True(vm.HasNotice);
        Assert.False(vm.HasError);
        Assert.Contains("没有日志输出", vm.Notice);
        Assert.Contains("web", vm.Notice);
        Assert.Equal("", vm.LogText);
    }

    [Fact]
    public void Read_Failure_ShowsCleanedError_AndKeepsLogAreaEmpty()
    {
        var cli = new RoutingWslcCli { ContainersJson = RunningWeb }
            .AlwaysFor("logs", 1, "", FixtureLoader.Read("wslc-logs-not-found.txt"));
        var vm = new LogsPageViewModel(new ContainersService(cli), new LogsService(cli));

        Assert.True(vm.HasError);
        Assert.Contains("WSLC_E_CONTAINER_NOT_FOUND", vm.Error);
        Assert.DoesNotContain("如果此错误是意外错误", vm.Error); // AC6:样板句被清洗掉
        Assert.Equal("", vm.LogText);
        Assert.False(vm.HasNotice);
    }

    [Fact]
    public async Task SelectedTail_AndTimestamps_FlowIntoArguments()
    {
        var (vm, cli) = Create();
        cli.EnqueueStream(0, "x\n");
        vm.SelectedTail = vm.TailOptions[0]; // 100
        vm.Timestamps = true;
        vm.FollowRequested = true;

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "logs", "-f", "-t", "-n", "100", "aaaa11112222" }, cli.LastStreamCall("logs"));
        Assert.Equal(1, vm.LineCount);
    }

    // ---------- AC9 · 单进程互斥 ----------

    [Fact]
    public async Task Follow_IsBusy_LocksEverythingUntilItEndsNaturally()
    {
        var release = new TaskCompletionSource<WslcCliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, cli) = Create();
        cli.StreamHandler = (args, onLine, token) =>
        {
            onLine!("streamed");
            return release.Task; // 进程还活着,日志还在流
        };
        vm.FollowRequested = true;

        var task = vm.LoadCommand.ExecuteAsync(null);

        // 跟随进行中:所有入口都被锁住(AC9)。
        Assert.True(vm.IsBusy);
        Assert.True(vm.IsFollowing);
        Assert.False(vm.CanLoad);
        Assert.False(vm.LoadCommand.CanExecute(null));
        Assert.False(vm.CanChangeOptions);
        Assert.False(vm.RefreshSourcesCommand.CanExecute(null));
        Assert.Contains("跟随中", vm.StatusText);

        var listCalls = cli.CountCalls("list");
        await vm.LoadAsync();               // 重复加载 → 直接返回
        await vm.LoadSourcesAsync(true);    // 忙碌时刷新被忽略
        Assert.Equal(1, cli.CountStreamCalls("logs"));
        Assert.Equal(listCalls, cli.CountCalls("list"));

        // 进程自己结束(wslc 在容器停止时就是这样)→ AC14。
        release.SetResult(new WslcCliResult(0, "streamed", ""));
        await task;

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsFollowing);
        Assert.True(vm.CanLoad);
        Assert.Equal("streamed", vm.LogText); // 缓冲里的行收尾时强制上屏
        Assert.Equal("跟随已结束(容器多半已停止)· 共 1 行", vm.Notice);
    }

    [Fact]
    public async Task Load_WhileBusy_IsIgnoredEntirely()
    {
        var release = new TaskCompletionSource<WslcCliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, cli) = Create();
        cli.StreamHandler = (args, onLine, token) => release.Task;
        vm.FollowRequested = true;
        var task = vm.LoadCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);

        await vm.LoadAsync(); // 第二次调用被互斥拦下,不会产生第二个进程

        release.SetResult(new WslcCliResult(0, "", ""));
        await task;
        Assert.Equal(1, cli.CountStreamCalls("logs"));
    }

    // ---------- AC8 · 取消跟随 ----------

    [Fact]
    public async Task CancelFollow_KeepsAlreadyDisplayedLines()
    {
        var (vm, cli) = Create();
        cli.StreamHandler = async (args, onLine, token) =>
        {
            onLine!("keep-me");
            await Task.Delay(Timeout.Infinite, token); // 模拟一直在流的进程
            return new WslcCliResult(0, "keep-me", "");
        };
        vm.FollowRequested = true;
        var task = vm.LoadCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.PendingCount > 0);

        vm.CancelFollow();
        await task;

        Assert.Equal("keep-me", vm.LogText);   // 已显示的内容不清空
        Assert.Equal(1, vm.LineCount);
        Assert.Equal("已停止跟随 · 共 1 行", vm.Notice);
        Assert.False(vm.IsFollowing);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void CancelFollow_WithoutFollow_IsNoOp()
    {
        var (vm, _) = Create();
        vm.CancelFollow(); // 没有跟随在跑时按"停止"不该有任何副作用
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsFollowing);
    }

    [Fact]
    public async Task Follow_ZeroLines_ExplainsWhyWithoutError()
    {
        var (vm, cli) = Create();
        cli.StreamHandler = (args, onLine, token) => Task.FromResult(new WslcCliResult(0, "", ""));
        vm.FollowRequested = true;

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.Contains("没有输出", vm.Notice);
        Assert.Contains("跟随已结束", vm.Notice);
    }

    // ---------- AC11 · 只有运行中的容器可以跟随 ----------

    [Fact]
    public void SelectingStoppedContainer_DisablesAndUnchecksFollow()
    {
        var (vm, _) = Create();
        vm.FollowRequested = true;

        vm.SelectedSource = vm.Sources[1]; // 切到已停止的 db

        Assert.False(vm.CanFollow);
        Assert.False(vm.FollowRequested); // AC11:实测停止的容器 -f 会立刻静默结束,只会让人以为按钮坏了
    }

    [Fact]
    public async Task Load_NeverSendsFollowForStoppedContainer_EvenIfRequested()
    {
        var (vm, cli) = Create();
        vm.SelectedSource = vm.Sources[1]; // db(已停止)
        vm.FollowRequested = true;         // 界面会禁用勾选,VM 还得自己兜底

        await vm.LoadAsync();

        Assert.Equal(0, cli.CountStreamCalls("logs")); // 走了一次性读取,没挂 -f 进程
        Assert.DoesNotContain("-f", cli.LastCall("logs"));
        Assert.False(vm.IsFollowing);
    }

    // ---------- AC12 · 行先进缓冲,批量上屏 ----------

    [Fact]
    public void EnqueuedLines_WaitInBuffer_UntilDrained()
    {
        var (vm, _) = Create();

        for (var i = 1; i <= 300; i++) vm.EnqueueLine($"L{i}");

        Assert.Equal(300, vm.PendingCount);
        Assert.Equal(0, vm.LineCount); // 还没上屏

        var drained = vm.DrainPending();

        Assert.Equal(300, drained.Count);
        Assert.Equal(300, vm.LineCount);
        Assert.Equal(0, vm.PendingCount);
    }

    [Fact]
    public void DrainPending_AppliesEvenASingleLine()
    {
        // 页面在跟随期间每 500ms 催一次上屏(见 LogsPage 的催屏计时器)。
        // 慢速容器每秒才 1 行,等不到 64 行的攒批阈值,所以"只有 1 行缓冲"也必须能上屏,
        // 否则日志看起来就是卡住的 —— 这条测试钉住计时器依赖的这个契约。
        var (vm, _) = Create();

        vm.EnqueueLine("slow-1");

        Assert.Equal(1, vm.PendingCount);
        Assert.Equal(0, vm.LineCount); // 催之前不上屏

        var drained = vm.DrainPending();

        Assert.Single(drained);
        Assert.Equal(1, vm.LineCount);
        Assert.Equal("slow-1", vm.LogText);
        Assert.Equal(0, vm.PendingCount);
    }

    [Fact]
    public async Task Follow_KeepsLastLoadedTextFresh()
    {
        // 真机验证时发现的问题:跟随期间状态栏的"最近加载"一直停在开始前的时刻,
        // 只有每次新行上屏时才该推进它,否则用户会以为跟随没生效。
        var gate = new TaskCompletionSource<WslcCliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, cli) = Create();
        cli.StreamHandler = (args, onLine, token) =>
        {
            onLine!("first");
            return gate.Task;
        };
        vm.FollowRequested = true;

        var task = vm.LoadCommand.ExecuteAsync(null);
        vm.LastLoadedText = "00:00:00"; // 假装这是上一次操作留下的时刻

        vm.EnqueueLine("second");
        vm.DrainPending(); // 相当于页面催屏计时器的一次触发

        Assert.NotEqual("00:00:00", vm.LastLoadedText);
        Assert.Contains(vm.LastLoadedText, vm.StatusText);

        gate.SetResult(new WslcCliResult(0, "", ""));
        await task;
    }

    [Fact]
    public async Task ScheduledDrain_GoesThroughUiThreadOnlyAtBatchThreshold()
    {
        var cli = new RoutingWslcCli { ContainersJson = RunningWeb };
        var ui = new RecordedUiThread();
        var vm = new LogsPageViewModel(new ContainersService(cli), new LogsService(cli), ui);
        ui.FlushUntilIdle(); // 把构造期间的加载跑完
        Assert.Single(vm.Sources);
        ui.Posted.Clear();

        for (var i = 0; i < LogsPageViewModel.BatchThreshold - 1; i++) vm.EnqueueLine($"L{i}");
        Assert.Empty(ui.Posted); // 没攒够就不催上屏

        vm.EnqueueLine($"L{LogsPageViewModel.BatchThreshold - 1}"); // 第 64 行 → 催一次
        Assert.Single(ui.Posted);
        ui.FlushUntilIdle();

        Assert.Equal(LogsPageViewModel.BatchThreshold, vm.LineCount);
        Assert.Equal(0, vm.PendingCount);
        await Task.CompletedTask;
    }

    // ---------- AC13 · 显示上限与丢弃计数 ----------

    [Fact]
    public void DisplayCap_DropsOldest_AndCountsDrops()
    {
        var (vm, _) = Create();

        for (var i = 1; i <= 2_500; i++) vm.EnqueueLine($"L{i}");
        vm.DrainPending();

        Assert.Equal(LogsPageViewModel.MaxDisplayedLines, vm.LineCount);
        Assert.Equal(500, vm.DroppedLineCount);
        Assert.StartsWith("L501", vm.LogText);
        Assert.Contains("已丢弃最早的 500 行", vm.StatusText);
    }

    // ---------- AC16 · 自动滚动开关的 VM 侧 ----------

    [Fact]
    public void AutoScrollOff_FreezesDisplay_ResumingFlushesBuffer()
    {
        var (vm, _) = Create();
        vm.AutoScroll = false;

        for (var i = 0; i < 10; i++) vm.EnqueueLine($"L{i}");
        Assert.Empty(vm.DrainPending()); // 暂停期间调用也什么都不动
        Assert.Equal(10, vm.PendingCount);
        Assert.Equal(0, vm.LineCount);
        Assert.Contains("已暂停滚动 · 缓冲 10 行", vm.StatusText);

        vm.AutoScroll = true; // 重新打开 → 攒下的行一次性补上

        Assert.Equal(10, vm.LineCount);
        Assert.Equal(0, vm.PendingCount);
    }

    // ---------- 辅助 ----------

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), "等待条件超时");
    }

    /// <summary>记录投递的假 UI 线程:测试自己决定何时执行(AC12 不依赖真实时钟)。</summary>
    private sealed class RecordedUiThread : IUIThread
    {
        public Queue<Action> Posted { get; } = new();

        public void Post(Action action) => Posted.Enqueue(action);

        public void FlushUntilIdle()
        {
            while (Posted.Count > 0)
            {
                Posted.Dequeue().Invoke();
            }
        }
    }
}

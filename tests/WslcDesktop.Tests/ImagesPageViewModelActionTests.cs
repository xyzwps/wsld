using System.Diagnostics;
using WslcDesktop.Core.Services;
using WslcDesktop.Core.ViewModels;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>
/// 功能 004 · AC5–AC9、AC12:镜像页 ViewModel 的拉取/删除编排
/// (全局单操作互斥、可取消、成功后刷新一次、失败保留数据、行状态活过刷新)。
/// </summary>
public class ImagesPageViewModelActionTests
{
    private const string BusyBox = """{"ID":"aaef90e06523","Repository":"docker.1ms.run/library/busybox","Tag":"latest","Size":"4.45MB","Containers":"2","CreatedAt":"2026-05-13 10:21:49 +0800 GMT+8","Digest":"<none>"}""";
    private const string Hello = """{"ID":"e2ac70e7319a","Repository":"docker.1ms.run/library/hello-world","Tag":"latest","Size":"10.1kB","Containers":"0","CreatedAt":"2026-03-24 05:33:59 +0800 GMT+8","Digest":"<none>"}""";
    private const string TwoImages = BusyBox + "\n" + Hello;

    private const string BusyBoxRef = "docker.1ms.run/library/busybox:latest";
    private const string NginxRef = "docker.1ms.run/library/nginx:latest";

    private const string PullFailStdErr = """
        来自引擎的错误信息: Get "https://registry-1.docker.io/v2/": context deadline exceeded
        错误代码： E_FAIL
        如果此错误是意外错误，请考虑搜索现有问题或在 https://github.com/microsoft/WSL/issues 提交新问题。
        """;

    // ---------- AC1 / AC8 · 拉取成功 ----------

    [Fact]
    public async Task Pull_NewImage_SetsNoticeAndRefreshesExactlyOnce()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages }
            .Enqueue(0, FixtureLoader.Read("wslc-pull-new.txt"));
        var vm = Create(cli);
        vm.PullText = NginxRef;
        var imagesBefore = cli.CountCalls("images");

        var result = await vm.PullAsync();

        Assert.NotNull(result);
        Assert.Equal("已拉取 " + NginxRef, vm.Notice);              // AC8
        Assert.True(vm.HasNotice);
        Assert.False(vm.HasError);
        Assert.Equal(new[] { "pull", NginxRef }, cli.LastCall("pull"));
        Assert.Equal(imagesBefore + 1, cli.CountCalls("images"));    // AC8:只刷新一次
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Pull_ExistingImage_ReportsUpToDate()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages }
            .Enqueue(0, FixtureLoader.Read("wslc-pull-uptodate.txt"));
        var vm = Create(cli);
        vm.PullText = BusyBoxRef;

        await vm.PullAsync();

        Assert.Equal(BusyBoxRef + " 已是最新", vm.Notice);
    }

    [Fact]
    public async Task ManualRefresh_ClearsNotice()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages }
            .Enqueue(0, FixtureLoader.Read("wslc-pull-new.txt"));
        var vm = Create(cli);
        vm.PullText = NginxRef;
        await vm.PullAsync();
        Assert.NotNull(vm.Notice);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Null(vm.Notice);   // 手动刷新不带着旧的操作提示
    }

    // ---------- AC2 · 空引用 ----------

    [Fact]
    public async Task Pull_EmptyInput_DoesNotStartProcess_AndHints()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        var vm = Create(cli);
        vm.PullText = "   ";

        var result = await vm.PullAsync();

        Assert.Null(result);
        Assert.Equal(ImagesPageViewModel.EmptyReferenceHint, vm.Error);
        Assert.Equal(0, cli.CountCalls("pull"));   // AC2:绝不带着空引用去启动进程
    }

    [Fact]
    public void PullCommand_IsDisabledWhenInputBlank()
    {
        var vm = Create(new RoutingWslcCli { ListJson = TwoImages });

        Assert.False(vm.CanPull);
        Assert.False(vm.PullCommand.CanExecute(null));

        vm.PullText = NginxRef;
        Assert.True(vm.CanPull);
        Assert.True(vm.PullCommand.CanExecute(null));
    }

    // ---------- AC5 · 全局单操作互斥 ----------

    [Fact]
    public async Task WhilePulling_SecondOperationIgnored_AndRefreshSkipped()
    {
        var (vm, cli, gate) = CreateGated();
        vm.PullText = "alpine:3.20";
        var pull = vm.PullAsync();
        await WaitUntilAsync(() => vm.IsBusy);

        Assert.False(vm.CanPull);                                     // AC5:拉取中"拉取"按钮不可用
        Assert.False(vm.PullCommand.CanExecute(null));
        Assert.Equal(2, vm.Items.Count);
        Assert.All(vm.Items, item => Assert.False(item.CanRemove));    // AC5:每一行的删除都禁用
        Assert.All(vm.Items, item => Assert.False(item.RemoveCommand.CanExecute(null)));

        var imagesBefore = cli.CountCalls("images");
        await vm.RefreshAsync();                                       // AC5:操作期间的刷新被跳过
        Assert.Equal(imagesBefore, cli.CountCalls("images"));

        Assert.False(await vm.ExecuteRemoveAsync(vm.Items[0]));         // AC5:重复调用被忽略
        Assert.Equal(0, cli.CountCalls("rmi"));

        gate.Complete("Status: Downloaded newer image for alpine:3.20");
        await pull;

        Assert.False(vm.IsBusy);
        Assert.True(vm.CanPull);
        Assert.All(vm.Items, item => Assert.True(item.CanRemove));      // 按钮恢复
    }

    [Fact]
    public async Task NewOperation_ClearsPreviousNotice()
    {
        var (vm, _, gate) = CreateGated();
        vm.PullText = "alpine:3.20";
        var first = vm.PullAsync();
        gate.Complete("Status: Downloaded newer image for alpine:3.20");
        await first;
        Assert.Equal("已拉取 alpine:3.20", vm.Notice);

        gate.Reset();
        vm.PullText = "redis:7";
        var second = vm.PullAsync();
        await WaitUntilAsync(() => vm.IsBusy);

        Assert.Null(vm.Notice);   // AC8:开始新动作时旧提示被清掉

        gate.Complete("Status: Downloaded newer image for redis:7");
        await second;
        Assert.Equal("已拉取 redis:7", vm.Notice);
    }

    // ---------- AC6 · 取消拉取 ----------

    [Fact]
    public async Task CancelPull_LeavesNoError_AndDoesNotRefresh()
    {
        var (vm, cli, _) = CreateGated();
        vm.PullText = NginxRef;
        var pull = vm.PullAsync();
        await WaitUntilAsync(() => vm.IsPulling);
        var imagesBefore = cli.CountCalls("images");

        vm.CancelPull();
        var result = await pull;

        Assert.Null(result);
        Assert.Equal("已取消拉取 " + NginxRef, vm.Notice);                 // AC6
        Assert.False(vm.HasError);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsPulling);
        Assert.Equal(imagesBefore, cli.CountCalls("images"));              // AC6:取消不刷新(实测无残留)
    }

    [Fact]
    public async Task CancelPull_WhileNothingIsRunning_DoesNothing()
    {
        var vm = Create(new RoutingWslcCli { ListJson = TwoImages });

        vm.CancelPull();

        Assert.False(vm.HasError);
        Assert.Null(vm.Notice);
        Assert.False(vm.IsPulling);
        Assert.False(vm.IsBusy);
        await Task.Yield();
    }

    [Fact]
    public async Task CancelPullCommand_IsUsableOnlyWhilePulling()
    {
        var (vm, _, gate) = CreateGated();
        Assert.False(vm.IsPulling);
        Assert.False(vm.CancelPullCommand.CanExecute(null));

        vm.PullText = NginxRef;
        var pull = vm.PullAsync();
        await WaitUntilAsync(() => vm.IsPulling);
        Assert.True(vm.CancelPullCommand.CanExecute(null));

        gate.Complete("Status: Downloaded newer image for " + NginxRef);
        await pull;
        Assert.False(vm.CancelPullCommand.CanExecute(null));
    }

    // ---------- AC7 · 进度只做"不确定"表达 ----------

    [Fact]
    public async Task BusyText_DescribesWhatIsRunningAndTicksSeconds()
    {
        var (vm, _, gate) = CreateGated();
        Assert.Equal("", vm.BusyText);

        vm.PullText = "alpine:3.20";
        var pull = vm.PullAsync();
        await WaitUntilAsync(() => vm.IsBusy);

        vm.Tick();   // AC7:界面的 1 秒计时器驱动,这里手动调用免得依赖真实时钟
        Assert.Matches(@"^正在拉取 alpine:3\.20 · 已等待 \d+ 秒$", vm.BusyText);

        gate.Complete("Status: Downloaded newer image for alpine:3.20");
        await pull;

        Assert.Equal("", vm.BusyText);   // 结束后不再显示进度
    }

    [Fact]
    public async Task Remove_MarksThatRowBusyWhileRunning()
    {
        var (vm, cli, gate) = CreateGated();
        var target = vm.Items[0];

        var remove = vm.ExecuteRemoveAsync(target);
        await WaitUntilAsync(() => vm.IsBusy);

        Assert.True(target.IsBusy);
        Assert.Contains("正在删除", vm.BusyText);

        gate.Complete("已取消标记: " + target.Reference);
        Assert.True(await remove);
        Assert.False(target.IsBusy);
        Assert.Equal(new[] { "rmi", target.Reference }, cli.LastCall("rmi"));
    }

    // ---------- AC9 · 失败不丢数据 ----------

    [Fact]
    public async Task Pull_Failure_KeepsListAndShowsCleanedError()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages }.Enqueue(1, "", PullFailStdErr);
        var vm = Create(cli);
        vm.PullText = "nope:latest";

        var result = await vm.PullAsync();

        Assert.Null(result);
        Assert.True(vm.HasError);
        Assert.Contains("E_FAIL", vm.Error);
        Assert.DoesNotContain("如果此错误是意外错误", vm.Error);   // 样板提示被清洗
        Assert.Null(vm.Notice);
        Assert.Equal(2, vm.Items.Count);                            // AC9:列表还是上一次的数据
        Assert.False(vm.IsBusy);
    }

    // ---------- AC12 · 行状态活过刷新 ----------

    [Fact]
    public async Task Refresh_ReusesRowObjectButSwapsUnderlyingData()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        var vm = Create(cli);
        var row = vm.Items[0];
        var modelBefore = row.Model;
        Assert.Equal("4.45MB", row.SizeText);

        cli.ListJson = BusyBox.Replace("4.45MB", "4.60MB") + "\n" + Hello;   // 同一条镜像,数据变新
        await vm.RefreshAsync();

        Assert.Same(row, vm.Items[0]);                     // AC12:同一个行对象,不是重建
        Assert.NotSame(modelBefore, row.Model);            // 底层数据已换成新解析出来的
        Assert.Equal("4.60MB", row.SizeText);
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public async Task Refresh_DropsRowsThatDisappearedFromTheList()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        var vm = Create(cli);
        Assert.Equal(2, vm.Items.Count);

        cli.ListJson = Hello;   // busybox 那行没了
        await vm.RefreshAsync();

        Assert.Single(vm.Items);
        Assert.Equal("docker.1ms.run/library/hello-world:latest", vm.Items[0].Reference);
        Assert.Equal("共 1 个镜像 · 1 个有标签 · 0 个正被容器引用", vm.Summary);
    }

    [Fact]
    public async Task Pull_ForAnImageAlreadyInList_MarksThatRowBusy()
    {
        var (vm, _, gate) = CreateGated();
        vm.PullText = BusyBoxRef;

        var pull = vm.PullAsync();
        await WaitUntilAsync(() => vm.IsBusy);

        Assert.True(vm.Items[0].IsBusy);   // 列表里已有这一行,让它显示进行中的圈
        Assert.False(vm.Items[1].IsBusy);

        gate.Complete("Status: Image is up to date for " + BusyBoxRef);
        await pull;

        Assert.False(vm.Items[0].IsBusy);
    }

    private static ImagesPageViewModel Create(RoutingWslcCli cli) =>
        new(new ImagesService(cli), uiThread: null);

    /// <summary>造一个"操作还在跑"的现场:pull/rmi 挂起,由测试决定结局,取消令牌会直接结掉它。</summary>
    private static (ImagesPageViewModel Vm, RoutingWslcCli Cli, ActionGate Gate) CreateGated(string listJson = TwoImages)
    {
        var cli = new RoutingWslcCli { ListJson = listJson };
        var gate = new ActionGate();
        cli.ActionHandler = gate.Handler;
        return (Create(cli), cli, gate);
    }

    /// <summary>挂起中的 wslc 调用:测试用 <see cref="Complete"/> 给出结局,取消则自动失败。</summary>
    private sealed class ActionGate
    {
        private TaskCompletionSource<WslcCliResult> _current = New();

        public Task<WslcCliResult> Handler(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var tcs = _current;
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }

        public void Complete(string stdout, int exitCode = 0, string stderr = "") =>
            _current.TrySetResult(new WslcCliResult(exitCode, stdout, stderr));

        /// <summary>开始下一轮挂起(上一次已经完成时用)。</summary>
        public void Reset() => _current = New();

        private static TaskCompletionSource<WslcCliResult> New() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>等待后台状态落地。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("等待条件超时。");
            await Task.Delay(10);
        }
    }
}
using System.Diagnostics;
using WslcDesktop.Core.Services;
using WslcDesktop.Core.ViewModels;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>
/// 功能 011:拉取期间的实时进度上屏(伪终端屏幕行 → 汇总一句话 + 逐行文本)。
/// 假 CLI 用 <c>ConsoleHandler</c> 制造"进程还在跑、进度一帧一帧到"的现场:
/// 帧内容是真机抓到的屏幕行(每层一行,回写覆盖),结局由测试放行,这样才能断言"中途"的状态。
/// </summary>
public class ImagesPagePullProgressTests
{
    private const string BusyBox = """{"ID":"aaef90e06523","Repository":"docker.1ms.run/library/busybox","Tag":"latest","Size":"4.45MB","Containers":"0","CreatedAt":"2026-05-13 10:21:49 +0800 GMT+8","Digest":"<none>"}""";
    private const string Hello = """{"ID":"e2ac70e7319a","Repository":"docker.1ms.run/library/hello-world","Tag":"latest","Size":"10.1kB","Containers":"0","CreatedAt":"2026-03-24 05:33:59 +0800 GMT+8","Digest":"<none>"}""";
    private const string TwoImages = BusyBox + "\n" + Hello;

    private const string NginxRef = "docker.1ms.run/library/nginx:latest";

    private const string FailStdErr = """
        来自引擎的错误信息: Get "https://registry-1.docker.io/v2/": context deadline exceeded
        错误代码： E_FAIL
        如果此错误是意外错误，请考虑搜索现有问题或在 https://github.com/microsoft/WSL/issues 提交新问题。
        """;

    /// <summary>第一帧:只有"这层要拉",还没拿到字节数。</summary>
    private static readonly IReadOnlyList<string> FrameFsLayer =
    [
        "latest: Pulling from library/nginx",
        "ecc510c1e359: Pulling fs layer",
        "04bdbc7ea6b5: Pulling fs layer",
    ];

    /// <summary>第二帧:同一块屏幕回写后的样子 —— 行数不变,内容变新(实测每层只占一行)。</summary>
    private static readonly IReadOnlyList<string> FrameDownloading =
    [
        "latest: Pulling from library/nginx",
        "ecc510c1e359: Downloading 1.073MB",
        "04bdbc7ea6b5: Pull complete",
    ];

    // ---------- AC1 · 走伪终端,命令映射不变 ----------

    [Fact]
    public async Task Pull_StartsWslcThroughThePseudoTerminal()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        cli.ConsoleHandler = FramesThenDone([FrameFsLayer], FixtureLoader.Read("wslc-pull-new.txt"));
        var vm = Create(cli);
        vm.PullText = NginxRef;

        await vm.PullAsync();

        Assert.Equal(1, cli.CountConsoleCalls("pull"));                  // AC1:拉取走 RunWithConsoleAsync
        Assert.Equal(new[] { "pull", NginxRef }, cli.LastConsoleCall("pull"));   // 参数与普通管道完全一致
        Assert.Equal(0, cli.CountCalls("pull") - cli.CountConsoleCalls("pull")); // 没有偷偷再跑一遍普通管道
        Assert.Equal("已拉取 " + NginxRef, vm.Notice);                     // 结果解析没被新路径破坏
    }

    // ---------- AC2 / AC3 · 中途就有反馈 ----------

    [Fact]
    public async Task Pull_WhileItIsRunning_ShowsLinesAndSummary()
    {
        var gate = new FrameGate([FrameFsLayer, FrameDownloading], FixtureLoader.Read("wslc-pull-new.txt"));
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        cli.ConsoleHandler = gate.Handler;
        var vm = Create(cli);
        vm.PullText = NginxRef;

        var pull = vm.PullAsync();
        await WaitUntilAsync(() => vm.HasPullProgress);

        Assert.True(vm.IsBusy);                                       // 进程还在跑,但用户已经看得见东西了
        Assert.Contains("ecc510c1e359: Downloading 1.073MB", vm.PullProgressText);
        Assert.Contains("latest: Pulling from library/nginx", vm.PullProgressText);
        Assert.Equal("已下载 1.1 MB · 完成 1/2 层", vm.PullSummary);       // AC2
        Assert.True(vm.HasPullSummary);

        gate.Complete();
        await pull;

        Assert.Equal("已拉取 " + NginxRef, vm.Notice);
    }

    [Fact]
    public async Task PullProgressText_HoldsOneRowPerLayer()
    {
        var gate = new FrameGate([FrameDownloading], FixtureLoader.Read("wslc-pull-new.txt"));
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        cli.ConsoleHandler = gate.Handler;
        var vm = Create(cli);
        vm.PullText = NginxRef;

        var pull = vm.PullAsync();
        await WaitUntilAsync(() => vm.HasPullProgress);

        // 界面文本 = 屏幕行,行数是"头行 + 层数",不是"帧数 × 层数"(AC3:一行=一层最新状态)
        var rows = vm.PullProgressText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, rows.Length);

        gate.Complete();
        await pull;
    }

    [Fact]
    public async Task Pull_WithoutManifestYet_HidesTheSummaryRow()
    {
        var gate = new FrameGate(
            [["latest: Pulling from library/nginx"]],
            FixtureLoader.Read("wslc-pull-uptodate.txt"));
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        cli.ConsoleHandler = gate.Handler;
        var vm = Create(cli);
        vm.PullText = "busybox:latest";

        var pull = vm.PullAsync();
        await WaitUntilAsync(() => vm.HasPullProgress);

        Assert.False(vm.HasPullSummary);        // 层数还不知道,那句汇总不该挂个空壳
        Assert.Equal("", vm.PullSummary);

        gate.Complete();
        await pull;
    }

    // ---------- 去重:同一块屏幕推两次不重复刷新 ----------

    [Fact]
    public async Task PullProgress_DuplicateFramesAreCollapsed()
    {
        var gate = new FrameGate([FrameFsLayer, FrameFsLayer, FrameDownloading], FixtureLoader.Read("wslc-pull-new.txt"));
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        cli.ConsoleHandler = gate.Handler;
        var vm = Create(cli);
        vm.PullText = NginxRef;

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var pull = vm.PullAsync();
        await WaitUntilAsync(() => vm.HasPullProgress);
        gate.Complete();
        await pull;

        // 实测一帧常被拆成两个 chunk,同一块屏幕会被推上来两次;上屏只应发生一次:
        // 开始清空 1 次 + 上屏 2 次(两个不同帧)+ 结束收起 1 次 = 4,不是 5。
        Assert.Equal(4, raised.Count(name => name == nameof(ImagesPageViewModel.PullProgressText)));
    }

    // ---------- AC4 · 结束 / 失败 / 取消都要收起 ----------

    [Fact]
    public async Task PullDone_HidesTheProgressPanel()
    {
        var gate = new FrameGate([FrameDownloading], FixtureLoader.Read("wslc-pull-new.txt"));
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        cli.ConsoleHandler = gate.Handler;
        var vm = Create(cli);
        vm.PullText = NginxRef;

        var pull = vm.PullAsync();
        await WaitUntilAsync(() => vm.HasPullProgress);
        Assert.True(vm.HasPullProgress);

        gate.Complete();
        await pull;

        Assert.False(vm.HasPullProgress);           // AC4:结果条 + 新列表接手,进度不再占地方
        Assert.Equal("", vm.PullProgressText);
        Assert.False(vm.HasPullSummary);
    }

    [Fact]
    public async Task PullFailed_HidesTheProgressAndKeepsTheError()
    {
        var gate = new FrameGate([FrameDownloading], "");
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        cli.ConsoleHandler = gate.Handler;
        var vm = Create(cli);
        vm.PullText = NginxRef;

        var pull = vm.PullAsync();
        await WaitUntilAsync(() => vm.HasPullProgress);

        gate.Complete(1, FailStdErr);
        Assert.Null(await pull);

        Assert.True(vm.HasError);                        // AC4:失败也只留错误条
        Assert.Contains("E_FAIL", vm.Error);
        Assert.False(vm.HasPullProgress);
        Assert.Equal(2, vm.Items.Count);                 // 列表数据不丢
    }

    [Fact]
    public async Task PullCancelled_HidesTheProgressAndReportsCancellation()
    {
        var gate = new FrameGate([FrameDownloading], "");
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        cli.ConsoleHandler = gate.Handler;
        var vm = Create(cli);
        vm.PullText = NginxRef;

        var pull = vm.PullAsync();
        await WaitUntilAsync(() => vm.HasPullProgress);

        vm.CancelPull();
        Assert.Null(await pull);

        Assert.False(vm.HasPullProgress);                // AC6:取消后不留半截进度
        Assert.Equal("", vm.PullProgressText);
        Assert.False(vm.HasError);
        Assert.Equal("已取消拉取 " + NginxRef, vm.Notice);
    }

    // ---------- 上一次的残留不能跟着下一次拉取跑 ----------

    [Fact]
    public async Task SecondPull_ResetsTheDedupeMemoryAndShowsTheSameFrameAgain()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        var vm = Create(cli);
        vm.PullText = NginxRef;

        var first = new FrameGate([FrameDownloading], FixtureLoader.Read("wslc-pull-new.txt"));
        cli.ConsoleHandler = first.Handler;
        var pull1 = vm.PullAsync();
        await WaitUntilAsync(() => vm.HasPullProgress);
        first.Complete();
        await pull1;
        Assert.Equal("", vm.PullProgressText);

        // 第二次拉取推送的是和上次一模一样的帧:如果开始时的去重记忆没清掉,这次就一帧都上不了屏
        var second = new FrameGate([FrameDownloading], FixtureLoader.Read("wslc-pull-new.txt"));
        cli.ConsoleHandler = second.Handler;
        var pull2 = vm.PullAsync();

        await WaitUntilAsync(() => vm.HasPullProgress);
        Assert.Contains("ecc510c1e359: Downloading 1.073MB", vm.PullProgressText);

        second.Complete();
        await pull2;
    }

    [Fact]
    public void PullProgress_IsQuietBeforeAnyOperation()
    {
        var vm = Create(new RoutingWslcCli { ListJson = TwoImages });

        Assert.Equal("", vm.PullProgressText);
        Assert.False(vm.HasPullProgress);
        Assert.False(vm.HasPullSummary);
        Assert.Equal("", vm.PullSummary);
    }

    // ---------- 汇总数字只许向前 ----------

    [Fact]
    public async Task Pull_WhenALayerStopsReportingBytes_SummaryDoesNotGoBackwards()
    {
        // 第二帧里 ecc510c1e359 变成 Download complete:那一行不再带字节数。
        // 没有台账的话,汇总会掉回只剩另一层的 1.1 MB,界面上的数字就倒着走了。
        IReadOnlyList<string> bothReporting =
        [
            "ecc510c1e359: Downloading 1.6MB",
            "04bdbc7ea6b5: Downloading 545.6kB",
        ];
        IReadOnlyList<string> oneLayerStopped =
        [
            "ecc510c1e359: Download complete",
            "04bdbc7ea6b5: Downloading 1.1MB",
        ];

        var gate = new FrameGate([bothReporting, oneLayerStopped], FixtureLoader.Read("wslc-pull-new.txt"));
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        cli.ConsoleHandler = gate.Handler;
        var vm = Create(cli);
        vm.PullText = NginxRef;

        var pull = vm.PullAsync();
        // 两帧是在读线程里连着推的,所以直接等最后一帧落地,别跟中间态抢时间
        await WaitUntilAsync(() => vm.PullSummary == "已下载 2.7 MB · 完成 0/2 层");

        Assert.Equal("已下载 2.7 MB · 完成 0/2 层", vm.PullSummary);   // 不再报数的那 1.6 MB 仍然算在内

        gate.Complete();
        await pull;
    }

    [Fact]
    public async Task SecondPull_CountsDownloadedBytesFromScratch()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        var vm = Create(cli);
        vm.PullText = NginxRef;

        var first = new FrameGate([FrameDownloading], FixtureLoader.Read("wslc-pull-new.txt"));
        cli.ConsoleHandler = first.Handler;
        var pull1 = vm.PullAsync();
        await WaitUntilAsync(() => vm.PullSummary == "已下载 1.1 MB · 完成 1/2 层");
        first.Complete();
        await pull1;

        // 第二趟屏幕上还没报过字节:台账要没清,汇总会从上一趟的 1.1 MB 接着往上涨
        var second = new FrameGate([FrameFsLayer], FixtureLoader.Read("wslc-pull-new.txt"));
        cli.ConsoleHandler = second.Handler;
        var pull2 = vm.PullAsync();
        await WaitUntilAsync(() => vm.PullSummary == "已下载 0 B · 完成 0/2 层");

        second.Complete();
        await pull2;
    }

    // ---------- 辅助 ----------

    private static ImagesPageViewModel Create(RoutingWslcCli cli) =>
        new(new ImagesService(cli), uiThread: null);

    /// <summary>不进"挂起"闸门:把帧全推完就直接成功结束(用来测不需要中途断言的场景)。</summary>
    private static Func<IReadOnlyList<string>, Action<IReadOnlyList<string>>?, CancellationToken, Task<WslcCliResult>>
        FramesThenDone(
            IReadOnlyList<IReadOnlyList<string>> frames,
            string stdout,
            int exitCode = 0,
            string stderr = "")
        => (arguments, onVisibleLines, cancellationToken) =>
        {
            foreach (var frame in frames) onVisibleLines?.Invoke(frame);
            return Task.FromResult(new WslcCliResult(exitCode, stdout, stderr));
        };

    /// <summary>推完帧就挂住,由测试决定这次拉取怎么收场(成功 / 失败 / 取消)。</summary>
    private sealed class FrameGate
    {
        private readonly IReadOnlyList<IReadOnlyList<string>> _frames;
        private readonly string _stdout;
        private readonly TaskCompletionSource<WslcCliResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FrameGate(IReadOnlyList<IReadOnlyList<string>> frames, string stdout)
        {
            _frames = frames;
            _stdout = stdout;
        }

        public async Task<WslcCliResult> Handler(
            IReadOnlyList<string> arguments,
            Action<IReadOnlyList<string>>? onVisibleLines,
            CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => _done.TrySetCanceled(cancellationToken));
            foreach (var frame in _frames) onVisibleLines?.Invoke(frame);

            // 这一句还挂着,VM 那边就处在"进程在跑、屏幕上已经有进度"的状态
            return await _done.Task.ConfigureAwait(false);
        }

        public void Complete(int exitCode = 0, string stderr = "") =>
            _done.TrySetResult(new WslcCliResult(exitCode, _stdout, stderr));
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

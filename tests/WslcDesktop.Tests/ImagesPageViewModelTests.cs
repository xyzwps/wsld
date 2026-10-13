using System.Diagnostics;
using WslcDesktop.Core.Services;
using WslcDesktop.Core.ViewModels;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>功能 003 · AC7/AC8/AC9/AC10/AC12:镜像列表页 ViewModel 行为。</summary>
public class ImagesPageViewModelTests
{
    private const string BusyBox = """{"ID":"aaef90e06523","Repository":"docker.1ms.run/library/busybox","Tag":"latest","Size":"4.45MB","Containers":"2","CreatedAt":"2026-05-13 10:21:49 +0800 GMT+8","Digest":"<none>"}""";
    private const string Hello = """{"ID":"e2ac70e7319a","Repository":"docker.1ms.run/library/hello-world","Tag":"latest","Size":"10.1kB","Containers":"0","CreatedAt":"2026-03-24 05:33:59 +0800 GMT+8","Digest":"<none>"}""";
    private const string Alpine = """{"ID":"ccc000ccc111","Repository":"alpine","Tag":"3.20","Size":"7.8MB","Containers":"1","CreatedAt":"2026-01-01 08:00:00 +0800 GMT+8","Digest":"<none>"}""";
    private const string ThreeImages = BusyBox + "\n" + Hello + "\n" + Alpine;

    private static (ImagesPageViewModel Vm, FakeWslcCli Cli) Create(string stdout = "", int exitCode = 0)
    {
        var cli = new FakeWslcCli().AlwaysReturn(exitCode, stdout);
        var vm = new ImagesPageViewModel(new ImagesService(cli), uiThread: null);
        return (vm, cli);
    }

    [Fact]
    public void Constructor_TriggersFirstRefresh()
    {
        // AC7:构造即加载;FakeWslcCli 同步完成,构造返回时首轮刷新已经结束。
        var (vm, cli) = Create(ThreeImages);

        Assert.Single(cli.Calls);
        Assert.Equal(3, vm.Items.Count);
        Assert.False(vm.HasError);
        Assert.NotEqual("尚未刷新", vm.LastRefreshedAtText);
    }

    [Fact]
    public void Summary_CountsFromFullList()
    {
        var (vm, _) = Create(ThreeImages);
        Assert.Equal("共 3 个镜像 · 3 个有标签 · 2 个正被容器引用", vm.Summary);
    }

    [Fact]
    public void Summary_WhenNoImages()
    {
        var (vm, _) = Create("");
        Assert.Equal("本机还没有镜像", vm.Summary);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void Refresh_InUseFirst_ThenRepositoryOrder()
    {
        // AC9
        var (vm, _) = Create(ThreeImages);

        Assert.Equal("alpine:3.20", vm.Items[0].DisplayName);   // 被引用 + 仓库名最靠前
        Assert.Equal("docker.1ms.run/library/busybox:latest", vm.Items[1].DisplayName);
        Assert.Equal("docker.1ms.run/library/hello-world:latest", vm.Items[2].DisplayName);
    }

    [Theory]
    [InlineData("hello", 1)]
    [InlineData("ALPINE", 1)]
    [InlineData("3.20", 1)]
    [InlineData("AAEF90E06523", 1)]
    [InlineData("latest", 2)]
    [InlineData("library", 2)]
    [InlineData("不存在", 0)]
    public void Search_FiltersCaseInsensitive_AcrossRepositoryTagAndId(string needle, int expected)
    {
        // AC8
        var (vm, _) = Create(ThreeImages);

        vm.SearchText = needle;

        Assert.Equal(expected, vm.Items.Count);
        Assert.Equal(3, vm.TotalCount);                 // AC10:汇总不受过滤影响
        Assert.Equal("共 3 个镜像 · 3 个有标签 · 2 个正被容器引用", vm.Summary);
    }

    [Fact]
    public async Task RefreshFailure_KeepsPreviousData_AndShowsCleanedError()
    {
        var cli = new FakeWslcCli()
            .Enqueue(0, ThreeImages)
            .AlwaysReturn(1, "", "错误代码： WSLC_E_ENGINE_NOT_READY" + Environment.NewLine +
                                 "如果此错误是意外错误，请考虑搜索现有问题或在 https://github.com/microsoft/WSL/issues 提交新问题。");
        var vm = new ImagesPageViewModel(new ImagesService(cli), uiThread: null);
        Assert.Equal(3, vm.Items.Count);

        await vm.RefreshAsync();

        Assert.True(vm.HasError);
        Assert.Equal(3, vm.Items.Count);                                   // AC7:旧数据保留
        Assert.Contains("WSLC_E_ENGINE_NOT_READY", vm.Error);
        Assert.DoesNotContain("如果此错误是意外错误", vm.Error);             // 样板提示被清掉
    }

    [Fact]
    public async Task RefreshAfterError_ClearsErrorWhenItWorksAgain()
    {
        var cli = new FakeWslcCli()
            .Enqueue(0, ThreeImages)
            .Enqueue(1, "", "boom")
            .Enqueue(0, ThreeImages);
        var vm = new ImagesPageViewModel(new ImagesService(cli), uiThread: null);

        await vm.RefreshAsync();
        Assert.True(vm.HasError);

        await vm.RefreshAsync();
        Assert.False(vm.HasError);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task Refresh_IgnoresReentrantCalls()
    {
        var cli = new GateCli(ThreeImages);
        var vm = new ImagesPageViewModel(new ImagesService(cli), uiThread: null);
        await cli.Started.Task;

        var second = vm.RefreshAsync();   // 第一次还没结束,应当被直接忽略
        await Task.Delay(30);
        Assert.Single(cli.Calls);

        await second;                        // 被忽略的那次是同步返回
        Assert.Single(cli.Calls);

        cli.Release();                       // 让构造触发的第一次刷新跑完
        await WaitUntilAsync(() => vm.Items.Count == 3);
        Assert.Single(cli.Calls);            // 全程只产生一次进程调用
    }

    [Fact]
    public async Task NoAutoRefresh_PollsNothing()
    {
        // AC12:镜像页不自动刷新,放着不动不会产生进程调用。
        var (vm, cli) = Create(ThreeImages);

        await Task.Delay(150);

        Assert.Single(cli.Calls);
        Assert.Equal(3, vm.Items.Count);
    }

    /// <summary>第一次调用挂起,等 Release 才返回,用来验证防重入。</summary>
    private sealed class GateCli : IWslcCli
    {
        private readonly string _stdout;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GateCli(string stdout) => _stdout = stdout;

        public List<IReadOnlyList<string>> Calls { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public Task<WslcCliResult> RunLinesAsync(IReadOnlyList<string> arguments, Action<string>? onStdoutLine, CancellationToken cancellationToken = default) =>
            RunAsync(arguments, cancellationToken);

        // 这些闸门假 CLI 只关心"调用有没有挂起",用不到实时上屏,所以伪终端路径同样转给一次性实现。
        public Task<WslcCliResult> RunWithConsoleAsync(IReadOnlyList<string> arguments, Action<IReadOnlyList<string>>? onVisibleLines, CancellationToken cancellationToken = default) =>
            RunAsync(arguments, cancellationToken);

        public async Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments);
            Started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return new WslcCliResult(0, _stdout, "");
        }
    }

    /// <summary>等待后台刷新落地(防抖测试里唯一不能同步断言的部分)。</summary>
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
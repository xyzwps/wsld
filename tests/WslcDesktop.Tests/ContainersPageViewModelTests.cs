using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;
using WslcDesktop.Core.ViewModels;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>功能 001 · AC1/AC2/AC5/AC6/AC7:容器列表页 ViewModel 行为。</summary>
public class ContainersPageViewModelTests
{
    private const string RunningJson = """{"ID":"aaaa11112222","Names":"web","Image":"nginx:latest","State":"running","Status":"Up 2 hours","Command":"nginx -g","Networks":"bridge"}""";
    private const string ExitedJson = """{"ID":"bbbb22223333","Names":"db","Image":"postgres:16","State":"exited","Status":"Exited (0) 5 minutes ago","Command":"postgres","Networks":"bridge"}""";
    private const string TwoContainers = RunningJson + "\n" + ExitedJson;

    private static (ContainersPageViewModel Vm, FakeWslcCli Cli) Create(string stdout = "", int exitCode = 0)
    {
        var cli = new FakeWslcCli().AlwaysReturn(exitCode, stdout);
        var vm = new ContainersPageViewModel(new ContainersService(cli), uiThread: null);
        return (vm, cli);
    }

    [Fact]
    public void Constructor_TriggersFirstRefresh()
    {
        // AC1:构造即加载;FakeWslcCli 同步完成,构造返回时首轮刷新已结束。
        var (vm, cli) = Create(TwoContainers);
        Assert.Single(cli.Calls);
        Assert.Equal(2, vm.Items.Count);
        Assert.False(vm.HasError);
    }

    [Fact]
    public void Refresh_PopulatesItemsAndCounts()
    {
        var (vm, _) = Create(TwoContainers);

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(2, vm.TotalCount);
        Assert.Equal(1, vm.RunningCount);
        Assert.Equal("共 2 个容器 · 1 个运行中", vm.Summary);
        Assert.NotEqual("尚未刷新", vm.LastRefreshedAtText);
    }

    [Fact]
    public void Refresh_RunningContainersFirst()
    {
        var (vm, _) = Create(TwoContainers);
        Assert.Equal("web", vm.Items[0].DisplayName); // running 排前
    }

    [Fact]
    public void ShowStoppedFalse_HidesExited()
    {
        var (vm, _) = Create(TwoContainers);

        vm.ShowStopped = false;
        Assert.Equal(2, vm.TotalCount);          // 汇总仍基于全量
        Assert.Single(vm.Items);                 // 列表只剩运行中
        Assert.Equal("web", vm.Items[0].DisplayName);
    }

    [Fact]
    public void Search_FiltersByNameImageAndId_CaseInsensitive()
    {
        var (vm, _) = Create(TwoContainers);

        vm.SearchText = "POSTGRES";
        Assert.Single(vm.Items);
        Assert.Equal("db", vm.Items[0].DisplayName);

        vm.SearchText = "aaaa1111"; // 按 ID 子串
        Assert.Single(vm.Items);
        Assert.Equal("web", vm.Items[0].DisplayName);

        vm.SearchText = "";
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public void Search_DoesNotCallCli()
    {
        var (vm, cli) = Create(TwoContainers);
        var callsBefore = cli.Calls.Count;

        vm.SearchText = "web";

        Assert.Equal(callsBefore, cli.Calls.Count); // AC6:过滤不重新调用 CLI
    }

    [Fact]
    public async Task Refresh_Failure_KeepsPreviousItems_AndSetsError()
    {
        var cli = new FakeWslcCli().AlwaysReturn(0, TwoContainers);
        var vm = new ContainersPageViewModel(new ContainersService(cli), uiThread: null);
        Assert.Equal(2, vm.Items.Count);

        cli.AlwaysReturn(1, "", "dockerd 未运行");
        await vm.RefreshAsync(CancellationToken.None);

        Assert.True(vm.HasError);
        Assert.Contains("dockerd 未运行", vm.Error);
        Assert.Equal(2, vm.Items.Count); // AC1:保留上一次数据
    }

    [Fact]
    public void Refresh_NdjsonParseFailure_SurfacesErrorMessage()
    {
        var (vm, _) = Create("这不是 JSON");
        Assert.True(vm.HasError);
        Assert.Contains("第 1 行", vm.Error);
    }

    [Fact]
    public void Refresh_IsReentrantGuarded()
    {
        var gate = new GateWslcCli();
        // 构造函数触发的那次刷新会挂在 gate 上,占据"正在刷新"状态。
        var vm = new ContainersPageViewModel(new ContainersService(gate), uiThread: null);
        SpinWait.SpinUntil(() => gate.StartedCalls >= 1, TimeSpan.FromSeconds(5));

        var second = vm.RefreshAsync(CancellationToken.None); // AC7:应直接返回

        Assert.True(second.IsCompleted);
        Assert.Equal(1, gate.StartedCalls);
        gate.ReleaseAll();
    }

    [Fact]
    public void EmptyList_WhenNoContainers()
    {
        var (vm, _) = Create();

        Assert.Empty(vm.Items);
        Assert.False(vm.HasError);           // AC2:空不是错误
        Assert.Equal(0, vm.TotalCount);
    }

    /// <summary>调用挂起直到 Release,用于测试防重入。</summary>
    private sealed class GateWslcCli : IWslcCli
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartedCalls;

        public Task<WslcCliResult> RunLinesAsync(IReadOnlyList<string> arguments, Action<string>? onStdoutLine, CancellationToken cancellationToken = default) =>
            RunAsync(arguments, cancellationToken);

        public async Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            StartedCalls++;
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new WslcCliResult(0, "[]", "");
        }

        public void ReleaseAll() => _gate.SetResult();
    }
}
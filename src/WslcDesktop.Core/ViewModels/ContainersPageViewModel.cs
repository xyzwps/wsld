using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.ViewModels;

/// <summary>
/// 容器列表页 ViewModel(功能 001)。
/// 构造时自动发起首次刷新(AC1);支持搜索/过滤(AC6)、汇总、自动刷新(AC7)。
/// </summary>
public sealed partial class ContainersPageViewModel : ObservableObject
{
    private readonly ContainersService _service;
    private readonly IUIThread? _uiThread;
    private List<ContainerInfo> _all = new();
    private volatile bool _refreshing;
    private CancellationTokenSource? _autoRefreshCts;

    public ContainersPageViewModel(ContainersService service, IUIThread? uiThread = null)
    {
        _service = service;
        _uiThread = uiThread;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None));
        _ = RefreshAsync(CancellationToken.None); // AC1:启动即加载,失败只展示错误不崩溃
    }

    public ObservableCollection<ContainerInfo> Items { get; } = new();

    public IAsyncRelayCommand RefreshCommand { get; }

    /// <summary>最近一次成功加载的全部容器(未过滤)数量。</summary>
    [ObservableProperty] private int _totalCount;

    /// <summary>其中运行中的数量。</summary>
    [ObservableProperty] private int _runningCount;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showStopped = true;
    [ObservableProperty] private int _autoRefreshSeconds = 5;
    [ObservableProperty] private string _lastRefreshedAtText = "尚未刷新";

    public string Summary => $"共 {TotalCount} 个容器 · {RunningCount} 个运行中";

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnRunningCountChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnErrorChanged(string? value) => HasError = !string.IsNullOrEmpty(value);
    partial void OnSearchTextChanged(string value) => Rebuild();
    partial void OnShowStoppedChanged(bool value) => Rebuild();

    /// <summary>刷新列表。上一次刷新未完成时直接返回(AC7 防重入)。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            IReadOnlyList<ContainerInfo> loaded;
            try
            {
                loaded = await _service.ListAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // AC1:失败保留上一次成功数据,仅展示错误
                await RunOnUiAsync(() => Error = ex.Message).ConfigureAwait(false);
                return;
            }

            await RunOnUiAsync(() =>
            {
                _all = loaded
                    .OrderBy(c => c.IsRunning ? 0 : 1)
                    .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Error = null;
                TotalCount = _all.Count;
                RunningCount = _all.Count(c => c.IsRunning);
                LastRefreshedAtText = DateTimeOffset.Now.ToString("HH:mm:ss");
                Rebuild();
            }).ConfigureAwait(false);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>启动自动刷新循环(0 秒表示关闭,循环会持续等待直到用户重新选择间隔)。</summary>
    public void StartAutoRefresh()
    {
        StopAutoRefresh();
        var cts = new CancellationTokenSource();
        _autoRefreshCts = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var seconds = AutoRefreshSeconds;
                var delay = seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromMilliseconds(300);
                try
                {
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                await RefreshAsync(cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);
    }

    public void StopAutoRefresh()
    {
        _autoRefreshCts?.Cancel();
        _autoRefreshCts = null;
    }

    private void Rebuild()
    {
        var needle = SearchText.Trim();
        Items.Clear();
        foreach (var container in _all)
        {
            if (!ShowStopped && !container.IsRunning) continue;
            if (needle.Length > 0 && !Matches(container, needle)) continue;
            Items.Add(container);
        }
    }

    private static bool Matches(ContainerInfo container, string needle) =>
        Contains(container.DisplayName, needle) ||
        Contains(container.Image, needle) ||
        Contains(container.Id, needle);

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private Task RunOnUiAsync(Action action)
    {
        if (_uiThread is null)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiThread.Post(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }
}
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;
using WslcDesktop.Core.WslcCli;
using static WslcDesktop.Core.I18n.Loc;

namespace WslcDesktop.Core.ViewModels;

/// <summary>
/// 容器列表页 ViewModel。
/// 功能 001:启动即加载、搜索过滤、汇总、自动刷新、刷新防重入。
/// 功能 002:启动/停止/重启/删除,全局单操作互斥,成功后刷新一次。
/// </summary>
public sealed partial class ContainersPageViewModel : ObservableObject
{
    private readonly ContainersService _service;
    private readonly IUIThread? _uiThread;
    private readonly Dictionary<string, ContainerItemViewModel> _known = new();
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

    public ObservableCollection<ContainerItemViewModel> Items { get; } = new();

    public IAsyncRelayCommand RefreshCommand { get; }

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showStopped = true;
    [ObservableProperty] private int _autoRefreshSeconds = 5;
    [ObservableProperty] private string _lastRefreshedAtText = T("refresh.never");

    /// <summary>是否有生命周期操作正在进行(功能 002 · AC5)。用字段而非自动属性,避免通知风暴。</summary>
    public bool IsBusy => Volatile.Read(ref _operationActive) != 0;

    private int _operationActive;

    public string Summary => Fmt("containers.summary", TotalCount, RunningCount);

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnRunningCountChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnErrorChanged(string? value) => HasError = !string.IsNullOrEmpty(value);
    partial void OnSearchTextChanged(string value) => Rebuild();
    partial void OnShowStoppedChanged(bool value) => Rebuild();

    /// <summary>
    /// 刷新列表。上一次刷新未完成时直接返回(功能 001 · AC7);
    /// 有操作正在进行时同样跳过,避免列表在操作过程中被替换(功能 002 · AC6)。
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_refreshing || IsBusy) return;
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
                // 功能 001 · AC1:失败保留上一次成功数据,仅展示错误
                await RunOnUiAsync(() => Error = WslcError.Describe(ex)).ConfigureAwait(false);
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

    /// <summary>
    /// 执行一次容器操作(功能 002)。成功则刷新一次(AC7),失败只展示清洗后的错误(AC8)。
    /// 同一时刻只允许一个操作(AC5),重复调用会被直接忽略。
    /// </summary>
    public async Task ExecuteAsync(
        ContainerItemViewModel item,
        ContainerAction action,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0) return;

        var succeeded = false;
        item.IsBusy = true;
        OnPropertyChanged(nameof(IsBusy));
        RefreshActionStates();
        try
        {
            try
            {
                await _service.RunAsync(action, item.Id, force, cancellationToken).ConfigureAwait(false);
                succeeded = true;
            }
            catch (OperationCanceledException)
            {
                // 取消视为"什么都没发生",不报错也不刷新。
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() => Error = WslcError.Describe(ex)).ConfigureAwait(false);
            }
        }
        finally
        {
            item.IsBusy = false;
            Volatile.Write(ref _operationActive, 0);
            OnPropertyChanged(nameof(IsBusy));
            RefreshActionStates();
        }

        if (succeeded)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
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

    private void RefreshActionStates()
    {
        foreach (var item in Items)
        {
            item.RaiseActionStates();
        }
    }

    private void Rebuild()
    {
        var needle = SearchText.Trim();
        var wanted = new List<ContainerItemViewModel>();

        foreach (var container in _all)
        {
            if (!ShowStopped && !container.IsRunning) continue;
            if (needle.Length > 0 && !Matches(container, needle)) continue;

            // AC11:按 ID 复用行对象,只替换数据,保住 IsBusy 与按钮状态。
            if (!_known.TryGetValue(container.Id, out var item))
            {
                item = new ContainerItemViewModel(this, container);
                _known[container.Id] = item;
            }
            else
            {
                item.UpdateModel(container);
            }
            wanted.Add(item);
        }

        foreach (var stale in _known.Keys.Where(id => !_all.Any(c => c.Id == id)).ToList())
        {
            _known.Remove(stale);
        }

        if (!SameSequence(Items, wanted))
        {
            Items.Clear();
            foreach (var item in wanted)
            {
                Items.Add(item);
            }
        }
    }

    /// <summary>逐元素引用比较:内容没变时完全不重建 Items,避免界面闪断。</summary>
    private static bool SameSequence(IReadOnlyList<ContainerItemViewModel> current, IReadOnlyList<ContainerItemViewModel> wanted)
    {
        if (current.Count != wanted.Count) return false;
        for (var i = 0; i < current.Count; i++)
        {
            if (!ReferenceEquals(current[i], wanted[i])) return false;
        }
        return true;
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
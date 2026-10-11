using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Core.ViewModels;

/// <summary>
/// 镜像列表页 ViewModel(功能 003)。
/// 与容器页保持同样的约定:启动即加载、失败保留旧数据只报错、搜索实时过滤、刷新防重入。
/// 差别是镜像几乎不会自己变,所以不做自动刷新(AC12),只提供手动刷新与操作后的定向更新。
/// </summary>
public sealed partial class ImagesPageViewModel : ObservableObject
{
    private readonly ImagesService _service;
    private readonly IUIThread? _uiThread;
    private List<ImageInfo> _all = new();
    private volatile bool _refreshing;

    public ImagesPageViewModel(ImagesService service, IUIThread? uiThread = null)
    {
        _service = service;
        _uiThread = uiThread;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None));
        _ = RefreshAsync(CancellationToken.None); // AC7:启动即加载,失败只展示错误不崩溃
    }

    public ObservableCollection<ImageInfo> Items { get; } = new();

    public IAsyncRelayCommand RefreshCommand { get; }

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _inUseCount;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _lastRefreshedAtText = "尚未刷新";

    /// <summary>汇总行(AC10)。数字来自全量数据,不受搜索过滤影响。</summary>
    public string Summary => TotalCount == 0
        ? "本机还没有镜像"
        : $"共 {TotalCount} 个镜像 · {TaggedCount} 个有标签 · {InUseCount} 个正被容器引用";

    private int TaggedCount => _all.Count(i => i.IsTagged);

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnInUseCountChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnErrorChanged(string? value) => HasError = !string.IsNullOrEmpty(value);
    partial void OnSearchTextChanged(string value) => Rebuild();

    /// <summary>刷新列表;上一次刷新还没结束时直接返回(防重入,与功能 001 一致)。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            IReadOnlyList<ImageInfo> loaded;
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
                // AC7:失败保留上一次成功数据,只展示清洗后的错误。
                await RunOnUiAsync(() => Error = WslcError.Describe(ex)).ConfigureAwait(false);
                return;
            }

            await RunOnUiAsync(() =>
            {
                _all = Sort(loaded);
                Error = null;
                TotalCount = _all.Count;
                InUseCount = _all.Count(i => i.IsInUse);
                LastRefreshedAtText = DateTimeOffset.Now.ToString("HH:mm:ss");
                Rebuild();
            }).ConfigureAwait(false);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>AC9:被容器引用的在前,其次仓库名、标签字母序。</summary>
    private static List<ImageInfo> Sort(IEnumerable<ImageInfo> source) =>
        source
            .OrderByDescending(i => i.IsInUse)
            .ThenBy(i => i.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.TagText, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void Rebuild()
    {
        var needle = SearchText.Trim();
        var wanted = needle.Length == 0
            ? _all
            : _all.Where(i => Matches(i, needle)).ToList();

        if (!SameSequence(Items, wanted))
        {
            Items.Clear();
            foreach (var item in wanted)
            {
                Items.Add(item);
            }
        }
    }

    /// <summary>内容没变时完全不重建 Items,避免界面闪断。</summary>
    private static bool SameSequence(IReadOnlyList<ImageInfo> current, IReadOnlyList<ImageInfo> wanted)
    {
        if (current.Count != wanted.Count) return false;
        for (var i = 0; i < current.Count; i++)
        {
            if (!ReferenceEquals(current[i], wanted[i])) return false;
        }
        return true;
    }

    /// <summary>AC8:仓库名、标签、镜像 ID 都参与匹配,不区分大小写。</summary>
    private static bool Matches(ImageInfo image, string needle) =>
        Contains(image.Repository, needle) ||
        Contains(image.TagText, needle) ||
        Contains(image.Id, needle);

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
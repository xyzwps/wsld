using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Core.ViewModels;

/// <summary>
/// 镜像页 ViewModel。
/// 功能 003:启动即加载、搜索过滤、排序、汇总、手动刷新(不轮询)。
/// 功能 004:拉取(可取消)与删除(被占用时二次确认升级为强制删除),
/// 全局单操作互斥,成功后刷新一次并给出提示。
/// </summary>
public sealed partial class ImagesPageViewModel : ObservableObject
{
    /// <summary>输入为空时的提示(功能 004 · AC2 的界面侧:空引用绝不启动进程)。</summary>
    public const string EmptyReferenceHint = "请先输入要拉取的镜像引用,例如 docker.1ms.run/library/redis:7。";

    /// <summary>常规删除被"正在使用"拒绝后追加的一句(功能 004 · AC10)。</summary>
    public const string ForceHint = " 该镜像正被容器使用,再点一次「强制删除」才能把它从磁盘上移除。";

    private readonly ImagesService _service;
    private readonly IUIThread? _uiThread;
    private readonly Dictionary<string, ImageItemViewModel> _known = new(StringComparer.OrdinalIgnoreCase);
    private List<ImageInfo> _all = new();
    private volatile bool _refreshing;
    private int _operationActive;
    private CancellationTokenSource? _pullCts;
    private ImageItemViewModel? _busyItem;
    private string _busyTarget = "";
    private DateTimeOffset _busyStartedAt;

    public ImagesPageViewModel(ImagesService service, IUIThread? uiThread = null)
    {
        _service = service;
        _uiThread = uiThread;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None, clearNotice: true));
        PullCommand = new AsyncRelayCommand(() => PullAsync(), () => CanPull);
        CancelPullCommand = new RelayCommand(CancelPull, () => IsPulling);
        _ = RefreshAsync(); // 功能 003 · AC7:启动即加载,失败只展示错误不崩溃
    }

    public ObservableCollection<ImageItemViewModel> Items { get; } = new();

    public IAsyncRelayCommand RefreshCommand { get; }

    /// <summary>拉取输入框对应的命令(AC1/AC5/AC14)。</summary>
    public IAsyncRelayCommand PullCommand { get; }

    /// <summary>取消正在进行的拉取(AC6)。</summary>
    public IRelayCommand CancelPullCommand { get; }

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _inUseCount;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _pullText = "";
    [ObservableProperty] private string _lastRefreshedAtText = "尚未刷新";
    [ObservableProperty] private bool _isPulling;

    /// <summary>汇总行(功能 003 · AC10)。数字来自全量数据,不受搜索过滤影响。</summary>
    public string Summary => TotalCount == 0
        ? "本机还没有镜像"
        : $"共 {TotalCount} 个镜像 · {TaggedCount} 个有标签 · {InUseCount} 个正被容器引用";

    private int TaggedCount => _all.Count(i => i.IsTagged);

    /// <summary>是否有操作正在进行(功能 004 · AC5)。用字段而非自动属性,避免通知风暴。</summary>
    public bool IsBusy => Volatile.Read(ref _operationActive) != 0;

    /// <summary>"拉取"按钮的可用性:忙碌中或输入为空都不允许(AC5/AC2)。</summary>
    public bool CanPull => !IsBusy && !string.IsNullOrWhiteSpace(PullText);

    /// <summary>操作提示条(AC8)。</summary>
    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    /// <summary>
    /// 进度文案(AC7)。实测 `wslc pull` 的输出要等进程结束才一次性冲刷,
    /// 做不出真实百分比,所以这里只报"在做什么 + 已等待几秒",由界面每秒调用 <see cref="Tick"/> 刷新。
    /// </summary>
    public string BusyText => IsBusy ? $"{_busyTarget} · 已等待 {ElapsedSeconds} 秒" : "";

    /// <summary>界面计时器驱动:重新求值 <see cref="BusyText"/>(测试里手动调用,不依赖真实时钟)。</summary>
    public void Tick() => OnPropertyChanged(nameof(BusyText));

    private int ElapsedSeconds => (int)Math.Max(0, (DateTimeOffset.Now - _busyStartedAt).TotalSeconds);

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnInUseCountChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnErrorChanged(string? value) => HasError = !string.IsNullOrEmpty(value);
    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));
    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnPullTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanPull));
        PullCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPullingChanged(bool value) => CancelPullCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// 刷新列表;上一次刷新还没结束时直接返回(功能 003 · AC7),
    /// 有操作进行时同样跳过(功能 004 · AC5),免得列表在操作中途被换掉。
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default, bool clearNotice = false)
    {
        if (_refreshing || IsBusy) return;
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
                // 功能 003 · AC7:失败保留上一次成功数据,只展示清洗后的错误。
                await RunOnUiAsync(() => Error = WslcError.Describe(ex)).ConfigureAwait(false);
                return;
            }

            await RunOnUiAsync(() =>
            {
                _all = Sort(loaded);
                Error = null;
                if (clearNotice) Notice = null; // 手动刷新不保留旧的操作提示
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

    /// <summary>
    /// 拉取一个镜像引用(功能 004 · AC1/AC5/AC6/AC8/AC9)。
    /// 返回 null 表示这次没成:输入为空、已有操作在跑、被取消或失败,原因已写进 Error/Notice。
    /// </summary>
    public async Task<PullResult?> PullAsync(CancellationToken cancellationToken = default)
    {
        var reference = PullText.Trim();
        if (reference.Length == 0)
        {
            // AC2:空引用绝不启动进程,只给一句人话提示。
            await RunOnUiAsync(() =>
            {
                Notice = null;
                Error = EmptyReferenceHint;
            }).ConfigureAwait(false);
            return null;
        }

        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0) return null; // AC5

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pullCts = cts;
        var item = FindItem(reference);
        PullResult? outcome = null;
        var cancelled = false;
        Exception? failure = null;

        Begin(item, $"正在拉取 {reference}", pulling: true);
        try
        {
            try
            {
                outcome = await _service.PullAsync(reference, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true; // AC6:用户点了"取消"
            }
            catch (Exception ex)
            {
                failure = ex; // AC9
            }
        }
        finally
        {
            _pullCts = null;
            SafeDispose(cts);
            await EndAsync().ConfigureAwait(false);
        }

        if (cancelled)
        {
            // AC6:取消之后不刷新(实测半途终止不会留下不完整的镜像),只说明已取消。
            await RunOnUiAsync(() => Notice = $"已取消拉取 {reference}").ConfigureAwait(false);
            return null;
        }

        if (failure is not null)
        {
            await RunOnUiAsync(() => Error = WslcError.Describe(failure)).ConfigureAwait(false);
            return null;
        }

        // AC8:成功 → 提示 + 刷新一次。
        await RunOnUiAsync(() => Notice = outcome!.Notice).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// 删除一个镜像引用(功能 004 · AC4/AC5/AC8/AC9/AC10/AC11)。
    /// <paramref name="force"/> 只由行对象在"上一次常规删除被占用拒绝"之后置为 true。
    /// </summary>
    public async Task<bool> ExecuteRemoveAsync(
        ImageItemViewModel item,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0) return false; // AC5

        var reference = item.Reference;
        var succeeded = false;
        Exception? failure = null;

        Begin(item, $"正在删除 {reference}", pulling: false);
        try
        {
            try
            {
                await _service.RemoveAsync(reference, force, cancellationToken).ConfigureAwait(false);
                succeeded = true;
            }
            catch (OperationCanceledException)
            {
                // 取消视为"什么都没发生",与功能 002 的约定一致。
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }
        finally
        {
            await EndAsync().ConfigureAwait(false);
        }

        if (succeeded)
        {
            // AC11:强制状态用完即复位;和提示一起回写到 UI 线程。
            await RunOnUiAsync(() =>
            {
                item.ForcePending = false;
                Notice = $"已删除 {reference}";
            }).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false); // AC8
            return true;
        }

        if (failure is not null)
        {
            var message = WslcError.Describe(failure);
            var conflict = !force && ImageRemovalConflict.IsConflict(failure);
            if (conflict) message += ForceHint; // AC10:只有"正在使用"这一类失败才升级按钮
            await RunOnUiAsync(() =>
            {
                if (conflict) item.ForcePending = true;
                Error = message;
            }).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>取消正在进行的拉取(AC6)。没有拉取在跑时什么都不做。</summary>
    public void CancelPull()
    {
        var cts = _pullCts;
        if (cts is null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 拉取刚好结束,忽略。
        }
    }

    /// <summary>AC9:被容器引用的在前,其次仓库名、标签字母序。</summary>
    private static List<ImageInfo> Sort(IEnumerable<ImageInfo> source) =>
        source
            .OrderByDescending(i => i.IsInUse)
            .ThenBy(i => i.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.TagText, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void Begin(ImageItemViewModel? item, string target, bool pulling)
    {
        // 三个入口都来自界面点击,这里一定在 UI 线程上。
        Notice = null;
        Error = null;
        _busyItem = item;
        _busyTarget = target;
        _busyStartedAt = DateTimeOffset.Now;
        if (item is not null) item.IsBusy = true;
        IsPulling = pulling;
        RaiseBusyState();
    }

    /// <summary>结束操作:状态回写可能落在后台线程上,统一调度回 UI 线程(AC5)。</summary>
    private Task EndAsync() => RunOnUiAsync(() =>
    {
        Volatile.Write(ref _operationActive, 0);
        if (_busyItem is not null) _busyItem.IsBusy = false;
        _busyItem = null;
        _busyTarget = "";
        IsPulling = false;
        RaiseBusyState();
    });

    private void RaiseBusyState()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanPull));
        OnPropertyChanged(nameof(BusyText));
        PullCommand.NotifyCanExecuteChanged();
        foreach (var item in Items)
        {
            item.RaiseActionStates();
        }
    }

    private ImageItemViewModel? FindItem(string reference) =>
        _known.TryGetValue(reference, out var item) ? item : null;

    private static void SafeDispose(CancellationTokenSource cts)
    {
        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放过,忽略。
        }
    }

    private void Rebuild()
    {
        var needle = SearchText.Trim();
        var wanted = new List<ImageItemViewModel>();

        foreach (var image in _all)
        {
            if (needle.Length > 0 && !Matches(image, needle)) continue;

            // AC12:按引用复用行对象,只替换数据,保住 IsBusy 与 ForcePending。
            if (!_known.TryGetValue(image.Reference, out var item))
            {
                item = new ImageItemViewModel(this, image);
                _known[image.Reference] = item;
            }
            else
            {
                item.UpdateModel(image);
            }
            wanted.Add(item);
        }

        foreach (var stale in _known.Keys
                     .Where(key => !_all.Any(i => string.Equals(i.Reference, key, StringComparison.OrdinalIgnoreCase)))
                     .ToList())
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
    private static bool SameSequence(IReadOnlyList<ImageItemViewModel> current, IReadOnlyList<ImageItemViewModel> wanted)
    {
        if (current.Count != wanted.Count) return false;
        for (var i = 0; i < current.Count; i++)
        {
            if (!ReferenceEquals(current[i], wanted[i])) return false;
        }
        return true;
    }

    /// <summary>功能 003 · AC8:仓库名、标签、镜像 ID 都参与匹配,不区分大小写。</summary>
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
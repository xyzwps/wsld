using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using wsld.Core.Models;
using wsld.Core.Parsing;
using wsld.Core.Services;
using wsld.Core.Settings;
using wsld.Core.WslcCli;
using static wsld.Core.I18n.Loc;

namespace wsld.Core.ViewModels;

/// <summary>
/// 镜像页 ViewModel。
/// 功能 003:启动即加载、搜索过滤、排序、汇总、手动刷新(不轮询)。
/// 功能 004:拉取(可取消)与删除(被占用时二次确认升级为强制删除),
/// 全局单操作互斥,成功后刷新一次并给出提示。
/// 功能 010:启用了镜像源(registry mirror)时,拉取前先改写引用,拉完再把镜像改名回用户输入的名字。
/// 功能 011:拉取期间把 wslc 在伪终端里渲染的分层进度实时搬到界面上,并汇总"已下载多少 / 完成几层"。
/// </summary>
public sealed partial class ImagesPageViewModel : ObservableObject
{
    /// <summary>输入为空时的提示(功能 004 · AC2 的界面侧:空引用绝不启动进程)。</summary>
    public static string EmptyReferenceHint => T("images.empty-reference");

    /// <summary>常规删除被"正在使用"拒绝后追加的一句(功能 004 · AC10)。</summary>
    public static string ForceHint => T("images.force-hint");

    private readonly ImagesService _service;
    private readonly IUIThread? _uiThread;
    private readonly Dictionary<string, ImageItemViewModel> _known = new(StringComparer.OrdinalIgnoreCase);
    private List<ImageInfo> _all = new();
    private volatile bool _refreshing;
    private int _operationActive;
    private CancellationTokenSource? _pullCts;
    private PullProgressSnapshot? _pullProgress;
    private IReadOnlyList<string> _lastPushedLines = Array.Empty<string>();

    /// <summary>
    /// 这一次拉取里"每层已下载字节的历史最大值"台账(功能 011 · AC2)。
    /// 累计发生在后台读线程、清空发生在 UI 线程,两边共用一把锁;
    /// 每一层只增不减,某层不再报字节数(Download complete 之后)也不会让界面上的"已下载"倒退。
    /// </summary>
    private readonly Dictionary<string, long> _pullLayerBytes = new(StringComparer.OrdinalIgnoreCase);

    private ImageItemViewModel? _busyItem;
    private string _busyTarget = "";
    private DateTimeOffset _busyStartedAt;

    public ImagesPageViewModel(ImagesService service, IUIThread? uiThread = null, AppSettings? settings = null)
    {
        _service = service;
        _uiThread = uiThread;
        Settings = settings ?? new AppSettings();
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None, clearNotice: true));
        PullCommand = new AsyncRelayCommand(() => PullAsync(), () => CanPull);
        CancelPullCommand = new RelayCommand(CancelPull, () => IsPulling);
        _ = RefreshAsync(); // 功能 003 · AC7:启动即加载,失败只展示错误不崩溃
    }

    public ObservableCollection<ImageItemViewModel> Items { get; } = new();

    /// <summary>
    /// 拉取期间"屏幕上看得见"的进度文本(功能 011 · AC3)。
    /// 每一层的最新状态占一行,所以文本长度只随层数增长,不会随 wslc 的刷新次数无限变长;
    /// 用 CRLF 拼接是实测结论 —— 只读文本框认 CRLF(与日志区同一套做法)。
    /// </summary>
    public string PullProgressText { get; private set; } = "";

    /// <summary>
    /// 应用设置(功能 010 · AC1)。界面传进来的是 <c>AppState.Current</c> 同一个实例,
    /// 所以设置页改完镜像源,这一页不用重建就能读到新值(说明文案靠 <see cref="RefreshMirrorState"/> 重算)。
    /// </summary>
    public AppSettings Settings { get; }

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
    [ObservableProperty] private string _lastRefreshedAtText = T("refresh.never");
    [ObservableProperty] private bool _isPulling;
    [ObservableProperty] private bool _hasPullProgress;

    /// <summary>汇总行(功能 003 · AC10)。数字来自全量数据,不受搜索过滤影响。</summary>
    public string Summary => TotalCount == 0
        ? T("images.summary.none")
        : Fmt("images.summary", TotalCount, TaggedCount, InUseCount);

    private int TaggedCount => _all.Count(i => i.IsTagged);

    /// <summary>是否有操作正在进行(功能 004 · AC5)。用字段而非自动属性,避免通知风暴。</summary>
    public bool IsBusy => Volatile.Read(ref _operationActive) != 0;

    /// <summary>"拉取"按钮的可用性:忙碌中或输入为空都不允许(AC5/AC2)。</summary>
    public bool CanPull => !IsBusy && !string.IsNullOrWhiteSpace(PullText);

    /// <summary>操作提示条(AC8)。</summary>
    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    /// <summary>
    /// 进度汇总一句话(功能 011 · AC2):已下载量 + 完成层数。
    /// 还没拿到 Manifest(层数为 0)时返回空串,界面上这一行就不占位置。
    /// </summary>
    public string PullSummary => _pullProgress is { TotalLayers: > 0 } p
        ? Fmt("pull.summary", FormatBytes(p.DownloadedBytes), p.DoneLayers, p.TotalLayers)
        : "";

    /// <summary>这一句要不要显示(有层数才显示,免得拉取刚开始挂个空壳)。</summary>
    public bool HasPullSummary => PullSummary.Length > 0;

    private static string FormatBytes(long bytes) => WslcPullProgress.FormatBytes(bytes);

    /// <summary>
    /// 进度文案(AC7)。实测 `wslc pull` 的输出要等进程结束才一次性冲刷,
    /// 做不出真实百分比,所以这里只报"在做什么 + 已等待几秒",由界面每秒调用 <see cref="Tick"/> 刷新。
    /// </summary>
    public string BusyText => IsBusy ? Fmt("busy.elapsed", _busyTarget, ElapsedSeconds) : "";

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
        RefreshMirrorState(); // 功能 010 · AC4:改写预览跟着输入走
    }

    /// <summary>启用镜像源后,这个引用才是真正交给 wslc pull 的(功能 010 · AC2/AC3)。</summary>
    public string EffectivePullReference => RegistryMirrorPolicy.Rewrite(PullText.Trim(), Settings.RegistryMirror).Reference;

    /// <summary>输入框下方那行"实际会拉 …"的说明;没发生改写时是空串(功能 010 · AC4)。</summary>
    public string PullMirrorHint
    {
        get
        {
            var rewrite = RegistryMirrorPolicy.Rewrite(PullText.Trim(), Settings.RegistryMirror);
            return rewrite.Rewritten ? Fmt("images.pull.via-mirror", rewrite.Reference) : "";
        }
    }

    /// <summary>有没有那行说明(界面用它决定要不要留出这一行)。</summary>
    public bool HasPullMirrorHint => PullMirrorHint.Length > 0;

    /// <summary>镜像源设置变了(或页面刚显示)时重算预览文案;设置页与镜像页共用同一份 AppSettings。</summary>
    public void RefreshMirrorState()
    {
        OnPropertyChanged(nameof(EffectivePullReference));
        OnPropertyChanged(nameof(PullMirrorHint));
        OnPropertyChanged(nameof(HasPullMirrorHint));
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
    /// 拉取一个镜像引用(功能 004 · AC1/AC5/AC6/AC8/AC9,功能 010 · AC2/AC3/AC5)。
    /// 输入框里是用户写的名字;启用镜像源时真正拉的是改写后的引用,成功后按设置改回这个名字。
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

        // 功能 010 · AC2/AC3:启用了镜像源,Docker Hub 的引用在这里被改写成镜像站地址。
        var rewrite = RegistryMirrorPolicy.Rewrite(reference, Settings.RegistryMirror);
        var effective = rewrite.Reference;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pullCts = cts;
        var item = FindItem(reference);
        PullResult? outcome = null;
        var cancelled = false;
        string? renameFailure = null;
        Exception? failure = null;

        Begin(item, Fmt("images.pulling", effective), pulling: true);
        try
        {
            try
            {
                outcome = await _service
                    .PullWithProgressAsync(effective, OnPullProgressLines, cts.Token)
                    .ConfigureAwait(false);

                // 功能 010 · AC5:改名属于同一次拉取操作的收尾,不放开全局互斥,免得中途被删除/刷新插队。
                if (rewrite.Rewritten && Settings.RenameMirrorPull && !cts.IsCancellationRequested)
                {
                    var rename = await RenameAfterMirrorAsync(effective, reference, cts.Token).ConfigureAwait(false);
                    if (rename.Tagged) outcome = outcome with { Reference = reference }; // 提示按用户写的名字报
                    renameFailure = rename.Message;
                }
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
            await RunOnUiAsync(() => Notice = Fmt("images.pull-cancelled", effective)).ConfigureAwait(false);
            return null;
        }

        if (failure is not null)
        {
            await RunOnUiAsync(() => Error = WslcError.Describe(failure)).ConfigureAwait(false);
            return null;
        }

        // AC8:成功 → 提示 + 刷新一次。
        // 功能 010 · AC5/AC6:镜像已经在本机了,改名没做干净时绿色提示与红色错误条同时出现,不把结果说成失败。
        // 顺序要紧:刷新成功时会把 Error 清成 null,改名的补充说明必须等刷完再写,否则会被冲掉。
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            Notice = outcome!.Notice;
            if (renameFailure is not null)
            {
                Error = Error is null ? renameFailure : Error + " · " + renameFailure; // 两件事都出错就都说明,不互相吞
            }
        }).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// 进度回调(功能 011 · AC3)。跑在后台读线程上:先把屏幕行解析成结构化进度,
    /// 内容与上一次完全相同时直接丢掉(一帧里 wslc 会连写好几个转义块,不去重就是无谓的界面刷新),
    /// 真正变了才调度回 UI 线程。这里绝不抛异常 —— 刷新失败不该把一次拉取报成命令失败。
    /// </summary>
    private void OnPullProgressLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || SameAsLastPush(lines)) return;
        _lastPushedLines = lines;
        PullProgressSnapshot snapshot;
        lock (_pullLayerBytes)
        {
            snapshot = WslcPullProgress.Parse(lines, _pullLayerBytes);
        }
        try
        {
            // 不等待:进度是"锦上添花",回 UI 线程排不上队也不能拖累读线程。
            _ = RunOnUiAsync(() => ApplyPullProgress(snapshot));
        }
        catch (Exception)
        {
            // 没有 UI 线程时 RunOnUiAsync 会就地执行,这里兜住它的一切异常。
        }
    }

    private bool SameAsLastPush(IReadOnlyList<string> lines)
    {
        var last = _lastPushedLines;
        if (last.Count != lines.Count) return false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!string.Equals(last[i], lines[i], StringComparison.Ordinal)) return false;
        }

        return true;
    }

    /// <summary>UI 线程:整块替换进度行,并刷新那句"已下载 X · 完成 N/M 层"(AC2/AC3)。</summary>
    private void ApplyPullProgress(PullProgressSnapshot snapshot)
    {
        _pullProgress = snapshot;
        PullProgressText = WslcLogText.Join(snapshot.Lines);
        HasPullProgress = snapshot.Lines.Count > 0;
        OnPropertyChanged(nameof(PullProgressText));
        OnPropertyChanged(nameof(PullSummary));
        OnPropertyChanged(nameof(HasPullSummary));
    }

    /// <summary>
    /// 把带镜像站前缀的引用改名回用户输入的名字:<c>wslc tag</c> 上新名字,再 <c>wslc rmi</c> 去掉旧名字
    /// (功能 010 · AC5;实测多标签时 rmi 只取消标记,镜像数据不动,见功能 004 的实测表)。
    /// Tagged 表示新名字已经打上(界面据此用用户写的名字报成功);Message 非空时是要展示的那句补充说明。
    /// </summary>
    private async Task<(bool Tagged, string? Message)> RenameAfterMirrorAsync(
        string effective,
        string typed,
        CancellationToken cancellationToken)
    {
        try
        {
            await _service.TagAsync(effective, typed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, Fmt("images.mirror.retag-failed", effective, WslcError.Describe(ex)));
        }

        try
        {
            await _service.RemoveAsync(effective, force: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (true, Fmt("images.mirror.untag-failed", typed, WslcError.Describe(ex)));
        }

        return (true, null);
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

        Begin(item, Fmt("images.deleting", reference), pulling: false);
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
                Notice = Fmt("images.deleted", reference);
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
        // 功能 011:上一次拉取的进度行不能留在这一页上,所以开始即清空。
        _pullProgress = null;
        _lastPushedLines = Array.Empty<string>();
        // 台账只属于这一次拉取:留着上一次的数字,下一趟的"已下载"就会从半路开始。
        // 清空与读线程的累计共用一把锁,免得上一趟的最后一帧正好插进来。
        lock (_pullLayerBytes)
        {
            _pullLayerBytes.Clear();
        }
        PullProgressText = "";
        HasPullProgress = false;
        OnPropertyChanged(nameof(PullProgressText));
        OnPropertyChanged(nameof(PullSummary));
        OnPropertyChanged(nameof(HasPullSummary));
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
        // 功能 011:进度面板随操作一起收起,剩下的是结果提示和新列表(AC4)。
        _pullProgress = null;   // 汇总数字也属于"这一次拉取",留着迟早会串到下一次
        HasPullProgress = false;
        PullProgressText = "";
        OnPropertyChanged(nameof(PullProgressText));
        OnPropertyChanged(nameof(PullSummary));
        OnPropertyChanged(nameof(HasPullSummary));
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

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.Services;
using WslcDesktop.Core.WslcCli;
using static WslcDesktop.Core.I18n.Loc;

namespace WslcDesktop.Core.ViewModels;

/// <summary>
/// "日志"页 ViewModel(功能 005)。
/// 职责:选容器 → 选行数/时间戳/是否跟随 → 一次性读取或持续跟随;
/// 后台读线程的行先进缓冲,由界面攒批上屏(AC12),并且整个页面同时只允许一个 wslc 进程(AC9)。
/// </summary>
public sealed partial class LogsPageViewModel : ObservableObject
{
    /// <summary>界面最多保留多少行,再多就丢最早的(AC13)。</summary>
    public const int MaxDisplayedLines = 2_000;

    /// <summary>缓冲攒够这么多行就催一次上屏,避免每行刷一遍整块文本(AC12)。</summary>
    public const int BatchThreshold = 64;

    /// <summary>暂停滚动时缓冲的上限,超出就丢最旧的行(保护内存,不追求精确)。</summary>
    public const int MaxPendingLines = 20_000;

    /// <summary>没选容器时点"加载日志"的提示。</summary>
    public static string NoSourceHint => T("logs.no-source");

    private readonly ContainersService _containers;
    private readonly LogsService _logs;
    private readonly IUIThread? _uiThread;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly List<string> _displayed = new();
    private readonly Dictionary<string, LogSourceOptionViewModel> _knownSources = new(StringComparer.Ordinal);
    private List<ContainerInfo> _all = new();
    private int _operationActive;
    private volatile bool _refreshingSources;
    private CancellationTokenSource? _followCts;
    private int _dropped;
    private bool _suppressAutoLoad;

    public LogsPageViewModel(ContainersService containers, LogsService logs, IUIThread? uiThread = null)
    {
        _containers = containers ?? throw new ArgumentNullException(nameof(containers));
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        _uiThread = uiThread;

        LoadCommand = new AsyncRelayCommand(() => LoadAsync(), () => CanLoad);
        StopFollowCommand = new RelayCommand(CancelFollow, () => IsFollowing);
        RefreshSourcesCommand = new AsyncRelayCommand(() => LoadSourcesAsync(preserveSelection: true), () => !IsBusy);

        TailOptions = LogQuery.TailChoices
            .Select(value => new TailOption(value, LogQuery.DescribeTail(value)))
            .ToList();
        _selectedTail = TailOptions.First(option => option.Value == LogQuery.DefaultTailLines);

        _ = LoadSourcesAsync(preserveSelection: false); // AC15:启动即把容器灌进下拉
    }

    /// <summary>下拉里的一项行数(0 表示"全部",此时不发 -n)。</summary>
    public sealed record TailOption(int Value, string Label);

    public ObservableCollection<LogSourceOptionViewModel> Sources { get; } = new();

    public IReadOnlyList<TailOption> TailOptions { get; }

    public IAsyncRelayCommand LoadCommand { get; }

    public IRelayCommand StopFollowCommand { get; }

    public IAsyncRelayCommand RefreshSourcesCommand { get; }

    [ObservableProperty] private LogSourceOptionViewModel? _selectedSource;
    [ObservableProperty] private TailOption _selectedTail;
    [ObservableProperty] private bool _timestamps;
    [ObservableProperty] private bool _followRequested;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _isFollowing;
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private int _lineCount;
    [ObservableProperty] private int _droppedLineCount;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string _lastLoadedText = T("logs.not-loaded");

    /// <summary>读取失败时展示的错误条(AC6)。</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>空日志/取消/跟随结束这类正常提示(AC10/AC14)。</summary>
    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    /// <summary>是否有日志进程在跑(读取或跟随),期间所有选项都不许改(AC9)。</summary>
    public bool IsBusy => Volatile.Read(ref _operationActive) != 0;

    /// <summary>"加载日志"按钮可用性:有选中容器且当前不忙(AC9)。</summary>
    public bool CanLoad => !IsBusy && SelectedSource is not null;

    /// <summary>容器/行数/时间戳/跟随四个选项是否可改(AC9)。</summary>
    public bool CanChangeOptions => !IsBusy;

    /// <summary>只有运行中的容器允许跟随(AC11 · 实测停止的容器 -f 会立刻结束且毫无提示)。</summary>
    public bool CanFollow => SelectedSource?.IsRunning == true;

    /// <summary>缓冲区里等待上屏的行数(测试用同一口径验证攒批逻辑)。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>状态栏文案(AC13/AC16)。</summary>
    public string StatusText
    {
        get
        {
            var parts = new List<string>();
            parts.Add(Fmt("logs.lines", LineCount));
            if (DroppedLineCount > 0) parts.Add(Fmt("logs.dropped", DroppedLineCount));
            parts.Add(Fmt("logs.last-loaded", LastLoadedText));
            if (IsFollowing) parts.Add(T("logs.following"));
            else if (IsBusy) parts.Add(T("logs.reading"));
            else if (!AutoScroll && PendingCount > 0) parts.Add(Fmt("logs.paused-buffer", PendingCount));
            return string.Join(" · ", parts);
        }
    }

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));

    partial void OnLineCountChanged(int value) => OnPropertyChanged(nameof(StatusText));

    partial void OnDroppedLineCountChanged(int value) => OnPropertyChanged(nameof(StatusText));

    partial void OnLastLoadedTextChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnIsFollowingChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        StopFollowCommand.NotifyCanExecuteChanged();
    }

    partial void OnAutoScrollChanged(bool value)
    {
        // 重新打开自动滚动 → 把暂停期间攒下的行一次性补上(AC16)。
        if (value) DrainPending();
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnSelectedTailChanged(TailOption value) => OnPropertyChanged(nameof(SelectedTailLabel));

    partial void OnSelectedSourceChanged(LogSourceOptionViewModel? value)
    {
        RaiseOptionStates();
        if (value is not null && !value.IsRunning && FollowRequested)
        {
            FollowRequested = false; // AC11:切到非运行中的容器,跟随自动取消
        }

        if (_suppressAutoLoad) return;
        _ = LoadAsync(); // 选完容器就能看到日志,不用再点一次
    }

    /// <summary>当前行数选项文案,供状态栏/界面复用。</summary>
    public string SelectedTailLabel => SelectedTail.Label;

    private void RaiseOptionStates()
    {
        OnPropertyChanged(nameof(CanLoad));
        OnPropertyChanged(nameof(CanChangeOptions));
        OnPropertyChanged(nameof(CanFollow));
        OnPropertyChanged(nameof(StatusText));
        LoadCommand.NotifyCanExecuteChanged();
        RefreshSourcesCommand.NotifyCanExecuteChanged();
    }
    /// <summary>
    /// 加载日志(AC1/AC9/AC10/AC11/AC14)。是否跟随由界面勾选**和**容器状态共同决定:
    /// 实测对已停止的容器发 -f 只会立刻拿到空结果,所以这里坚持不发,免得用户以为按钮坏了。
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var source = SelectedSource;
        if (source is null)
        {
            Notice = NoSourceHint;
            return;
        }

        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0) return; // AC9

        var follow = FollowRequested && source.IsRunning;
        var query = new LogQuery
        {
            ContainerRef = source.Reference,
            TailLines = SelectedTail.Value,
            Timestamps = Timestamps,
            Follow = follow,
        };

        var cts = follow ? new CancellationTokenSource() : null;
        _followCts = cts;
        var token = cts is null ? cancellationToken : cts.Token;

        ClearDisplay();
        IsFollowing = follow;
        OnPropertyChanged(nameof(IsBusy));
        RaiseOptionStates();

        try
        {
            if (follow) await RunFollowAsync(source, query, token).ConfigureAwait(false);
            else await RunReadAsync(source, query, token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _operationActive, 0);
            await RunOnUiAsync(() =>
            {
                if (cts is not null)
                {
                    ForceDrain(); // 收尾:没攒够一批的行也要上屏
                    cts.Dispose();
                    _followCts = null;
                }

                IsFollowing = false;
                OnPropertyChanged(nameof(IsBusy));
                RaiseOptionStates();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>停止正在进行的跟随(AC8)。没有跟随在跑时什么都不做。</summary>
    public void CancelFollow()
    {
        var cts = _followCts;
        if (cts is null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 跟随刚好结束,忽略。
        }
    }

    private async Task RunReadAsync(LogSourceOptionViewModel source, LogQuery query, CancellationToken token)
    {
        LogSnapshot snapshot;
        try
        {
            snapshot = await _logs.ReadAsync(query, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // 一次性读取没有取消入口,真被取消就当什么都没发生
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() => Error = WslcError.Describe(ex)).ConfigureAwait(false); // AC6
            return;
        }

        await RunOnUiAsync(() =>
        {
            AppendToDisplay(snapshot.Lines); // 一次性读取即使暂停滚动也要把结果显示出来(AC16)
            LastLoadedText = NowText();
            if (snapshot.IsEmpty) Notice = EmptyNoticeFor(source); // AC10:空输出不是错误
        }).ConfigureAwait(false);
    }

    private async Task RunFollowAsync(LogSourceOptionViewModel source, LogQuery query, CancellationToken token)
    {
        Exception? failure = null;
        var cancelled = false;

        try
        {
            await _logs.FollowAsync(query, EnqueueLine, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await RunOnUiAsync(() =>
        {
            ForceDrain();
            LastLoadedText = NowText();
            if (cancelled)
            {
                Notice = Fmt("logs.follow-stopped", LineCount); // AC8:已显示的日志保留
            }
            else if (failure is not null)
            {
                Error = WslcError.Describe(failure);
            }
            else if (LineCount == 0)
            {
                Notice = Fmt("logs.follow-empty", source.DisplayName); // AC10 + AC14
            }
            else
            {
                Notice = Fmt("logs.follow-ended", LineCount); // AC14
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 后台读线程调用:只往线程安全队列里塞行,不碰任何界面状态(AC12)。
    /// 攒够 <see cref="BatchThreshold"/> 行才催一次上屏。
    /// </summary>
    public void EnqueueLine(string line)
    {
        if (line is null) return;
        _pending.Enqueue(line);

        while (_pending.Count > MaxPendingLines && _pending.TryDequeue(out _))
        {
            Interlocked.Add(ref _dropped, 1); // AC13:暂停滚动期间也要有上限
        }

        if (_pending.Count >= BatchThreshold) ScheduleDrain();
    }

    /// <summary>把缓冲的行一次性上屏;关掉自动滚动时保持视图冻结(AC16)。</summary>
    public IReadOnlyList<string> DrainPending() => AutoScroll ? ForceDrain() : Array.Empty<string>();

    private IReadOnlyList<string> ForceDrain()
    {
        if (_pending.IsEmpty) return Array.Empty<string>();

        var moved = new List<string>();
        while (_pending.TryDequeue(out var line)) moved.Add(line);
        AppendToDisplay(moved);
        return moved;
    }

    private void AppendToDisplay(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;

        _displayed.AddRange(lines);
        var overflow = _displayed.Count - MaxDisplayedLines;
        if (overflow > 0)
        {
            _displayed.RemoveRange(0, overflow);
            Interlocked.Add(ref _dropped, overflow);
        }

        DroppedLineCount = Volatile.Read(ref _dropped);
        LineCount = _displayed.Count;
        LogText = WslcLogText.Join(_displayed);

        // 跟随期间"最近加载"要跟着新行走:真机上发现它一直停在跟随开始前的时刻,
        // 状态栏看起来就像"实时跟随没生效"。一次性读取(IsFollowing=false)不受影响。
        if (IsFollowing) LastLoadedText = NowText();
    }

    private void ScheduleDrain()
    {
        if (_uiThread is null) return; // 单元测试里由测试自己调 DrainPending,不依赖时钟
        _uiThread.Post(() => DrainPending());
    }

    private void ClearDisplay()
    {
        _displayed.Clear();
        while (_pending.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _dropped, 0);
        LogText = "";
        LineCount = 0;
        DroppedLineCount = 0;
        Error = null;
        Notice = null; // AC8 的约定:新操作开始时把旧提示一并清掉
    }

    /// <summary>刷新容器下拉(AC15)。忙碌时跳过;失败只报错,已有选项保持不动。</summary>
    public async Task LoadSourcesAsync(bool preserveSelection, CancellationToken cancellationToken = default)
    {
        if (IsBusy || _refreshingSources) return; // AC9
        _refreshingSources = true;
        try
        {
            IReadOnlyList<ContainerInfo> loaded;
            try
            {
                loaded = await _containers.ListAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() => Error = WslcError.Describe(ex)).ConfigureAwait(false);
                return;
            }

            await RunOnUiAsync(() => ApplySources(loaded, preserveSelection)).ConfigureAwait(false);
        }
        finally
        {
            _refreshingSources = false;
        }
    }

    private void ApplySources(IReadOnlyList<ContainerInfo> loaded, bool preserveSelection)
    {
        var before = SelectedSource;
        _all = loaded
            .OrderBy(c => c.IsRunning ? 0 : 1)
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var wanted = new List<LogSourceOptionViewModel>();
        foreach (var container in _all)
        {
            if (!_knownSources.TryGetValue(container.Id, out var option))
            {
                option = new LogSourceOptionViewModel(container);
                _knownSources[container.Id] = option;
            }
            else
            {
                option.UpdateModel(container); // AC15:按 Id 复用对象,选择才不会丢
            }
            wanted.Add(option);
        }

        foreach (var stale in _knownSources.Keys.Where(id => !_all.Any(c => c.Id == id)).ToList())
        {
            _knownSources.Remove(stale);
        }

        var keep = preserveSelection && before is not null && wanted.Contains(before) ? before : null;
        if (keep is not null && !keep.IsRunning && ReferenceEquals(keep, SelectedSource) && FollowRequested)
        {
            FollowRequested = false; // AC11:刷新后发现容器停了,跟随自动取消
        }

        _suppressAutoLoad = true;
        if (!SameSequence(Sources, wanted))
        {
            Sources.Clear();
            foreach (var option in wanted) Sources.Add(option);
        }

        SelectedSource = keep ?? wanted.FirstOrDefault(o => o.IsRunning) ?? wanted.FirstOrDefault();
        _suppressAutoLoad = false;

        if (SelectedSource is null)
        {
            ClearDisplay();
            Notice = T("logs.no-containers");
        }
        else if (!ReferenceEquals(SelectedSource, before))
        {
            _ = LoadAsync(); // 启动即加载 / 换了容器 → 自动读一次日志
        }

        RaiseOptionStates();
    }

    private static string EmptyNoticeFor(LogSourceOptionViewModel source) =>
        Fmt("logs.empty-output", source.DisplayName);

    private static string NowText() => DateTimeOffset.Now.ToString("HH:mm:ss");

    private static bool SameSequence(IReadOnlyList<LogSourceOptionViewModel> current, IReadOnlyList<LogSourceOptionViewModel> wanted)
    {
        if (current.Count != wanted.Count) return false;
        for (var i = 0; i < current.Count; i++)
        {
            if (!ReferenceEquals(current[i], wanted[i])) return false;
        }
        return true;
    }

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

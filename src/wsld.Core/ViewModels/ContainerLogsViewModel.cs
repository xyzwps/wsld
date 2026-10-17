using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using wsld.Core.Models;
using wsld.Core.Parsing;
using wsld.Core.Services;
using wsld.Core.WslcCli;
using static wsld.Core.I18n.Loc;

namespace wsld.Core.ViewModels;

/// <summary>界面「行数」下拉的一项:值 + 文案("最近 200 行" / "全部")。</summary>
public sealed record LogTailOption(int Value, string Label);

/// <summary>
/// 容器详情页「日志」页签(功能 005)。两种读法各一个入口,互不掺混:
/// <see cref="ReadCommand"/> 查看日志 = 一次性读当前能看到的,读完就结束;
/// <see cref="FollowCommand"/> 实时日志 = 从点下去这一刻起只收新行,**不加载历史**
/// (实测 <c>logs -f</c> 不带 <c>--since</c> 会先把全部历史冲出来,所以起点是这条模式的硬要求)。
/// 页面上"选哪个容器"由详情区决定,这里只接 <see cref="ShowContainer"/>。
/// </summary>
public sealed partial class ContainerLogsViewModel : ObservableObject
{
    /// <summary>攒够这么多行就催一次上屏,免得每来一行就重绘整块文本。</summary>
    public const int BatchThresholdLines = 64;

    /// <summary>界面最多保留多少行;超出就丢最早的并计数(实时挂着跑一夜不至于吃满内存)。</summary>
    public const int MaxDisplayedLines = 2_000;

    private readonly LogsService _logs;
    private readonly IUIThread? _uiThread;

    /// <summary>已上屏的行,只在 UI 线程(_uiThread 为 null 时即测试线程)上读写。</summary>
    private readonly List<string> _displayed = new();

    /// <summary>后台读线程 → UI 线程的行缓冲。</summary>
    private readonly ConcurrentQueue<string> _pending = new();

    private int _dropped;
    private int _operationActive;
    private DateTimeOffset _lastLoadedAt;
    private CancellationTokenSource? _followCts;

    public ContainerLogsViewModel(LogsService logs, IUIThread? uiThread = null)
    {
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        _uiThread = uiThread;
        TailOptions = LogQuery.TailChoices
            .Select(value => new LogTailOption(value, LogQuery.DescribeTail(value)))
            .ToList();
        _selectedTail = TailOptions.First(option => option.Value == LogQuery.DefaultTailLines);
    }

    [ObservableProperty] private LogTailOption _selectedTail;
    [ObservableProperty] private bool _timestamps;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private string _containerRef = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _notice = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isFollowing;
    [ObservableProperty] private bool _canFollow;

    /// <summary>读取与实时都要求"当前没有进行中的日志进程";实时还要求容器在跑。</summary>
    public bool CanRead => !IsBusy;

    public bool CanChangeOptions => !IsBusy;

    public bool CanStop => IsFollowing;

    /// <summary>实时按钮的悬停说明:能不能点,原因直接写在文案里,免得灰按钮让人猜。</summary>
    public string FollowTooltip => CanFollow ? T("logs.live-tooltip") : T("logs.live.only-running");

    public IReadOnlyList<LogTailOption> TailOptions { get; }

    public AsyncRelayCommand ReadCommand { get; private set; } = null!;
    public AsyncRelayCommand FollowCommand { get; private set; } = null!;
    public AsyncRelayCommand StopFollowCommand { get; private set; } = null!;

    /// <summary>文本变化后页面据此把视图拉到底(勾选了自动滚动才拉)。在 UI 线程上触发。</summary>
    public event EventHandler? LogTextChangedUi;

    public void InitializeCommands()
    {
        ReadCommand = new AsyncRelayCommand(ReadAsync, () => CanRead);
        FollowCommand = new AsyncRelayCommand(FollowAsync, () => CanFollow);
        StopFollowCommand = new AsyncRelayCommand(StopFollowAsync, () => CanStop);
    }

    /// <summary>
    /// 详情页换容器:停掉正在跑的实时日志,清空已显示的行与状态。
    /// 两种模式的缓冲都属于"这一个容器这一次会话",不跟着用户跑到别的容器上。
    /// </summary>
    public void ShowContainer(string containerRef, bool isRunning)
    {
        CancelFollowCore();
        _pending.Clear();
        _displayed.Clear();
        _dropped = 0;
        _lastLoadedAt = default;
        ContainerRef = containerRef;
        CanFollow = isRunning;
        LogText = "";
        Notice = "";
        StatusText = "";
        RaiseStates();
    }

    /// <summary>
    /// 页签被切走 / 页面关掉时调用:只停实时日志,已显示的行留着(回来还能看)。
    /// </summary>
    public void StopFollowing() => CancelFollowCore();

    /// <summary>容器起停同步过来:只有运行中的容器才收得到新行。</summary>
    public void UpdateRunning(bool isRunning)
    {
        if (CanFollow == isRunning) return;
        CanFollow = isRunning;
        if (!isRunning) CancelFollowCore();
        RaiseStates();
    }

    private async Task ReadAsync()
    {
        if (!TryBeginOperation()) return;
        await RunOnUiAsync(() =>
        {
            IsBusy = true;
            Notice = "";
            RaiseStates();
        }).ConfigureAwait(false);

        try
        {
            var query = LogQuery.View(ContainerRef, SelectedTail.Value, Timestamps);
            var snapshot = await _logs.ReadAsync(query).ConfigureAwait(false);
            await RunOnUiAsync(() =>
            {
                _displayed.Clear();
                _dropped = 0;
                Append(snapshot.Lines);
                Notice = snapshot.IsEmpty ? T("logs.notice.empty") : "";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await RunOnUiAsync(() => Notice = WslcError.Describe(ex)).ConfigureAwait(false);
        }
        finally
        {
            await EndOperationAsync().ConfigureAwait(false);
        }
    }

    private async Task FollowAsync()
    {
        if (!TryBeginOperation()) return;

        var cts = new CancellationTokenSource();
        _followCts = cts;
        await RunOnUiAsync(() =>
        {
            // 新一轮实时从空白开始:上一趟「查看日志」读到的历史不该混进来(两个视图各自独立)。
            // 而"停止实时"不清屏 —— 已经看到的行留着(实测取消跟随不会在容器里留下任何东西)。
            _pending.Clear();
            _displayed.Clear();
            _dropped = 0;
            LogText = "";
            StatusText = "";
            IsBusy = true;
            IsFollowing = true;
            Notice = "";
            RaiseStates();
        }).ConfigureAwait(false);

        var received = 0;
        var cancelled = false;
        string? failure = null;
        try
        {
            // 起点取"点下去这一刻":实测 --since 会连 -f 开头那次历史冲刷一起过滤掉,
            // 所以实时日志天然只看得到此刻之后的行,要看之前的请用「查看日志」。
            var query = LogQuery.Live(ContainerRef, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Timestamps);
            await _logs.FollowAsync(
                query,
                line =>
                {
                    Interlocked.Increment(ref received);
                    EnqueueLine(line);
                },
                cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = WslcError.Describe(ex);
        }
        finally
        {
            await RunOnUiAsync(() =>
            {
                DrainPending();
                IsFollowing = false;
                Notice = EndedText(cancelled, failure, received);
            }).ConfigureAwait(false);
            await EndOperationAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 收尾文案分四种:用户停的 / 读失败的 / 容器到结束都没吐一行 / 正常收到过行。
    /// 实测容器被停止时 wslc 自己以退出码 0 退出,不会留一句"容器已结束",这句话得界面自己说。
    /// </summary>
    private string EndedText(bool cancelled, string? failure, int received)
    {
        if (failure is not null) return failure;
        if (cancelled) return T("logs.notice.stopped");
        return received == 0 ? T("logs.notice.ended-empty") : T("logs.notice.ended");
    }

    private async Task StopFollowAsync()
    {
        await RunOnUiAsync(CancelFollowCore).ConfigureAwait(false);
    }

    private void CancelFollowCore()
    {
        try
        {
            _followCts?.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// 后台读线程调用:只往线程安全队列里塞行,攒够一批才催一次上屏。
    /// 低速率日志(实测每秒一行的探针容器)靠页面的催屏定时器兜住,不指望批阈值。
    /// </summary>
    public void EnqueueLine(string line)
    {
        _pending.Enqueue(line);
        if (_pending.Count >= BatchThresholdLines) ScheduleDrain();
    }

    /// <summary>把缓冲里的行一次性合并到界面文本;只有一行也要能上屏。</summary>
    public void DrainPending()
    {
        if (_pending.IsEmpty) return;

        var batch = new List<string>();
        while (_pending.TryDequeue(out var line)) batch.Add(line);
        Append(batch);
    }

    /// <summary>追加行 + 裁剪到上限 + 刷新状态栏,都在 UI 线程上。</summary>
    private void Append(IEnumerable<string> lines)
    {
        var added = 0;
        foreach (var line in lines)
        {
            _displayed.Add(line);
            added++;
        }

        if (added == 0) return;

        if (_displayed.Count > MaxDisplayedLines)
        {
            var excess = _displayed.Count - MaxDisplayedLines;
            _displayed.RemoveRange(0, excess);
            _dropped += excess;
        }

        _lastLoadedAt = DateTimeOffset.Now;
        LogText = WslcLogText.Join(_displayed);
        StatusText = BuildStatusText();
        LogTextChangedUi?.Invoke(this, EventArgs.Empty);
    }

    private string BuildStatusText()
    {
        var parts = new List<string> { Fmt("logs.status.count", _displayed.Count) };
        if (_lastLoadedAt != default)
        {
            parts.Add(Fmt("logs.status.last-loaded",
                _lastLoadedAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (_dropped > 0) parts.Add(Fmt("logs.status.dropped", _dropped));
        return string.Join(" · ", parts);
    }

    private bool TryBeginOperation() => Interlocked.CompareExchange(ref _operationActive, 1, 0) == 0;

    private Task EndOperationAsync() =>
        RunOnUiAsync(() =>
        {
            Interlocked.Exchange(ref _operationActive, 0);
            IsBusy = false;
            RaiseStates();
        });

    private void RaiseStates()
    {
        OnPropertyChanged(nameof(CanRead));
        OnPropertyChanged(nameof(CanChangeOptions));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(FollowTooltip));
        ReadCommand?.NotifyCanExecuteChanged();
        FollowCommand?.NotifyCanExecuteChanged();
        StopFollowCommand?.NotifyCanExecuteChanged();
    }

    private void ScheduleDrain()
    {
        if (_uiThread is null) return;
        _uiThread.Post(DrainPending);
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_uiThread is null) { action(); return Task.CompletedTask; }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiThread.Post(() => { try { action(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } });
        return tcs.Task;
    }
}

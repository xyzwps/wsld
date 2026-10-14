using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;
using WslcDesktop.Core.WslcCli;
using static WslcDesktop.Core.I18n.Loc;

namespace WslcDesktop.Core.ViewModels;

/// <summary>
/// "命令编排"页 ViewModel(功能 012)。
/// 职责:表单状态(RunSpec)→ 实时重拼命令 → 校验 → 复制或就地执行 → 回显输出;
/// 再把一条剪贴板里的 wslc run 读回表单。拼装的纯逻辑全部在 RunCommandBuilder,
/// 进程调用全部在 ContainersService,这一层只做编排与线程调度。
/// </summary>
public sealed partial class RunBuilderPageViewModel : ObservableObject
{
    private readonly ContainersService _containers;
    private readonly ImagesService _images;
    private readonly IUIThread? _uiThread;

    private readonly StringBuilder _outputBuffer = new();
    private readonly object _outputLock = new();

    private int _executionActive; // 0/1,Interlocked 保证"同时只有一条在跑"
    private CancellationTokenSource? _execCts;
    private bool _suspendRebuild;

    public RunBuilderPageViewModel(
        ContainersService containers,
        ImagesService images,
        IUIThread? uiThread = null,
        RunHistory? history = null)
    {
        _containers = containers;
        _images = images;
        _uiThread = uiThread;
        History = history ?? new RunHistory();
        History.Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));

        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => CanExecute);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        RefreshImagesCommand = new AsyncRelayCommand(RefreshImagesAsync);
        ClearFormCommand = new RelayCommand(ClearForm, () => !IsBusy);
        ClearHistoryCommand = new RelayCommand(History.Clear);

        AddPortCommand = new RelayCommand(() => Spec.Ports.Add(new PortRow()));
        AddVolumeCommand = new RelayCommand(() => Spec.Volumes.Add(new VolumeRow()));
        AddEnvCommand = new RelayCommand(() => Spec.EnvVars.Add(new EnvRow()));
        AddLabelCommand = new RelayCommand(() => Spec.Labels.Add(new LabelRow()));
        AddTmpfsCommand = new RelayCommand(() => Spec.TmpfsPaths.Add(new PathRow()));

        HookSpec(Spec);
        Rebuild();
    }

    /// <summary>表单的全部状态;页面直接绑定它的属性,改动即时反映到生成的命令。</summary>
    public RunSpec Spec { get; } = new();

    /// <summary>就地执行历史(功能 012 补强);实例由外部注入,切语言重建页面时历史跟着续上。</summary>
    public RunHistory History { get; }

    /// <summary>历史卡片显示列表还是空态提示。</summary>
    public bool HasHistory => History.Entries.Count > 0;

    /// <summary>本机镜像引用,给镜像下拉框当候选;拉不到不报错(它只是便利,不是功能本身)。</summary>
    public ObservableCollection<string> ImageReferences { get; } = new();

    public IAsyncRelayCommand ExecuteCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand RefreshImagesCommand { get; }
    public IRelayCommand ClearFormCommand { get; }
    public IRelayCommand ClearHistoryCommand { get; }

    public IRelayCommand AddPortCommand { get; }
    public IRelayCommand AddVolumeCommand { get; }
    public IRelayCommand AddEnvCommand { get; }
    public IRelayCommand AddLabelCommand { get; }
    public IRelayCommand AddTmpfsCommand { get; }

    // ---- 派生状态 ----

    [ObservableProperty] private string _generatedCommand = "";
    [ObservableProperty] private bool _hasValidationIssues;
    [ObservableProperty] private string _validationText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _notice = "";
    [ObservableProperty] private bool _hasNotice;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _outputText = "";

    /// <summary>可以执行:表单有效且没有别的执行在跑。</summary>
    public bool CanExecute => !IsBusy && !HasValidationIssues;

    /// <summary>可以复制:同一条有效性判断 —— 复制出去的也必须是能跑的命令。</summary>
    public bool CanCopy => !HasValidationIssues;

    public bool CanImport => !IsBusy;

    /// <summary>
    /// 拉取策略在下拉框里的序号:0 = 默认(不下发 --pull),1 = always,2 = missing,3 = never。
    /// 认不得的值在解析阶段就被赶去其他参数了,所以这四个选项永远和真正生成的命令一致。
    /// </summary>
    public int PullPolicyIndex
    {
        get
        {
            var index = System.Array.IndexOf(PullPolicyOptions, Spec.PullPolicy);
            return index >= 0 ? index : 0;
        }
        set
        {
            Spec.PullPolicy = value >= 1 && value < PullPolicyOptions.Length ? PullPolicyOptions[value] : "";
        }
    }

    private static readonly string[] PullPolicyOptions = { "", "always", "missing", "never" };

    // ---- 表单 → 命令(实时) ----

    private void Rebuild()
    {
        if (_suspendRebuild) return;

        var issues = RunCommandBuilder.Validate(Spec);
        HasValidationIssues = issues.Count > 0;
        ValidationText = string.Join("\r\n", issues.Select(DescribeIssue));
        GeneratedCommand = RunCommandBuilder.Display(Spec);
        OnPropertyChanged(nameof(PullPolicyIndex));

        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanCopy));
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>机器问题码 → 人话(带行号);文案键见 StringTables 的 run.error.*。</summary>
    private static string DescribeIssue(RunIssue issue) => issue.Code switch
    {
        RunIssueCode.ImageMissing => T("run.error.image-missing"),
        RunIssueCode.PortContainerMissing => Fmt("run.error.port-container", Row(issue.Row)),
        RunIssueCode.PortNumber => Fmt("run.error.port-number", Row(issue.Row), Text(issue.Value)),
        RunIssueCode.PortRange => Fmt("run.error.port-range", Row(issue.Row)),
        RunIssueCode.PortProtocol => Fmt("run.error.port-protocol", Row(issue.Row)),
        RunIssueCode.VolumeSource => Fmt("run.error.volume-source", Row(issue.Row)),
        RunIssueCode.VolumeTarget => Fmt("run.error.volume-target", Row(issue.Row)),
        RunIssueCode.TmpfsPath => Fmt("run.error.tmpfs", Row(issue.Row)),
        RunIssueCode.EnvKey => Fmt("run.error.env-key", Row(issue.Row)),
        RunIssueCode.LabelKey => Fmt("run.error.label-key", Row(issue.Row)),
        RunIssueCode.HealthRetries => T("run.error.health-retries"),
        RunIssueCode.Cpus => T("run.error.cpus"),
        _ => T("run.error.image-missing"),
    };

    /// <summary>带行号的问题才有值;空值补空串,免得 nullable 警告污染文案。</summary>
    private static string Row(int? row) => (row ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Text(string? value) => value ?? "";

    private void HookSpec(RunSpec spec)
    {
        spec.PropertyChanged += (_, _) => Rebuild();
        HookRows(spec.Ports);
        HookRows(spec.Volumes);
        HookRows(spec.EnvVars);
        HookRows(spec.Labels);
        HookRows(spec.TmpfsPaths);
    }

    /// <summary>行集合:增删都接线,删掉的老行立刻摘监听,不留悬挂订阅。</summary>
    private void HookRows<T>(ObservableCollection<T> rows) where T : INotifyPropertyChanged
    {
        rows.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null)
                foreach (INotifyPropertyChanged item in e.OldItems) item.PropertyChanged -= OnRowChanged;
            if (e.NewItems is not null)
                foreach (INotifyPropertyChanged item in e.NewItems) item.PropertyChanged += OnRowChanged;
            Rebuild();
        };
        foreach (var row in rows) row.PropertyChanged += OnRowChanged;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Rebuild();

    // ---- 就地执行 ----

    private async Task ExecuteAsync()
    {
        if (Interlocked.CompareExchange(ref _executionActive, 1, 0) != 0) return;

        // 按下执行就记录,不问成败:用户要的正是"跑过的那条",失败了更要能找回。
        History.Record(GeneratedCommand);

        var cts = new CancellationTokenSource();
        _execCts = cts;

        Error = null;
        HasError = false;
        Notice = "";
        HasNotice = false;
        lock (_outputLock) _outputBuffer.Clear();
        OutputText = "";
        await RunOnUiAsync(() => SetBusy(true));

        try
        {
            var result = await _containers.RunAsync(Spec, OnStdoutLine, cts.Token);

            // 输出兜底:真机走逐行回调;一次性冲刷时这里补齐,面板不该是空的
            if (result.StdOut.Trim().Length > 0)
            {
                lock (_outputLock)
                {
                    if (_outputBuffer.Length == 0) _outputBuffer.Append(result.StdOut);
                }
            }

            if (result.ExitCode == 0)
            {
                var id = FirstNonEmptyLine(result.StdOut) ?? Spec.Image.Trim();
                Notice = string.IsNullOrWhiteSpace(id)
                    ? T("run.executed-noname")
                    : Fmt("run.executed", id);
                HasNotice = true;
            }
            else
            {
                HasError = true;
                Error = WslcError.Describe(new WslcCliException("wslc run", result));
            }
        }
        catch (OperationCanceledException)
        {
            HasNotice = true;
            Notice = T("run.execution-cancelled");
        }
        catch (Exception ex)
        {
            HasError = true;
            Error = WslcError.Describe(ex);
        }
        finally
        {
            Volatile.Write(ref _executionActive, 0);
            _execCts = null;
            SafeDispose(cts);
            await RunOnUiAsync(() =>
            {
                FlushOutputToUi();
                SetBusy(false);
            });
        }
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanCopy));
        ExecuteCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ClearFormCommand.NotifyCanExecuteChanged();
    }

    private void Cancel() => _execCts?.Cancel();

    /// <summary>逐行回调在后台读线程上:先攒缓冲,再调度回 UI 刷文本。</summary>
    private void OnStdoutLine(string line)
    {
        lock (_outputLock)
        {
            _outputBuffer.Append(line).Append('\n');
        }
        var ui = _uiThread;
        if (ui is null) FlushOutputToUi();
        else ui.Post(FlushOutputToUi);
    }

    private void FlushOutputToUi()
    {
        string snapshot;
        lock (_outputLock) snapshot = _outputBuffer.ToString();
        OutputText = snapshot.Replace("\r\n", "\n").Replace('\n', '\r');
    }

    private static string? FirstNonEmptyLine(string text) =>
        text.Split('\n').FirstOrDefault(line => line.Trim().Length > 0)?.Trim();

    // ---- 复制 / 粘贴读回 ----

    /// <summary>剪贴板操作发生在 UI 页,成功与否由这里落成提示。</summary>
    public void MarkCopied()
    {
        Notice = T("run.copied");
        HasNotice = true;
    }

    public void MarkCopyFailed()
    {
        HasError = true;
        Error = T("run.copy-failed");
    }

    /// <summary>把一条 wslc run 命令读回表单;空剪贴板/读不懂各给各的提示。</summary>
    public void ImportCommandText(string? clipboardText)
    {
        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            HasNotice = true;
            Notice = T("run.import.empty");
            return;
        }

        if (!RunCommandBuilder.TryParse(clipboardText, out var parsed) || parsed is null)
        {
            HasError = true;
            Error = T("run.import.failed");
            return;
        }

        ApplyParsed(parsed);
        HasNotice = true;
        Notice = T("run.imported");
    }

    /// <summary>逐属性搬运而不是换掉 Spec 实例:页面的绑定与监听都挂在现有对象上。</summary>
    private void ApplyParsed(RunSpec parsed)
    {
        _suspendRebuild = true;
        try
        {
            Spec.Image = parsed.Image;
            Spec.Name = parsed.Name;
            Spec.Detach = parsed.Detach;
            Spec.Rm = parsed.Rm;
            Spec.PublishAll = parsed.PublishAll;
            Spec.Interactive = parsed.Interactive;
            Spec.Tty = parsed.Tty;
            Spec.PullPolicy = parsed.PullPolicy;
            Spec.Entrypoint = parsed.Entrypoint;
            Spec.Command = parsed.Command;
            Spec.Arguments = parsed.Arguments;
            Spec.Hostname = parsed.Hostname;
            Spec.Network = parsed.Network;
            Spec.Cpus = parsed.Cpus;
            Spec.Memory = parsed.Memory;
            Spec.ShmSize = parsed.ShmSize;
            Spec.Workdir = parsed.Workdir;
            Spec.User = parsed.User;
            Spec.EnvFile = parsed.EnvFile;
            Spec.HealthCmd = parsed.HealthCmd;
            Spec.HealthInterval = parsed.HealthInterval;
            Spec.HealthTimeout = parsed.HealthTimeout;
            Spec.HealthRetries = parsed.HealthRetries;
            Spec.HealthStartPeriod = parsed.HealthStartPeriod;
            Spec.NoHealthcheck = parsed.NoHealthcheck;
            Spec.ExtraArgs = parsed.ExtraArgs;

            ReplaceRows(Spec.Ports, parsed.Ports);
            ReplaceRows(Spec.Volumes, parsed.Volumes);
            ReplaceRows(Spec.EnvVars, parsed.EnvVars);
            ReplaceRows(Spec.Labels, parsed.Labels);
            ReplaceRows(Spec.TmpfsPaths, parsed.TmpfsPaths);
        }
        finally
        {
            _suspendRebuild = false;
        }

        Rebuild();
    }

    private static void ReplaceRows<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var row in source) target.Add(row);
    }

    // ---- 清空 / 历史恢复 ----

    /// <summary>一键清空:表单回到出厂状态(后台运行仍勾着),输出与提示也一并抹掉。</summary>
    private void ClearForm()
    {
        ApplyParsed(new RunSpec());
        Error = null;
        HasError = false;
        Notice = "";
        HasNotice = false;
        lock (_outputLock) _outputBuffer.Clear();
        OutputText = "";
    }

    /// <summary>把一条历史命令填回表单继续改;走的是与剪贴板读回同一条解析路径。</summary>
    public void RestoreFromHistory(RunHistoryEntry? entry)
    {
        if (entry is null) return;

        if (IsBusy)
        {
            HasNotice = true;
            Notice = T("run.restore.busy");
            return;
        }

        if (!RunCommandBuilder.TryParse(entry.Command, out var parsed) || parsed is null)
        {
            HasError = true;
            Error = T("run.import.failed");
            return;
        }

        ApplyParsed(parsed);
        HasNotice = true;
        Notice = T("run.restored");
    }

    // ---- 镜像候选 ----

    public async Task RefreshImagesAsync()
    {
        try
        {
            var images = await _images.ListAsync().ConfigureAwait(false);
            var references = images
                .Where(image => image.IsTagged)
                .Select(image => image.Reference)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            await RunOnUiAsync(() =>
            {
                ImageReferences.Clear();
                foreach (var reference in references) ImageReferences.Add(reference);
            });
        }
        catch
        {
            // 候选列表是便利功能:拉不到就空着,不影响手动输入。
        }
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
}
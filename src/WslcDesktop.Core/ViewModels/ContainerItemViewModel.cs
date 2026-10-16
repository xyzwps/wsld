using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.ViewModels;

/// <summary>
/// 列表中的一行:容器数据 + 该行的操作状态与四个命令(功能 002)。
/// 之所以要包一层,是因为刷新时容器对象会被整体替换,
/// 而"正在操作中"这种界面状态必须活过刷新(AC11)。
/// </summary>
public sealed partial class ContainerItemViewModel : ObservableObject
{
    private readonly ContainersPageViewModel _owner;
    private readonly AsyncRelayCommand _startCommand;
    private readonly AsyncRelayCommand _stopCommand;
    private readonly AsyncRelayCommand _restartCommand;
    private readonly AsyncRelayCommand _removeCommand;

    public ContainerItemViewModel(ContainersPageViewModel owner, ContainerInfo model)
    {
        _owner = owner;
        Model = model;

        _startCommand = new AsyncRelayCommand(() => ExecuteAsync(ContainerAction.Start), () => CanStart);
        _stopCommand = new AsyncRelayCommand(() => ExecuteAsync(ContainerAction.Stop), () => CanStop);
        _restartCommand = new AsyncRelayCommand(() => ExecuteAsync(ContainerAction.Restart), () => CanRestart);
        _removeCommand = new AsyncRelayCommand(RemoveAsync, () => CanRemove);
    }

    /// <summary>底层数据;每次列表刷新都会被替换成新解析出来的对象。</summary>
    public ContainerInfo Model { get; private set; }

    /// <summary>本行正在执行操作(AC5)。</summary>
    [ObservableProperty]
    private bool _isBusy;

    public string Id => Model.Id;
    public string DisplayName => Model.DisplayName;
    public string ImageLine => Model.ImageLine;
    public bool IsRunning => Model.IsRunning;
    public string Status => Model.Status;

    /// <summary>运行中时隐藏"启动"按钮(AC9)。</summary>
    public bool CanStart => !_owner.IsBusy && !IsRunning;

    /// <summary>已停止时隐藏"停止"按钮(AC9)。</summary>
    public bool CanStop => !_owner.IsBusy && IsRunning;

    /// <summary>重启对已停止的容器同样有效,因此始终可用(AC9)。</summary>
    public bool CanRestart => !_owner.IsBusy;

    /// <summary>删除始终可用;运行中的由界面层确认后转成强制删除(AC9/AC10)。</summary>
    public bool CanRemove => !_owner.IsBusy;

    public IAsyncRelayCommand StartCommand => _startCommand;
    public IAsyncRelayCommand StopCommand => _stopCommand;
    public IAsyncRelayCommand RestartCommand => _restartCommand;
    public IAsyncRelayCommand RemoveCommand => _removeCommand;

    /// <summary>界面层需要"运行中的容器删除前要额外确认",故单独暴露入口。</summary>
    private Task RemoveAsync() => _owner.ExecuteAsync(this, ContainerAction.Remove, force: Model.IsRunning);

    private Task ExecuteAsync(ContainerAction action) => _owner.ExecuteAsync(this, action);

    /// <summary>刷新时复用行对象:换掉数据并通知界面(AC11)。</summary>
    public void UpdateModel(ContainerInfo model)
    {
        var wasRunning = IsRunning;
        Model = model;
        if (wasRunning != model.IsRunning)
        {
            IsBusy = false;
        }
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ImageLine));
        OnPropertyChanged(nameof(Status));
        RaiseActionStates();
    }

    /// <summary>页面级忙碌状态变化后,重新评估每一行的按钮可用性。</summary>
    public void RaiseActionStates()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(CanRemove));
        _startCommand.NotifyCanExecuteChanged();
        _stopCommand.NotifyCanExecuteChanged();
        _restartCommand.NotifyCanExecuteChanged();
        _removeCommand.NotifyCanExecuteChanged();
    }
}
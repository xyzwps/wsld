using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using wsld.Core.Models;
using wsld.Core.Services;

namespace wsld.Core.ViewModels;

/// <summary>
/// 列表中的一行:容器数据 + 该行的忙碌状态与"启动"命令(功能 002)。
/// 之所以要包一层,是因为刷新时容器对象会被整体替换,
/// 而"正在操作中"这种界面状态必须活过刷新(AC11)。
/// 停止 / 重启 / 删除只在右侧详情里给:一行四个图标按钮把名字挤得没地方,
/// 而点这一行本来就会显示它的详情。
/// </summary>
public sealed partial class ContainerItemViewModel : ObservableObject
{
    private readonly ContainersPageViewModel _owner;
    private readonly AsyncRelayCommand _startCommand;

    public ContainerItemViewModel(ContainersPageViewModel owner, ContainerInfo model)
    {
        _owner = owner;
        Model = model;

        _startCommand = new AsyncRelayCommand(() => ExecuteAsync(ContainerAction.Start), () => CanStart);
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

    /// <summary>运行中不给"启动"按钮(AC9)。</summary>
    public bool CanStart => !_owner.IsBusy && !IsRunning;

    public IAsyncRelayCommand StartCommand => _startCommand;

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
        _startCommand.NotifyCanExecuteChanged();
    }
}
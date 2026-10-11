using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.ViewModels;

/// <summary>
/// 镜像列表里的一行:镜像数据 + 这一行的操作状态(功能 004)。
/// 做法与功能 002 的容器行一致 —— 刷新时数据对象会被整体替换,
/// 但"正在删除""等待强制删除"这类界面状态必须活过刷新(AC12),所以包一层。
/// </summary>
public sealed partial class ImageItemViewModel : ObservableObject
{
    private readonly ImagesPageViewModel _owner;
    private readonly AsyncRelayCommand _removeCommand;

    public ImageItemViewModel(ImagesPageViewModel owner, ImageInfo model)
    {
        _owner = owner;
        Model = model;
        _removeCommand = new AsyncRelayCommand(RemoveAsync, () => CanRemove);
    }

    /// <summary>底层数据,每次列表刷新都会被换成新解析出来的对象。</summary>
    public ImageInfo Model { get; private set; }

    /// <summary>这一行正在执行操作(AC5)。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// 常规删除刚被"镜像正在使用"拒绝,等待用户第二次显式确认(AC10)。
    /// 为 true 时按钮文案变成"强制删除",按下才会发送 `wslc rmi -f`。
    /// </summary>
    [ObservableProperty]
    private bool _forcePending;

    public string Reference => Model.Reference;
    public string DisplayName => Model.DisplayName;
    public string SizeText => Model.SizeText;
    public string UsageText => Model.UsageText;
    public string SubtleLine => Model.SubtleLine;
    public bool IsInUse => Model.IsInUse;
    public int ContainerCount => Model.ContainerCount;

    /// <summary>页面级忙碌时所有行的按钮都不可用(AC5)。</summary>
    public bool CanRemove => !_owner.IsBusy;

    /// <summary>按钮文案:被占用拒绝过一次之后才升级为"强制删除"(AC10)。</summary>
    public string RemoveLabel => ForcePending ? "强制删除" : "删除";

    public IAsyncRelayCommand RemoveCommand => _removeCommand;

    partial void OnForcePendingChanged(bool value)
    {
        OnPropertyChanged(nameof(RemoveLabel));
    }

    /// <summary>由界面层的确认对话框触发;是否强制由这一行自己的状态决定(AC11)。</summary>
    private Task RemoveAsync() => _owner.ExecuteRemoveAsync(this, ForcePending);

    /// <summary>刷新时复用行对象:换掉数据并通知界面(AC12)。</summary>
    public void UpdateModel(ImageInfo model)
    {
        Model = model;
        if (!model.IsInUse)
        {
            // 占用状态已经消失,没必要再保留"强制删除"这个破坏性入口。
            ForcePending = false;
        }
        OnPropertyChanged(nameof(Reference));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(SubtleLine));
        OnPropertyChanged(nameof(IsInUse));
        OnPropertyChanged(nameof(ContainerCount));
        RaiseActionStates();
    }

    /// <summary>页面级忙碌状态变化后,重新评估这一行的按钮可用性。</summary>
    public void RaiseActionStates()
    {
        OnPropertyChanged(nameof(CanRemove));
        _removeCommand.NotifyCanExecuteChanged();
    }
}
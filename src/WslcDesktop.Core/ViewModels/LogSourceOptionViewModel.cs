using CommunityToolkit.Mvvm.ComponentModel;
using WslcDesktop.Core.Models;
using static WslcDesktop.Core.I18n.Loc;

namespace WslcDesktop.Core.ViewModels;

/// <summary>
/// "日志"页顶部容器下拉里的一项(功能 005 · AC15)。
/// 和容器页/镜像页的行对象一样,按 Id 复用,刷新时只换数据,
/// 这样界面选中项不会因为刷新而丢失(AC15),也不会触发多余的重复加载。
/// </summary>
public sealed class LogSourceOptionViewModel : ObservableObject
{
    private ContainerInfo _model;

    public LogSourceOptionViewModel(ContainerInfo model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <summary>容器长 ID —— 发给 wslc 的就是它(实测名称也行,但没命名的容器只有 ID 可靠)。</summary>
    public string Reference => _model.Id;

    /// <summary>界面显示用的名称。</summary>
    public string DisplayName => _model.DisplayName;

    /// <summary>短 ID。</summary>
    public string ShortId => _model.ShortId;

    public bool IsRunning => _model.IsRunning;

    public string StateText => T(IsRunning ? "state.running" : "state.stopped");

    /// <summary>下拉项文案:名称 · 短ID · 状态(AC15)。</summary>
    public string Label => $"{DisplayName} · {ShortId} · {StateText}";

    /// <summary>刷新时用新的容器数据替换(同一 Id)。</summary>
    public void UpdateModel(ContainerInfo model)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        if (!string.Equals(model.Id, _model.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("只能用同一个容器的数据更新行对象。", nameof(model));
        }

        _model = model;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ShortId));
    }
}
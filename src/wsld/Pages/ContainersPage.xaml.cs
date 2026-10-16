using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using wsld.Core;
using wsld.Core.I18n;
using wsld.Core.ViewModels;
using wsld.Services;
using static wsld.Core.I18n.Loc;

namespace wsld.Pages;

/// <summary>
/// 容器列表页(左栏) + 容器详情页(右栏)分栏布局。
/// 功能 001:列表/搜索/自动刷新;功能 002:行内生命周期操作;容器详情:点列表项(选中即查看)。
/// </summary>
public sealed partial class ContainersPage : Page, INotifyPropertyChanged
{
    public ContainersPageViewModel ViewModel { get; }
    public ContainerDetailViewModel? DetailViewModel { get; private set; }

    /// <summary>右栏详情面板是否可见。BoolToVis 只认真 bool(见其类注释),
    /// 直接把 DetailViewModel 对象丢给它永远得到 false,右栏就再也出不来,
    /// 所以这里单独开一个 HasDetail 供四处 Visibility 使用。</summary>
    public bool HasDetail => DetailViewModel is not null;

    public bool DetailHasError => DetailViewModel?.HasError ?? false;
    public string? DetailError => DetailViewModel?.Error;

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly DispatcherUiThread _uiThread;
    private bool _restoringSelection;

    public ContainersPage()
    {
        _uiThread = new DispatcherUiThread(DispatcherQueue);
        ViewModel = new ContainersPageViewModel(AppServices.Containers, _uiThread);

        DataContext = Localizer.Default;
        InitializeComponent();

        ViewModel.Items.CollectionChanged += (_, _) =>
        {
            UpdateEmptyHint();
            RestoreSelection();
        };
        UpdateEmptyHint();

        Loaded += (_, _) => ViewModel.StartAutoRefresh();
        Unloaded += (_, _) => ViewModel.StopAutoRefresh();
    }

    private async void DetailRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (DetailViewModel is null) return;

        var running = DetailViewModel.IsRunning;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("dialog.delete-container.title"),
            Content = BuildDeleteMessage(DetailViewModel.DisplayName, running),
            PrimaryButtonText = T("action.delete"),
            CloseButtonText = T("action.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await DetailViewModel.RemoveCommand.ExecuteAsync(null);
    }

    private static object BuildDeleteMessage(string name, bool running)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = Fmt("dialog.delete-container.body", name),
            TextWrapping = TextWrapping.Wrap,
        });
        if (running)
        {
            panel.Children.Add(new TextBlock
            {
                Text = T("dialog.delete-container.running"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)
                    Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorCriticalBrush"],
            });
        }
        return panel;
    }

    private void UpdateEmptyHint()
    {
        EmptyHint.Visibility = ViewModel.Items.Count == 0 && !ViewModel.HasError
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>选中项变化 = 用户在列表里点了一行(也可能是按方向键挪了焦点)。</summary>
    private void ContainerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 自动刷新后 RestoreSelection 把选中项放回去,那次不算"用户点击",别跟着动滚动条。
        if (_restoringSelection) return;

        if (e.AddedItems.Count > 0)
        {
            // 滚到"这次刚被选中的那一项":ScrollIntoView 收数据项本身。
            // 别拿 AddedItems 的下标去索引 Items(那是"新增列表里的第几个",单选恒为 0 = 第一行);
            // 也别用 SelectedItem —— 它可能还是 RestoreSelection 刚放回去的旧那一行。
            ContainerList.ScrollIntoView(e.AddedItems[0]);
        }

        if (ContainerList.SelectedItem is ContainerItemViewModel item) ShowDetail(item.Id);
    }

    /// <summary>
    /// Items 被整批重建时(容器状态变了会重新排序、搜索词变了会过滤),ListView 把选中项清成 null,
    /// 可右栏还在显示这个容器 —— 高亮跟着详情走,别让两边各说各话。
    /// </summary>
    private void RestoreSelection()
    {
        var id = DetailViewModel?.ContainerId;
        if (string.IsNullOrEmpty(id)) return;

        var match = ViewModel.Items.FirstOrDefault(item => item.Id == id);
        if (match is null || ReferenceEquals(ContainerList.SelectedItem, match)) return;

        _restoringSelection = true;
        ContainerList.SelectedItem = match;
        _restoringSelection = false;
    }

    /// <summary>换容器时旧详情对象的三个事件要先摘掉,否则每点一次就漏一个订阅在页面上。</summary>
    private void DetachDetail()
    {
        if (DetailViewModel is null) return;
        DetailViewModel.Removed -= OnDetailRemoved;
        DetailViewModel.RefreshRequested -= OnDetailRefreshRequested;
        DetailViewModel.PropertyChanged -= OnDetailPropertyChanged;
    }

    private void OnDetailPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ContainerDetailViewModel.HasError) or nameof(ContainerDetailViewModel.Error))
        {
            OnPropertyChanged(nameof(DetailHasError));
            OnPropertyChanged(nameof(DetailError));
        }
    }

    private void ShowDetail(string containerId)
    {
        // 重复点同一行不重建:右栏会白闪一下,自动刷新还可能把选中项弄丢。
        if (DetailViewModel?.ContainerId == containerId) return;

        DetachDetail();
        DetailViewModel = new ContainerDetailViewModel(AppServices.Containers, _uiThread);
        DetailViewModel.InitializeCommands(_ => Task.CompletedTask);
        DetailViewModel.Removed += OnDetailRemoved;
        DetailViewModel.RefreshRequested += OnDetailRefreshRequested;
        DetailViewModel.PropertyChanged += OnDetailPropertyChanged;
        OnPropertyChanged(nameof(DetailViewModel));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailHasError));
        OnPropertyChanged(nameof(DetailError));

        _ = DetailViewModel.LoadAsync(containerId);
    }

    private void OnDetailRemoved(object? sender, EventArgs e)
    {
        DetachDetail();
        DetailViewModel = null;
        OnPropertyChanged(nameof(DetailViewModel));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailHasError));
        OnPropertyChanged(nameof(DetailError));
        _ = ViewModel.RefreshAsync(CancellationToken.None);
    }

    private void OnDetailRefreshRequested(object? sender, EventArgs e)
    {
        if (DetailViewModel is not null)
        {
            _ = DetailViewModel.LoadAsync(DetailViewModel.ContainerId);
        }
        _ = ViewModel.RefreshAsync(CancellationToken.None);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class DispatcherUiThread(DispatcherQueue queue) : IUIThread
    {
        public void Post(Action action) => queue.TryEnqueue(() => action());
    }
}

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using wsld.Core;
using wsld.Core.I18n;
using wsld.Core.Models;
using wsld.Core.Services;
using wsld.Core.ViewModels;
using wsld.Services;
using wsld.Ui;

namespace wsld.Pages;

/// <summary>
/// 命令编排页(功能 012):把 wslc run 的表单拼成一条命令,复制走或就地跑。
/// 页面只管交互:表单状态在 RunSpec,拼装/校验/读回在 RunCommandBuilder,进程在 ContainersService。
/// 删行、复制、读回剪贴板这些必须碰界面的动作留在这一层,Core 不碰剪贴板。
/// </summary>
public sealed partial class RunBuilderPage : Page
{
    private bool _syncingImageText;

    public RunBuilderPageViewModel ViewModel { get; }

    /// <summary>
    /// 拉取策略的候选项,必须是纯字符串,不能写成内联 ComboBoxItem:
    /// 那种写法里 Content 是延后求值的绑定,ComboBox 初始化时抄进选框的是空内容,
    /// 序号明明选中了 0,框里却什么都不显示(实测,dev-notes §39)。
    /// 切换语言时主窗口会重建页面,所以这里在构造时取一次文案就够。
    /// </summary>
    public IReadOnlyList<string> PullPolicyOptions { get; } =
        new[] { Loc.T("run.pull.default"), "always", "missing", "never" };

    public RunBuilderPage()
    {
        ViewModel = new RunBuilderPageViewModel(
            AppServices.Containers,
            AppServices.Images,
            new DispatcherUiThread(DispatcherQueue),
            AppServices.RunHistory);

        // 静态文案的绑定源:页面 DataContext 指向 Localizer(功能 009 的约定)。
        DataContext = Localizer.Default;

        InitializeComponent();

        // 输出是新写入的一帧,视图要停在最后一行才看得见正在动的东西(dev-notes §19 的同一套道理)。
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RunBuilderPageViewModel.OutputText))
            {
                DispatcherQueue.TryEnqueue(() => TextBoxScroller.ScrollToBottom(OutputBox));
            }
        };

        // 镜像候选只是便利输入,拉不到就空着,不影响手打引用。
        Loaded += (_, _) => _ = ViewModel.RefreshImagesAsync();
    }

    /// <summary>
    /// 下拉框的 Text 默认在失焦/选中时才提交,而这条命令要边改边长出来,
    /// 所以这里把敲进去的字实时推给 Spec;值相同就不回写,免得和 x:Bind 互相绕圈。
    /// </summary>
    private void ImageBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_syncingImageText) return;

        var text = sender.Text ?? "";
        if (ViewModel.Spec.Image == text) return;

        _syncingImageText = true;
        try
        {
            ViewModel.Spec.Image = text;
        }
        finally
        {
            _syncingImageText = false;
        }
    }

    /// <summary>删行走 Click:行对象在模板的 DataContext 上,集合归属由按钮自己的 Tag 说明。</summary>
    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: not null } element) return;

        switch (element.Tag as string)
        {
            case "port" when element.DataContext is PortRow port:
                ViewModel.Spec.Ports.Remove(port);
                break;
            case "volume" when element.DataContext is VolumeRow volume:
                ViewModel.Spec.Volumes.Remove(volume);
                break;
            case "env" when element.DataContext is EnvRow env:
                ViewModel.Spec.EnvVars.Remove(env);
                break;
            case "label" when element.DataContext is LabelRow label:
                ViewModel.Spec.Labels.Remove(label);
                break;
            case "tmpfs" when element.DataContext is PathRow path:
                ViewModel.Spec.TmpfsPaths.Remove(path);
                break;
        }
    }

    /// <summary>复制的是界面上那一条,也是真正会执行的那一条:同源的,不存在预览与执行两副面孔。</summary>
    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = ViewModel.GeneratedCommand;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);

            // Flush 是把内容真正交出去:本进程之后退出,剪贴板里那条命令仍然在。
            // 别的程序正开着剪贴板时它会抛 CLIPBRD_E_CANT_OPEN,这种占用转瞬即逝,
            // 所以重试几次;仍然不行就回读确认,别把已经成功的复制报成失败。
            var flushed = false;
            for (var attempt = 1; attempt <= 4 && !flushed; attempt++)
            {
                try
                {
                    Clipboard.Flush();
                    flushed = true;
                }
                catch (Exception ex)
                {
                    App.Log($"Clipboard.Flush attempt {attempt}: {ex.GetType().Name} 0x{ex.HResult:x8} {ex.Message}");
                    await Task.Delay(50 * attempt);
                }
            }

            if (flushed || Clipboard.GetContent().Contains(StandardDataFormats.Text))
            {
                ViewModel.MarkCopied();
            }
            else
            {
                ViewModel.MarkCopyFailed();
            }
        }
        catch (Exception ex)
        {
            App.Log("复制到剪贴板失败: " + ex);
            ViewModel.MarkCopyFailed();
        }
    }

    /// <summary>把一条历史命令填回表单(功能 012 补强);行对象挂在模板的 DataContext 上。</summary>
    private void RestoreFromHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RunHistoryEntry entry })
        {
            ViewModel.RestoreFromHistory(entry);
        }
    }

    /// <summary>把剪贴板里的 wslc run 读回表单,继续在这里改(AC8)。</summary>
    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        string? text = null;
        try
        {
            var view = Clipboard.GetContent();
            if (view.Contains(StandardDataFormats.Text))
            {
                text = await view.GetTextAsync();
            }
        }
        catch (Exception ex)
        {
            App.Log("读取剪贴板失败: " + ex);
        }

        ViewModel.ImportCommandText(text);
    }

    /// <summary>把后台线程上的状态更新调度回 UI 线程。</summary>
    private sealed class DispatcherUiThread(DispatcherQueue queue) : IUIThread
    {
        public void Post(Action action) => queue.TryEnqueue(() => action());
    }
}

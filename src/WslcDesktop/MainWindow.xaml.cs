using Microsoft.UI.Xaml;
using WslcDesktop.Pages;

namespace WslcDesktop;

/// <summary>
/// 主窗口只负责承载页面:容器管理 UI 在 <see cref="ContainersPage"/> 中。
/// (WinUI 编译限制:x:Bind 的转换器模板无法在 Window 根上生成代码,因此使用 Page 承载。)
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "WSLC Desktop · 容器管理";
        RootHost.Children.Add(new ContainersPage());
    }
}
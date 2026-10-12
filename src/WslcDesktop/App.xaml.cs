using System.IO;
using Microsoft.UI.Xaml;
using WslcDesktop.Converters;
using WslcDesktop.Core.I18n;
using WslcDesktop.Pages;
using WslcDesktop.Services;

namespace WslcDesktop;

/// <summary>
/// 应用入口。
/// 注意:转换器必须在 <see cref="OnLaunched"/> 中注册,而不是在构造函数中。
/// 在构造函数里写入 Application.Resources 会让 Microsoft.UI.Xaml.dll 抛出
/// 原生异常(0xC000027B),进程在任何托管代码执行前就退出。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public static string LogPath { get; } = ResolveLogPath();

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            Log("UnhandledException: " + e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain: " + e.ExceptionObject);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("OnLaunched");

        // 在代码中注册转换器,避免 XAML 直接引用当前程序集类型。
        Resources["BoolToVis"] = new BoolToVisibilityConverter();
        Resources["LocText"] = new LocalizationConverter();

        // 功能 009:读设置(主题 + 语言)并落到 Localizer,窗口照着它长出来。
        AppState.Initialize();

        _window = new MainWindow();
        _window.Activate();
        Log("window activated");
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响主流程。
        }
    }

    private static string ResolveLogPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WslcDesktop");
        return Path.Combine(dir, "startup.log");
    }
}
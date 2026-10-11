# 开发备忘

只记录会反复踩到、且网上很难查到的坑。

## 1. WinUI 3:绝对不要在 `App` 构造函数里写 `Resources[...]`

**症状**:进程启动后立刻退出,退出码 `0xC000041B` / `0xC000041D`;Windows 事件日志显示
出错模块为 `Microsoft.UI.Xaml.dll`,异常代码 `0xc000027b`(stowed exception),WER 中
`combase.dll` + `E_UNEXPECTED`。C# 侧一行日志都写不出来 —— 崩溃发生在托管代码之前,
`App()` 里注册的 `UnhandledException` 根本没有机会生效。

**根因**:`Application.Resources` 依赖的 XAML 运行时在 `App` 构造期间尚未初始化完成,
赋值会直接抛原生异常。

**修复**:把值注册移到 `OnLaunched` 中、创建窗口之前:

```csharp
protected override void OnLaunched(LaunchActivatedEventArgs args)
{
    Resources["BoolToVis"] = new BoolToVisibilityConverter(); // 正确位置
    _window = new MainWindow();
    _window.Activate();
}
```

**定位方法**(可复用):用同样 csproj 属性建一个只有 `App.xaml` + 空 `MainWindow` 的最小
探针工程,确认配置本身可用后,再往探针里逐行加代码。每加一次都要 `dotnet build` 并检查
`$LASTEXITCODE`,然后启动进程等 5 秒看是否存活。注意:如果上一个实例还没退出,
`dotnet build` 会因文件占用而失败,从而误判为"这一行导致崩溃"。

## 2. `x:Bind` + 转换器的模板要放在 `Page` 里,不能放在 `Window` 根上

放在 `Window` 根上时 MarkupCompilePass2 生成的代码会调用
`SetConverterLookupRoot(this)`,而该重载要求 `FrameworkElement`,报 `CS1503`。
因此 `MainWindow` 只保留一个空的 `<Grid x:Name="RootHost"/>`,页面在 code-behind 里塞进去。

## 3. 本机只能用 dotnet CLI 构建

没有安装 Visual Studio(`vswhere` 返回空),所以不要指望 `msbuild` / VS 的 XAML 工具链。
构建与测试命令:

```powershell
dotnet build src\WslcDesktop\WslcDesktop.csproj -v q --nologo
dotnet test  tests\WslcDesktop.Tests\WslcDesktop.Tests.csproj --nologo -v q
```

可执行文件输出在
`src\WslcDesktop\bin\Debug\net9.0-windows10.0.19041.0\win-x64\WslcDesktop.exe`
(注意不是 `bin\x64\Debug`)。

## 4. 版本组合

`Microsoft.WindowsAppSDK` 固定 2.5.1,`Microsoft.Windows.SDK.BuildTools` 固定
10.0.28000.2705,`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`。
本机已安装 Microsoft.WindowsAppRuntime.2 2.5.1.0,该组合已验证可正常启动。
改版本号需要联网 restore,非必要不动。

## 5. 调整窗口尺寸:`Window.AppWindow` 可用,但需要 `using Windows.Graphics;`

`AppWindow.Move(PointInt32)` / `AppWindow.Resize(SizeInt32)` 就在 `Window.AppWindow` 上,
不需要 `Win32Interop` 也不需要 `WindowId.FromHandle` —— 那两个 API 确实解析不到,
但根本用不上。`PointInt32` / `SizeInt32` 位于 `Windows.Graphics` 命名空间
(来自 `Microsoft.Windows.SDK.NET.dll`),漏掉这个 using 会报"未能找到类型"。

```csharp
using Microsoft.UI.Windowing;
using Windows.Graphics;   // 关键:PointInt32 / SizeInt32 在这里

var appWindow = AppWindow;
appWindow.Resize(new SizeInt32(1180, 780));   // 物理像素
appWindow.Move(new PointInt32(80, 60));
```

不设置的话 WinUI 3 会按系统默认给一个超出屏幕右侧的窗口,界面右边的按钮全在屏幕外。

## 6. `ProgressRing` 没有 `IsRing` 属性

WindowsAppSDK 2.5.1 的 `ProgressRing` 上不存在 `IsRing`,写进 XAML 会报
`WMC0011: Unknown member 'IsRing'`。直接用默认环形样式即可。

## 7. 截图工具必须先 `SetProcessDPIAware`

`tools/capture-window.ps1` 用 `GetWindowRect` + `CopyFromScreen` 抓窗口。
如果 PowerShell 进程不是 DPI 感知的,`GetWindowRect` 返回的是**缩放后**的坐标
(150% 下 1180 变成 787),而 `CopyFromScreen` 用的是物理像素,结果只会截到窗口左上角一块。
已在脚本里调用 `[Win32Cap]::SetProcessDPIAware()` 修正。

## 8. 二分定位崩溃时,一定要检查 `$LASTEXITCODE`

应用进程还活着的时候 `dotnet build` 会因为 exe 被占用而失败
(`MSB3027/MSB3021`),`-v q` 下很容易被忽略。
于是"改了 A 就崩溃"的结论可能其实是"编译失败,跑的还是上一个坏二进制"。
每轮之间先 `Get-Process WslcDesktop | Stop-Process -Force`,再构建并确认退出码为 0。
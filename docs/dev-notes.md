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
## 9. System.Text.Json 的自定义转换器**不会**被 null 调用

**症状**:`{"Containers":null}` 之后 `public string Containers { get; set; } = ""` 变成真正的 null,
任何 `.Trim()` 都抛 `NullReferenceException`。单元测试里加了 null 用例才暴露出来。

**根因**:遇到 JSON `null` 时,STJ 直接给属性赋 `default(T)`(引用类型即 null),**不会**调用
`JsonConverter<T>.Read`,所以转换器里写的 `JsonTokenType.Null => ""` 是死代码。

**修复**:在 setter 里归一化,别让 null 进对象:

```csharp
private string _containers = "";
public string Containers { get => _containers; set => _containers = value ?? ""; }
```

## 10. `x:Bind` + `Mode=OneWay` 绑到不实现 INPC 的模型会报 WMC1506

镜像行直接绑 `ImageInfo`(纯数据,没有通知),MarkupCompile 报
`WMC1506: OneWay bindings require at least one of their steps to support raising notifications`。
列表刷新是整体替换 ItemsSource,行内字段根本不需要通知 —— 改成 `Mode=OneTime` 就 0 警告了。
(功能 004 正是这么做的:引入实现 INPC 的 `ImageItemViewModel` 之后,行内绑定改回 `Mode=OneWay`,依然 0 警告。)

## 11. 用 UI Automation 验证"点了导航项真的换页了"

不用装任何自动化工具,pwsh 自带的能力就够:

```powershell
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$win  = $root.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition(
          [System.Windows.Automation.AutomationElement]::NameProperty, 'WSLC Desktop')))
$item = $win.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition(
          [System.Windows.Automation.AutomationElement]::NameProperty, '镜像')))
# 关键:NavigationViewItem 不支持 InvokePattern("不支持的模式"),要用 SelectionItemPattern
($item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
```

## 12. UI Automation 读 WinUI 3 元素的 `Current.Enabled` 永远是 null

**症状**:自动化脚本里 `if (-not $btn.Current.Enabled) { throw "disabled" }` 无条件抛错,
看起来像"所有按钮都被禁用了",但界面上明明是亮的。

**根因**:`UIAutomationClient`(WPF 那套托管封装)拿不到 WinUI 3 provider 的 `Enabled` 属性,
返回默认值;在 PowerShell 里是 `$null`,而 `-not $null` 恒为真。

**修复**:不要用 `Current.Enabled` 判断可用性。要么直接 `InvokePattern.Invoke()`(真禁用会抛异常),
要么读 `LegacyIAccessiblePattern` 的 State,要么用截图核对。

## 13. `CopyFromScreen` 会抓到 WinUI 的撕裂帧

**症状**:前后两秒的两张截图,一张是 4 行 + "共 4 个镜像",另一张是 1 行 + "共 1 个镜像",
而 `wslc images` 自始至终都是 4 个 —— 界面上半截和下半截来自不同的渲染帧。

**根因**:WinUI 3 由 DirectComposition 合成,`CopyFromScreen` 抓的是屏幕缓冲,
可能正好落在组合更新的中间。

**修复**:截图只用来看效果;判断状态一律再用 UIA 读一次文本复核
(比如 `AutomationId=Message` 那个元素的 Name)。要抓"进行中"的画面就多抓几张挑。

## 14. `DataTemplate` 根元素要显式给 `AutomationProperties.Name`

**症状**:UIA 树里每一行的 Name 都是 `WslcDesktop.Core.ViewModels.ImageItemViewModel`,
读屏软件念出来全是类名。

**根因**:ListItem 自身没有可读文本时,Name 退化成数据对象的 `ToString()`。

**修复**:模板根元素绑一个真正可读的字段:

```xml
<StackPanel AutomationProperties.Name="{x:Bind DisplayName, Mode=OneWay}">
```

## 15. Button 同时绑 `Command` 和 `IsEnabled` 时,命令会改写 IsEnabled

WinUI 的 `ButtonBase` 监听 `ICommand.CanExecuteChanged`,并把结果**直接写进 `IsEnabled` 本地值**,
这会盖掉 `IsEnabled="{x:Bind ...}"`。所以操作结束时一定要有人调 `NotifyCanExecuteChanged()`,
否则按钮会卡在禁用态 —— 功能 004 的 `RaiseBusyState()` 就是专门干这个的。

## 16. ContentDialog 的按钮有固定 AutomationId

`PrimaryButton` / `SecondaryButton` / `CloseButton`,自动化脚本可以直接按 ID 点,不必按文案找。
另外对话框打开时,UIA 树里会多出一个 Name 等于对话框标题的 `ControlType.Window` 元素,
可以用它判断"框真的弹出来了"。
## 17. 取消跟随:`wslc` 被终止后会以退出码 -1 自己退场

**症状**:点"停止跟随"有时弹红色错误条,而不是预期的"已停止跟随 · 共 N 行"。

**根因**:`Kill(entireProcessTree: true)` 和 `WaitForExit()` 在赛跑。被终止的 `wslc`
经常先带着退出码 `-1`(它自己的"我被终止了")退场,`ProcessWslcCli` 于是把这次调用
判成"命令失败"抛 `WslcCliException`,而取消信号是之后才被看到的。

**修复**:在 `RunAsync` 返回前、`RunLinesAsync` 的 `WaitForExit()` 之后各补一次
`cancellationToken.ThrowIfCancellationRequested()`;catch 里若令牌已经触发,
一律改抛 `OperationCanceledException`。**取消就是取消,不能报成失败。**

## 18. WinUI 3 没有 `WrapPanel`,工具栏防裁切只能靠"可伸缩列"

**症状**:日志页工具栏在 1180 宽窗口下,"自动滚动"和"停止跟随"被右边缘切掉。

**根因**:①WinUI 3(Windows App SDK 2.5)根本没有 `WrapPanel` / `WrapLayout`
(反射查 `Microsoft.WinUI.dll`,带 Wrap 的只有 `ItemsWrapGrid`/`VariableSizedWrapGrid`/`WrapGrid`);
②放在 `Auto` 列里的 `ComboBox` 会按**最长条目**算自己要多宽,把右边的按钮整组顶出可视区。

**修复**:定下布局约定 —— 动作按钮一律 `Auto` 列(永远保住自己的宽度),
会变长的下拉放 `*` 列 + `MaxWidth`(窄窗口先压缩它,文字截断就行)。
另外 `ToggleSwitch` 默认 `MinWidth=154`,开关两侧文案相同时直接设 0,一行能多塞两个开关。

## 19. 攒批上屏会把慢速日志"憋死"

**症状**:真机上勾"实时跟随",容器每秒打印 1 行,8 秒后界面还停在"200 行";
点停止才一次跳到 208 行。看起来完全像跟随没生效。

**根因**:`EnqueueLine` 只在缓冲攒够 64 行时才催一次上屏(防高频重绘),慢速容器永远攒不满阈值,
而"跟随结束"才是唯一的兜底上屏时机。

**修复**:页面在 `IsFollowing` 为真期间挂一个 500ms 的 `DispatcherQueueTimer` 催屏,
跟随结束或页面卸载立刻停,不空转。VM 侧契约用
`DrainPending_AppliesEvenASingleLine` 钉住:哪怕只有 1 行缓冲也要能上屏。

## 20. 跟随期间状态栏的"最近加载"停在旧时刻

**症状**:"206 行 · 最近加载 20:16:50 · 跟随中" —— 行数在涨、时间不动,
读起来像"20:16:50 之后就再没有新日志"。

**根因**:`LastLoadedText` 只在一次性读取完成、以及跟随结束时赋值。

**修复**:`AppendToDisplay` 里如果 `IsFollowing` 为真,顺带把它推到当下(一次性读取路径不受影响)。

## 21. 截图会被"焦点 ToolTip"以及它自己的顶层窗口污染

**症状**:①截图右上角莫名多出气泡"wslc logs(按下方选项拼参数)",把鼠标移开也不消失;
②`tools/capture-window.ps1` 输出 `captured 317x64`,截到的是个小窗口。

**根因**:①UIA 的 `InvokePattern.Invoke()` 会把键盘焦点留在按钮上,WinUI 随即弹出它的 ToolTip,
这是焦点触发的、不随鼠标移动消失;②ToolTip 是**同进程的独立顶层窗口**,
`EnumWindows` 遍历到的第一个窗口很可能就是它,而脚本用的是 `$found[0]`。

**修复**:截图前在日志区空白处点一下(把焦点从按钮上移走);
截图脚本改成按面积挑最大的窗口。

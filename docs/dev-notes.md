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

## 22. 带 BOM 的 `settings.json` 会被静默判成"设置被重置"

**症状**:用户在资源管理器里用记事本改过设置文件之后,应用"忘记"了所有设置 ——
不报错、不提示,只是下次启动又变回"跟随系统"。

**根因**:`SettingsStore.LoadFrom` 外面包了一层 `catch { return new AppSettings(); }`
(设计如此:设置读坏了不能把应用启动搞崩)。而记事本 / Visual Studio 很可能把文件存成
**带 BOM 的 UTF-8**,`System.Text.Json` 把 U+FEFF 当非法字符直接抛异常 ——
于是"一个字符的问题"被兜底 catch 变成"整份设置没了",而且完全看不出来。

**修复**:读取时 `File.ReadAllText(path).TrimStart('\uFEFF')`。
测试 `Utf8BomPrefixedFile_IsStillRead` 钉住这条。顺带提醒:凡是"读失败就退回默认值"的地方,
默认值本身要无害,而且最好只兜底**读**,不要顺手把写失败也一起吞成"成功"。

## 23. `ComboBox` 的 UIA 矩形把"下拉的占位"一起算进去了

**症状**:想确认"工具栏下拉到底要多宽",用 UIA 量 `ComboBox.Current.BoundingRectangle`,
固定 `Width="150"` 之后量出来还是 237×95 —— 看起来像"宽度没生效",于是得出
"它会按最长条目要 220+ 宽,把右边的开关挤出窗口"的错误结论。

**真相**:同一个控件,去掉固定宽度后 UIA 报 169×95(英文)/ 196×95(中文),
而截图上闭合的下拉框明显就是 150 DIP。也就是说 **UIA 那个矩形 = 控件本体 ∪ 下拉展开时的占位**,
高度 95 也远超"表头 + 32 高的框"。按它判断布局会一路判断错。

**约定**:量宽度要么看闭合状态的截图,要么量 `ContentPresenter` 那一层;
下拉固定宽度(`Width="150"`)的真正理由是**中英切换时工具栏宽度不跳**、
以及"最长条目一定放得下"(实测中文最长"最近 1000 行"、英文"1000 lines"),
不是"不固定就会溢出"。

另外两个连带结论:

- `string.Format(InvariantCulture, "{0}", 1000)` 出来是 `1000`,**不带千分位**(默认 G 格式),
  所以条目文案比预想的短,别按"1,000"去估宽度;
- 真要把一行塞满,`ToggleSwitch` 的默认 `MinWidth=154` 才是主要杀手(见 #18)。

## 24. `Platforms=x64` 会留下两份输出目录,自动化极易测到旧 exe

**症状**:改了 XAML、`dotnet build` 成功、截图却纹丝不动,像是"改动没生效"。

**根因**:项目里写了 `Platforms=x64`,**通过解决方案构建**时输出落在
`src\WslcDesktop\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\`,
而**直接构建单个项目 / 历史遗留**的 `bin\Debug\...` 也还在那儿放着上一次的 exe。
两份并存,`Get-ChildItem -Recurse -Filter WslcDesktop.exe` 会一次返回两个。

**修复**:脚本里一律 `Sort-Object LastWriteTime -Descending | Select-Object -First 1`,
并且把选中的路径和时间打印出来。更稳的做法是构建前先删掉旧输出,或者干脆只走解决方案构建。
另外:构建前必须先杀进程,否则文件被占用导致构建失败,而失败信息容易被误读成"代码有问题"。

## 25. 截窗口要用 DWM 扩展帧边界,而且还要再 inset 2px

**症状**:截图四边混进了一条背后的窗口内容(桌面 / 别的程序),看起来像"没截全"或"多了一圈"。

**根因**:`GetWindowRect` 返回的矩形包含 Win11 那圈**不可见边框**(阴影 + 调整大小的热区);
`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` 才是屏幕上真正看到的窗口范围 ——
但它外面**还包着约 2px 的不可见热区**,直接用同样会漏进来。

**修复**:`tools/win32-cap.cs` 导出 `DwmGetWindowAttribute` + `DWMWA_EXTENDED_FRAME_BOUNDS`,
`tools/capture-window.ps1` 优先用 DWM 边界,拿到后四边各 inset 2px,拿不到才退回 `GetWindowRect`。
配合 #21(按面积挑最大窗口 + 截图前把焦点从按钮上移开)才能稳定出干净截图。

## 26. `wslc` 3.0.1 根本没有镜像源开关,加速只能做在应用这一层

**症状**:想给镜像页加"registry mirror",第一反应是"写进 wslc 的配置就行"。

**真相**:`wslc` 没有这个开关。实测 `wslc settings` 生成/读取的
`%LOCALAPPDATA%\wslc\settings.yaml` 只有 `session.*`(cpuCount / memorySize / maxStorageSize /
storagePath / networkingMode / hostFileShareMode / dnsTunneling / hostLoopback /
defaultBindingAddress / idleTimeout)、`credentialStore`、`experimental.portRelay` 这些键;
`wslc registry` 只管登录凭据;`wslc pull` 只有 `-a` / `-q`;二进制里 `strings` 搜不到任何
mirror 相关的键名。也就是说 Docker 那个 `daemon.json → registry-mirrors` 在 wslc 上没有对应物。

**做法**:改写引用 —— 用户输入 `redis:7`,应用实际执行 `wslc pull docker.1ms.run/library/redis:7`,
成功后再 `wslc tag` 改回 `redis:7`、`wslc rmi` 去掉镜像站那个标签(实测多标签时 `rmi` 只 untag,镜像数据不动)。
规则按 Docker 的引用规范化对齐:裸名字属于官方库要补 `library/`,`bitnami/redis` 直接接在镜像站后面,
`docker.io/…` 三种等价写法都算 Hub;已经写全域名的(`ghcr.io/…`、`localhost:5000/…`)一律不碰。
逻辑全在 `RegistryMirrorPolicy`(Core,可单测),服务层不掺规则。

**顺带一个坑**:`wslc rmi <其中一个标签>` 的输出只有 `已取消标记:`,没有 `已删除:` ——
用它做"改名收尾"时千万别以为把镜像删了。反过来,改名顺序**必须**是先 `tag` 后 `rmi`,
写反了就是真删镜像。

## 27. 用 SendKeys 往 WinUI 输入框灌地址,会被中文 IME 吃掉

**症状**:自动化脚本里 `SendKeys::SendWait("https://Docker.1ms.run/")`,界面上文本框里的内容
面目全非,应用回"这一串不像注册地址",看着像规范化逻辑写错了。

**真相**:系统输入法是中文时,SendKeys 走的是键盘消息 → IME 处理,英文字母先进候选框,
`/`、`.` 这些也被 IME 吞掉,真正落到 `TextBox.Text` 的是别的字符。

**做法**:验证输入框一律用 UIA 的 `ValuePattern.SetValue()`(直接写文本,绕开输入法),
需要触发提交再单独发 `{ENTER}` / `{TAB}` 这类控制键。WinUI 的 `TextBox` 支持 ValuePattern;
它的 UIA `Name` 是 PlaceholderText,所以判断"框里到底是什么"要看 `Value` 而不是 `Name`。
另外 `NavigationView` 的导航项要用 `SelectionItemPattern.Select()`(`ListItemPattern` 这个类型
在 .NET 的 UIAutomation 里不存在,别照着旧名字写)。

## 28. ConPTY 的输入写端提前关闭 = 向子进程广播 Ctrl+C

**症状**:第一次给 `wslc pull` 挂伪终端,子进程一帧都没画就退场,退出码 `0xC000013A`
(STATUS_CONTROL_C_EXIT),屏幕上什么都没有,看着像"伪终端根本不工作"。

**真相**:`CreatePseudoConsole` 的输入管道有两个句柄(读端给 ConPTY,写端在我们手里)。
写完 STARTUPINFO 就把写端关掉看着很合理 —— 但 ConPTY 认为"所有输入写端都关了 = 终端已断开",
于是向挂在伪控制台上的**整个进程树**广播一次 Ctrl+C。`wslc` 收到就自杀,连第一帧都来不及输出。

**做法**:输入写端一路留着,只在 `Dispose` 里关。我们本来就不往 stdin 写东西,
子进程不会"卡在等输入"上(它退出时管道另一端自然没人读)。
取消与收尾靠 `KillTree()` + `CloseConsole()`:关掉伪控制台让输出写端全部关闭,后台读线程才拿得到 EOF,
不会留下一个挂死的读任务。这条路径任何单测都盖不到(单测走假 CLI),
所以真机门禁项 `PullWithProgressAsync_OnRealWslc_StreamsScreenThroughThePseudoTerminal`
专门钉它 —— 断言"至少回调过一帧",子进程启动即被杀时这条断言必红。

## 29. 父进程的标准句柄会盖掉伪控制台,子进程根本没挂上去

**症状**:同一段代码,在资源管理器里双击启动应用时进度实时上屏;
一旦从管道/终端里启动(`dotnet test`、`pwsh -Command`、重定向了 stdout 的脚本),
伪终端一帧都收不到 —— 就是用户报的"只有转圈"。

**真相**:`CreateProcess` 会把父进程**当时的** STD_INPUT/STD_OUTPUT/STD_ERROR 句柄继承给子进程,
这个继承的优先级高于 `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`。
父进程的 std 句柄是管道时,子进程拿到的就是那个管道:它认为自己不在终端里,
于是走"全部攒到进程退出才一次性冲刷"的老路(功能 004 的实测表记的就是这条),ConPTY 白挂了。

**做法**:`StartInternal` 里在 `CreateProcess` 之前用 `SetStdHandle` 把三个标准句柄临时置零,
调用一返回立刻在 `finally` 里还原(只影响这一次 `CreateProcess` 的继承,不动父进程自己的控制台)。
验证方式:同一个门禁测试分别在**非 tty**与 **tty** 两种宿主下跑,并且观察 wslc 的输出
**不再泄漏到父终端** —— 泄漏就说明子进程挂的还是父进程那个终端。

## 30. 分层进度只按"当前帧"求和,"已下载"会倒退

**症状**:真机拉 nginx 时汇总行出现 `1.6 MB → 545.6 kB → 0 B`,数字往回跳。

**真相**:wslc 只在 `Downloading` / `Extracting` 这两种状态后面写字节数;
某层一旦变成 `Download complete`(乃至 `Pull complete`),屏幕上那一行就**不再带字节**了。
"当前帧里所有层的字节数之和"因此会随层数完成而缩水,最后一层完成时直接归零。

**做法**:解析时带一份"每层历史最大字节数"的台账(`WslcPullProgress.Parse(lines, ledger)`),
每帧只把该层的最大值写回去,汇总取台账之和 —— 只增不减。台账属于**一次拉取**:
`ImagesPageViewModel.Begin()` 里必须清空(和进度行、去重记忆一起),
否则第二趟的"已下载"会从上一趟的数字接着涨。累计在后台读线程、清空在 UI 线程,两边共用一把锁。

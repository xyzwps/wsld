# 界面怎么验证

非打包的 WinUI 3 **没有 dev server**:布局改完必须启动应用、点一遍、截图确认。
单元测试(Core 那几百项全走假 `wslc`)覆盖不到视觉宽度、圆角、对齐,也覆盖不到只在运行时暴露的 XAML 问题。
命令行的构建与测试入口、真机门禁的开关方式见 [docs/development.md](../development.md)。

## 哪些只能人工看(或只能靠静态闸)

| 情形 | 为什么自动化抓不到 | 出处 |
|---|---|---|
| 视觉宽度、右边缘裁切、中英切换后布局跳 | 判据就是"看起来对不对";`ComboBox` 的 UIA 矩形还会把下拉占位一起算进去,按它判断一路错 | 见 dev-notes 第 23 条,量宽规则在 `winui-shell.md` |
| 样式 `TargetType` 贴错元素类型 | 编译器不校验,`DataTemplate` 里的元素要到实例化那一刻才创建 —— 页面打开没事,点「添加端口」才炸 | 见 dev-notes 第 37 条;已做成静态闸 `XamlStyleTargetTypeTests` |
| 下拉框选框是空的 | 纯视觉的空:序号已选中、生成的命令也对,只有肉眼看得到(截图时才发现) | 见 dev-notes 第 39 条;已做成静态闸 `XamlComboBoxItemContentTests` |
| ConPTY 那两条(标准句柄、输入写端) | 单测全走假 CLI,这条路任何单测都盖不到 | 见 dev-notes 第 28、29 条;真机门禁项断言"至少回调过一帧",子进程启动即被杀时必红 |
| 渲染中间帧 | WinUI 3 由 DirectComposition 合成,`CopyFromScreen` 可能抓到撕裂帧,上下半截来自不同帧 | 见 dev-notes 第 13 条 |

静态闸自己也要带一个用例:喂一段已知有错的源文件,**必须报且只报一处** ——
规则写错却一条都不报,比没有闸更危险。

## 哪些能自动化

| 要做的事 | 用什么 | 出处 |
|---|---|---|
| 点导航项换页 | `SelectionItemPattern.Select()`(`NavigationViewItem` 不支持 InvokePattern) | 见 dev-notes 第 11 条 |
| 点对话框按钮 | `ContentDialog` 的 `PrimaryButton` / `SecondaryButton` / `CloseButton` 有固定 AutomationId,不必按文案找 | 见 dev-notes 第 16 条 |
| 往输入框灌文本 | UIA 的 `ValuePattern.SetValue()`;WinUI `TextBox` 的 UIA `Name` 是 PlaceholderText,要看 `Value` | 见 dev-notes 第 27 条 |
| 判断按钮可用/状态 | 直接 `InvokePattern.Invoke()`(真禁用会抛异常)、读 `LegacyIAccessiblePattern` 的 State,或按 `AutomationId` 取文本复核 | 见 dev-notes 第 12、13 条 |
| 读进程/镜像残留 | 走查结束后 `Get-Process wslc` 应为 0 个、`wslc images` 与走查前一致 | 005 / 010 / 011 的真机验证都以此收尾 |

**不要**用 `Current.Enabled` 判断可用性:WinUI 3 的 provider 拿不到这个属性,永远读到 `null`,
看起来像"所有按钮都被禁用了"而界面上明明是亮的 → 见 dev-notes 第 12 条。

## 截图与产物的固定动作

- 截图前在空白处点一下,把键盘焦点从按钮上移走(焦点触发的 ToolTip 是**同进程的独立顶层窗口**,
  会混进截图);按面积挑最大的窗口 → 见 dev-notes 第 21 条。
- 窗口边界用 DWM 扩展帧边界(`DWMWA_EXTENDED_FRAME_BOUNDS`)再四边 inset 2px,截图进程必须先
  `SetProcessDPIAware` → 见 dev-notes 第 25、7 条。
- 跑测试/截图前先确认用的是**新构建的 exe**:`Platforms=x64` 会留下两份输出目录,
  一律挑 `LastWriteTime` 最新的那个;构建前先杀进程,否则文件占用导致构建失败被误读成"代码有问题"
  → 见 dev-notes 第 24、8 条。
- 列表/详情行的可读名字要在模板根上显式给 `AutomationProperties.Name`,否则 UIA 树里每行 Name 都是类名,
  读屏念不出来、自动化也找不到行 → 见 dev-notes 第 14 条。
- 截图只是走查过程凭据,不入代码库;册子的验收表只记实测结论(看到什么、量到多少)。

## 真机走查留下的三条教训

1. **实时日志把历史接在后面**:第一轮实现"实时"不清屏,点下去后状态栏从"共 200 行"接着往上长,
   看起来仍是"加载了历史"。真机才看得出来,补了单测钉住"新一轮实时从空白开始"。
2. **两个"停止"重名**:页签里的停止实时与详情头部的停止容器都叫"停止",读屏与自动化分不清
   —— 脚本第一次就点到了停止容器。日志那个改成「停止实时」。
3. **状态栏"最近加载"停在旧时刻**:行数在涨、时间不动,读起来像没新日志了。攒批上屏的阈值与催屏定时器
   这类节奏问题,单测全绿也照样看不出来 → 见 dev-notes 第 19、20 条。

这三条的共性:**症状只在界面上出现**。改动碰到视觉、交互命名、刷新节奏,就要安排一次真机走查,
并把走查后的本机状态复原(临时容器、临时标签、探针都清掉)。

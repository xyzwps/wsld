# 功能 004 · 镜像操作(拉取 / 删除)

状态:已实现
相关:功能 003(镜像列表)、功能 002(容器操作 —— 交互与状态编排沿用同一套做法)

## 是什么

在"镜像"页上做两件事:

1. **拉取镜像**:顶部输入一个引用(例如 `docker.1ms.run/library/redis:7`),点"拉取"或按回车,
   应用调用 `wslc pull`,完成后刷新列表;等待期间可以**取消**。
2. **删除镜像**:每一行一个红色"删除"按钮,调用 `wslc rmi`。如果被容器占用而失败,
   这一行的按钮变成"强制删除",第二次显式确认后才发送 `wslc rmi -f`。

同时把功能 003 的列表行包成"行视图对象"(和容器页一致),
让"正在删除""等待强制删除"这类界面状态能活过刷新。

## 为什么

光有镜像列表是闭环不了的:想跑一个新容器,得先把镜像拉下来;试用完了要能把占磁盘的镜像删掉。
这两个命令在命令行里并不友好 —— `pull` 输出几十行进度、失败了要自己在一堆文本里找原因;
`rmi` 的"占用冲突"信息更是把 Docker 引擎的原始报错直接甩给用户。
桌面应用要提供的正是:确认(避免误删)、可取消(拉错了能停)、进度感(知道没卡死)、
把失败原因翻译成人话,并且只在用户明确同意时才做"强制删除"这种破坏性动作。

## 实测到的 wslc 行为(3.0.1,本规格的依据)

| 场景 | 实测结果 |
|---|---|
| `wslc pull <新镜像>` | 退出码 0(hello-world 约 4–5 秒);stdout 为分层进度行,末尾依次是 `Digest: sha256:...`、`Status: Downloaded newer image for <ref>`、完整引用 |
| `wslc pull <已存在的镜像>` | 退出码 0,1–2 秒;`Status: Image is up to date for <ref>` |
| `wslc pull <拉不到的镜像>` | 退出码 1(约 10 秒超时);stderr `来自引擎的错误信息: Get "https://registry-1.docker.io/v2/": ...` + `错误代码： E_FAIL` |
| `wslc pull -q <ref>` | 退出码 0,stdout 只有完整引用一行(没有 `Status:`,因此本功能不用 `-q`) |
| **进度输出的到达时机** | 把 stdout 重定向到文件后,前 12 秒始终 0 字节,进程退出那一刻才一次性出现全部 45 行 → **实时进度条做不出来**,只能"进行中 + 已等待秒数" |
| **拉取途中终止 wslc 进程** | `wslc images` 里没有半截镜像,也没有残留的 wslc 进程 → 取消 = 结束子进程,不留垃圾 |
| `wslc rmi <未被使用的引用>` | 退出码 0;`已取消标记: <ref>` + `已删除: sha256:...` |
| `wslc rmi <多标签镜像的某一个标签>` | 退出码 0,只有 `已取消标记: ...`;镜像本体仍在(其它标签行照旧列出) |
| `wslc rmi <被容器引用的镜像>` | 退出码 1;stderr `conflict: unable to remove repository reference "..." (must force) - container f47fdddf9219 is using its referenced image aaef90e06523` + `错误代码： ERROR_SHARING_VIOLATION` |
| `wslc rmi -f <被容器引用的镜像>` | **退出码 0**:`已取消标记` 两行 + `已删除: sha256:...`;容器仍留在 `ps -a` 里,但 `wslc start` 它必定失败(实测 `E_INVALIDARG`,OCI runtime create failed) |
| `wslc rmi -f <不存在的引用>` | 退出码 1;`No such image: <ref>` + `错误代码： WSLC_E_IMAGE_NOT_FOUND` |
| `wslc rmi A <不存在的> C` | 退出码 1,A 已被删除、C 没被处理 → **批量删除遇错即停**,所以应用一次只删一个引用 |
| `wslc tag <src> <dst>` | 退出码 0,无任何输出 |

要点:

- 失败信息一律在 stderr,末尾都带"欢迎提 issue"样板句,展示前用 `WslcError.Describe` 清洗(功能 002 · AC4 已建立)。
- `pull` 保留详细输出而不是 `-q`,因为其中的 `Status:` 行能区分"新下载的"和"本来就最新",值得给用户不同提示。
- `-f` 是真删除:即使容器还引用着它,也会把镜像从磁盘上拿掉,留下的容器再也起不来。
  因此**强制删除绝不能是默认路径**,必须是"常规删除失败 → 用户第二次显式确认"。

## 预期行为(验收条件)

### AC1 · 拉取命令映射
`ImagesService.PullAsync(reference)` 精确调用 `pull <reference>`(reference 先 `Trim`),并解析 stdout。

### AC2 · 空引用不触碰 CLI
`PullAsync` / `RemoveAsync` 收到 null 或全空白引用时抛 `ArgumentException`,
且 `IWslcCli` **一次都没被调用**(删除是不可挽回的动作,必须前置校验)。

### AC3 · 拉取结果解析
退出码 0 时返回 `PullResult`:

- `Status: Image is up to date for X` → `IsUpToDate == true`
- `Status: Downloaded newer image for X` → `IsUpToDate == false`
- 输出里没有 `Status:` 行(例如未来格式变化)→ `IsUpToDate == false`、`StatusLine` 为空,不抛异常。

### AC4 · 删除命令映射
`RemoveAsync(reference, force)` → `rmi <reference>`,`force == true` → `rmi -f <reference>`;
非 0 退出抛 `WslcCliException`,`Command` 分别是 `wslc pull` / `wslc rmi`。

### AC5 · 全局单操作互斥
同一时刻只允许一个镜像操作(拉取或删除)。重复调用被直接忽略;
操作进行期间 `RefreshAsync` 跳过,顶部"拉取"按钮与每一行的"删除"按钮都不可用。

### AC6 · 拉取可以取消
`CancelPull()` 取消本次拉取的令牌,`ProcessWslcCli`(功能 001)据此终止 wslc 进程。
取消之后:不报错、**不刷新**、`Notice` 为 `已取消拉取 X`(实测不留半截镜像,无需刷新)。

### AC7 · 进度只做"不确定"表达
因为实测 `pull` 的输出在进程结束时才冲刷,界面不承诺百分比,只显示进度环 +
`正在拉取 X · 已等待 N 秒`;秒数由 `Tick()` 重新求值(界面挂一个 1 秒计时器调用它),
这样单元测试不依赖真实时钟。

### AC8 · 成功后刷新一次并给出提示
拉取或删除成功 → 执行一次刷新,并设置 `Notice`:
`已拉取 X` / `X 已是最新` / `已删除 X`。
新操作开始时 `Notice` 与 `Error` 一并清空,避免旧提示和新结果同时出现。

### AC9 · 失败不丢数据
任何失败 → `Error` 为清洗后的单句文案,列表保持上一次成功的数据,`Notice` 清空。

### AC10 · 被占用 → 变成"强制删除"按钮
删除失败且输出含 `must force` / `unable to remove repository reference` / `ERROR_SHARING_VIOLATION` 之一时:
该行 `ForcePending == true`,按钮文案由"删除"变为"强制删除",`Error` 末尾追加一句
"…再点一次「强制删除」才能把镜像从磁盘上移除"。
其它错误(如 `WSLC_E_IMAGE_NOT_FOUND`)**不**进入这个状态,不给用户多余的破坏性选项。

### AC11 · 强制删除是第二次显式动作
只有 `ForcePending == true` 的行,第二次点击才发送 `rmi -f <ref>`;
界面此时必须在确认框里用红色文字说明"该镜像正被 N 个容器引用,强制删除后这些容器将无法再次启动"(实测依据)。
删除成功后 `ForcePending` 复位。

### AC12 · 行状态活过刷新
行对象按 `Reference` 复用(与功能 002 的容器行同一套做法):刷新时只换数据,
`IsBusy` / `ForcePending` / 按钮可用性都保留;引用从列表消失后行对象一并丢弃。

### AC13 · 不破坏功能 003 的约定
启动即加载、搜索过滤、排序、汇总、失败保留旧数据、不自动轮询 —— 行为与功能 003 完全一致。

### AC14 · 界面
镜像页顶部:引用输入框(回车即拉取)+ "拉取"按钮 + "取消"按钮(仅拉取期间可见)+ 进度环;
成功提示用绿色 InfoBar,失败用红色 InfoBar;每行右侧红色"删除"按钮,点击先弹确认框
(被引用时提示引用它的容器数量),确认后才执行。

## 测试清单

| 测试文件 | 覆盖 |
|---|---|
| `ImageActionsServiceTests`(新增) | AC1 AC2 AC3 AC4 |
| `ImagesPageViewModelActionTests`(新增) | AC5 AC6 AC7 AC8 AC9 AC12 AC14 的 VM 侧 |
| `ImageForcePolicyTests`(新增) | AC10 AC11 |
| `ImagesPageViewModelTests`(功能 003) | AC13 回归 |
| `RealWslcImagesIntegrationTests`(环境开关) | 真实 `wslc pull`(已存在→up to date)、`wslc tag` + `wslc rmi` 删除临时标签后列表恢复原状 |
| 夹具 `wslc-pull-new.txt` / `wslc-pull-uptodate.txt`(新增) | AC3 的真实输出 |

## 验证结果

### 自动化

- 单元测试 `dotnet test`:**167 通过 / 0 失败 / 0 警告**(功能 004 新增 54 个 ——
  `ImageActionsServiceTests` 20、`ImagesPageViewModelActionTests` 16、`ImageForcePolicyTests` 18;
  功能 001–003 的 113 个全部保持通过,AC13 成立)。
- 真机冒烟 `WSLC_DESKTOP_INTEGRATION=1`:6 个用例全通过,其中本功能新增 3 个 ——
  重复拉取已存在的镜像 → `IsUpToDate == true`;删除正被 2 个容器引用的 busybox → 抛冲突、
  `ImageRemovalConflict.IsConflict == true`、镜像仍在列表里;给已有镜像打临时标签再
  `RemoveAsync` → 标签消失、镜像数据无损。

### 界面走查(真实 wslc 3.0.1,截图见 `docs/screenshots/`)

| 截图 | 验证的验收标准 |
|---|---|
| `004-images-page.png` | AC13 / AC14:布局、汇总行、每行的"删除"按钮与占用标注 |
| `004-pulling.png` | AC5 / AC6 / AC7:拉取中"拉取"变灰、"取消"出现、进度环 + "正在拉取 … · 已等待 2 秒"、该行行内进度环、四个"删除"全部禁用、列表保持 4 行不动 |
| `004-pull-success.png` | AC3 / AC8:绿色提示"docker.1ms.run/library/hello-world:latest 已是最新" |
| `004-pull-cancelled.png` | AC6:点"取消"后提示"已取消拉取 …",列表不刷新、本机没有留下半截镜像 |
| `004-remove-confirm.png` | AC11 / AC14:删除前确认框,并说明"只会取消当前标签" |
| `004-force-pending.png` | AC9 / AC10:引擎拒绝后红色错误条保留原始 conflict 信息 + 追加强制提示,该行按钮已变成"强制删除" |
| `004-force-confirm.png` | AC11:第二次点击弹"强制删除镜像",红字写明"正被 2 个容器引用,强制删除后这些容器将无法再次启动";选"取消"后什么都没删 |
| `004-containers-regression.png` | 功能 001 / 002 的容器页回归正常 |

走查结束后本机状态与走查前一致:镜像 3 个(busybox / hello-world / nginx)、容器 2 个(probe1、probe2)。

### 顺带修掉的两处旧问题

- 功能 003 的 `ImagesService.ListAsync` 参数被误合并成一个字符串(`"--format json"`),
  由 003 的回归测试当场抓到,已改回 `"--format", "json"`。
- 列表行的无障碍名称原本是数据对象的类名(`...ImageItemViewModel`),
  现在两个页面的行都显式绑定 `AutomationProperties.Name`。
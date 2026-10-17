# 开发指南

面向想跑起来、改代码、加测试的人。用户能看见什么看 [docs/specs/](specs/),
为什么这么定与实测依据看 [docs/notes/](notes/index.md),交付安装包看 [release-packaging.md](release-packaging.md)。

## 环境要求

- Windows 11,已安装 WSL 3(含 `wslc.exe`,默认位于 `C:\Program Files\WSL\wslc.exe`)——
  应用只是这枚 CLI 的图形外壳,没有它跑不出任何真实数据。
- .NET 9 SDK。仓库只用 `dotnet` CLI 构建(不依赖 Visual Studio 的 XAML 工具链,原因见
  [dev-notes.md](dev-notes.md) 第 3 条)。
- 可选:[just](https://github.com/casey/just),一条命令做完整件事。

## 常用命令

装了 just 就用 `justfile` 里的快捷命令,`just --list` 看全部:

| 命令 | 做什么 |
|---|---|
| `just dev` | 编译并启动桌面应用 |
| `just test` | 跑单元测试(不需要真实 `wslc`) |
| `just itest` | 端到端冒烟,要本机真的有 `wslc.exe` |
| `just kill` | 结束正在运行的实例(编译提示"文件被占用"时先跑它) |
| `just clean` | 清编译产物:各项目 `bin\`、`obj\` 加 `artifacts\`(发布目录与旧 setup.exe) |
| `just build` | 只编译整个解决方案(x64) |
| `just package` / `just verify-installer` | 打安装包 / 安装包自检,见 [release-packaging.md](release-packaging.md) |

不用 just 的等价命令:

```powershell
dotnet build wsld.sln
dotnet test  wsld.sln
dotnet run --project src/wsld   # 启动桌面应用
```

## 工程结构

- `src/wsld.Core` —— 全部业务逻辑,与 UI 解耦,可直接单测:
  `WslcCli`(进程调用)、`Parsing`(输出/NDJSON 解析)、`Models`、`ViewModels`、
  `Services`、`Settings`、`I18n`
- `src/wsld` —— WinUI 3 视图壳(Windows App SDK 2.5,非打包 unpackaged 模式):
  `Pages`(左侧导航 + 各功能页面)、`Converters`、`Ui`、`Services`
- `tests/wsld.Tests` —— xUnit
- `tools/` + `installer/` —— 打包、图标、清理等脚本
- `assets/` —— 图标母版等素材

## 测试

默认 `dotnet test` 只跑单元测试(460 项),全部不依赖真实 `wslc`。

打开真实 CLI 的端到端冒烟(共 13 项):

```powershell
$env:WSLD_INTEGRATION = "1"
dotnet test --filter FullyQualifiedName~Integration
```

或直接 `just itest`。这些用例会真的动本机容器与镜像,跑之前确认环境干净、不心疼流量:

- 容器那条会自建一个专用容器,跑完 启动/停止/重启/删除 一整个来回后自动清理。
- 镜像那几条重复拉取已存在的镜像(其中一条走伪终端,确认拉取进度确实逐帧回调)、
  尝试删除正被引用的镜像(引擎会拒绝,本机无损)、给已有镜像打一个临时标签再删掉它。
- 日志那几条读取未启动容器的空输出、不存在的容器的错误文案,并自建一个每秒打印一行的
  临时容器,验证 `-n` 尾部行数、`-t` 时间戳、**实时日志只收到点下去之后的行**
  (`-f --since`,不把历史冲出来)、逐行回调确实到达、以及取消后不留下半死的 `wslc` 进程。

## 界面改动的验证方式

非打包的 WinUI 3 没有 dev server,布局改完必须启动应用、点一遍、截图确认——
单元测试覆盖不到视觉宽度、圆角、对齐这类问题,详见 [dev-notes.md](dev-notes.md)。

截图用 `tools/capture-window.ps1` 抓当前运行的窗口,产物落在仓库根的 `capture.png`,
它只是走查时的过程凭据,**不入代码库**;spec 的验收表只记实测结论(看到什么、量到多少)。

## 规格册怎么写

`docs/specs/` 是**用户可见契约的唯一清单**。给人读、给人 review;可执行测试是它的影子,不是反过来。
这儿只放"用户能看见什么",不放为什么、也不放实现:契约依据(实测到的 wslc 行为、刻意的取舍)
按主题在 [docs/notes/](notes/index.md),会反复踩到的坑在 [dev-notes.md](dev-notes.md)。

一个能力一本册子,一份扁平文件 `docs/specs/<能力>.md`,结构固定四档:

```markdown
# container-list

## Purpose
一句话:这本册子管的是界面上哪一块东西。

### Requirement: 这一条锁的是哪个不变量
#### Scenario: CL-001 启动应用就把容器摆出来
- **WHEN** 本机装有 wslc 且启动应用
- **THEN** 容器页列出的每一条与 `wslc list -a` 的容器一一对应
- **AND** 没有任何容器时读得到"没有容器",不出现错误条
```

### 编号规则

- 每条 `#### Scenario:` 带稳定 ID,形如 `CL-001`,按能力前缀分组(见下面那张索引表)。
- **ID 是稳定句柄,不是阅读顺序**:册子按语义分节,新场景先选节、再取该前缀下的下一个号,
  所以全局 ID 不保证递增;要守的规矩是**同一 Requirement 内按 ID 升序**。
- **ID 一旦提交不复用、不重排。** 删掉一条是显式评审动作,要说清为什么这条契约不再成立。
  三位号段,上限 999。
- **一条 Scenario ≤ 5 行**:判的是"这一条读不读得动"。

### 写法规矩

1. **只写用户可观察的结果**:看得见的文字、看得见的几何、点击/键盘的结果、`wslc` 那侧看得见的命令。
   - ✗ `THEN `Items` 集合计数为 3`(条数对、内容被裁光,测试照样绿)
   - ✓ `THEN 列表可见三行,名称分别读得到 probe1、probe2、rustfs`
2. **不写实现**:不出现类名、方法名、控件名、XAML 资源键。实现变了册子不该变。
   唯一例外是与 `wslc` 的**调用契约**——它是系统边界,`wslc` 那一侧真看得见,
   所以允许出现命令行与退出码(如"点刷新只发 `list -a --format json` 这一条"),
   但仍不写应用内部由哪个类去发。
3. **一条 Scenario 只锁一个不变量。**
4. **bug fix 必须先补 Scenario**(描述"现象"),再补可执行用例,再改代码。
5. 每条契约讲不清"为什么这样定"时,在 Requirement 正文里给一行指向 `docs/notes/` 的引用,
   别把依据抄进册子。

### 与测试的绑定

可执行侧用 trait 挂 ID:`[Fact, Trait("case", "CL-001")]`。

```powershell
just casecov   # 册子里的编号与测试里的编号做双向差集
```

册里有号、测试没挂 = 未实现;测试挂了号、册子里没有 = 私测(禁止)。

### 能力索引

| 前缀 | 能力 | 册子 | 谁该读 |
|---|---|---|---|
| CL | 容器列表(刷新 / 搜索 / 汇总) | `container-list.md` | 碰容器页那一列的人 |
| CD | 容器详情卡片(字段呈现与几何) | `container-detail.md` | 往详情区加字段、动卡片宽度的人 |
| CA | 容器生命周期操作(启动/停止/重启/删除) | `container-actions.md` | 碰操作按钮与互斥的人 |
| LG | 容器日志(查看 / 实时) | `container-logs.md` | 碰日志页签、流式读取与显示上限的人 |
| IM | 镜像列表(搜索 / 排序 / 汇总) | `images.md` | 碰镜像页那一列的人 |
| IA | 镜像操作(拉取 / 删除 / 强制删除 / 取消) | `image-actions.md` | 碰拉取框与删除按钮的人 |
| PP | 拉取实时进度(伪终端屏幕 + 分层汇总) | `pull-progress.md` | 碰进度区、字节台账与降级路径的人 |
| RM | 镜像加速:引用改写与拉后改名 | `registry-mirror.md` | 碰镜像源改写、tag/rmi 收尾顺序的人 |
| RB | `wslc run` 命令编排(生成/复制/执行/读回/历史) | `run-builder.md` | 碰那张表单与命令拼装的人 |
| SH | 界面外壳(顶栏 / 导航 / 图标键 / 窗口几何) | `shell.md` | 任何改 XAML 布局、加按钮的人 |
| TH | 外观主题(跟随系统 / 浅 / 深) | `theme.md` | 碰换皮与标题栏配色的人 |
| LANG | 界面语言与词条 | `i18n.md` | 往界面里写文案的人 |
| SET | 设置读写与关于卡片 | `settings.md` | 改 settings.json 结构、动关于卡片的人 |

新加能力就添一行、取一个新前缀;006 进入容器、007 资源统计、008 网络与卷这些还没动的
规划项见 [README](../README.md),动手时再开册子。

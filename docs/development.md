# 开发指南

面向想跑起来、改代码、加测试的人。产品层面的"是什么/为什么"看 [docs/specs/](specs/),
交付安装包看 [release-packaging.md](release-packaging.md)。

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

默认 `dotnet test` 只跑单元测试(426 项),全部不依赖真实 `wslc`。

打开真实 CLI 的端到端冒烟(共 10 项):

```powershell
$env:WSLD_INTEGRATION = "1"
dotnet test --filter FullyQualifiedName~Integration
```

或直接 `just itest`。这些用例会真的动本机容器与镜像,跑之前确认环境干净、不心疼流量:

- 容器那条会自建一个专用容器,跑完 启动/停止/重启/删除 一整个来回后自动清理。
- 镜像那几条重复拉取已存在的镜像(其中一条走伪终端,确认拉取进度确实逐帧回调)、
  尝试删除正被引用的镜像(引擎会拒绝,本机无损)、给已有镜像打一个临时标签再删掉它。
- 日志那几条读取未启动容器的空输出、不存在的容器的错误文案,并自建一个每秒打印一行的
  临时容器,验证 `-n` 尾部行数、`-t` 时间戳、跟随的流式回调确实逐行到达、以及取消后
  不留下半死的 `wslc` 进程。

## 界面改动的验证方式

非打包的 WinUI 3 没有 dev server,布局改完必须启动应用、点一遍、截图确认——
单元测试覆盖不到视觉宽度、圆角、对齐这类问题,详见 [dev-notes.md](dev-notes.md)。

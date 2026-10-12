# WSLC Desktop

管理 Windows WSL 容器(`wslc` CLI)的 WinUI 3 桌面应用。

## 功能与规格
每个功能都有对应的 spec 文档(是什么/为什么/预期行为)与单元测试,见 `docs/specs/`。

| 编号 | 功能 | 状态 | 规格 |
|---|---|---|---|
| 001 | 容器列表 | 已实现 | docs/specs/001-container-list.md |
| 002 | 容器生命周期操作(启动/停止/重启/删除) | 已实现 | docs/specs/002-container-actions.md |
| 003 | 镜像列表与应用导航 | 已实现 | docs/specs/003-images.md |
| 004 | 镜像操作(拉取 / 删除) | 已实现 | docs/specs/004-image-actions.md |
| 005 | 容器日志(尾部行数 / 时间戳 / 实时跟随) | 已实现 | docs/specs/005-container-logs.md |
| 006 | 进入容器(执行命令) | 规划 | — |
| 007 | 资源统计 stats | 规划 | — |
| 008 | 网络与卷 | 规划 | — |
| 009 | 系统信息与设置 | 规划 | — |

## 技术栈
- WinUI 3(Windows App SDK 2.5,.NET 9,非打包 unpackaged 模式)
- `WslcDesktop.Core`:全部业务逻辑(wslc 进程调用、NDJSON 解析、ViewModel),与 UI 解耦,可单测
- `WslcDesktop`:WinUI 视图壳(左侧导航 + 各功能页面)
- `WslcDesktop.Tests`:xUnit

## 构建与运行
```powershell
dotnet build WslcDesktop.sln
dotnet test  WslcDesktop.sln
dotnet run --project src/WslcDesktop   # 启动桌面应用
```
要求:Windows 11 + 已安装 WSL 3(含 `wslc.exe`,默认位于 `C:\Program Files\WSL\wslc.exe`)。

## 端到端冒烟测试(可选)
默认 `dotnet test` 只跑不依赖真实 `wslc` 的单元测试(242 个)。
打开真实 CLI 测试(共 10 项):容器那条会自行创建一个专用容器,跑完 启动/停止/重启/删除 一整个来回后自动清理;
镜像那几条重复拉取已存在的镜像、尝试删除正被容器引用的镜像(引擎会拒绝,本机无损)、给已有镜像打一个临时标签再删掉它;
日志那几条读取未启动容器的空输出、不存在的容器的错误文案,并自建一个每秒打印一行的临时容器,
验证 `-n` 尾部行数、`-t` 时间戳、跟随的流式回调确实逐行到达、以及取消后不留下半死的 `wslc` 进程。
```powershell
$env:WSLC_DESKTOP_INTEGRATION = "1"
dotnet test --filter FullyQualifiedName~Integration
```
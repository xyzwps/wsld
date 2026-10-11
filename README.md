# WSLC Desktop

管理 Windows WSL 容器(`wslc` CLI)的 WinUI 3 桌面应用。

## 功能与规格
每个功能都有对应的 spec 文档(是什么/为什么/预期行为)与单元测试,见 `docs/specs/`。

| 编号 | 功能 | 状态 | 规格 |
|---|---|---|---|
| 001 | 容器列表 | 已实现 | docs/specs/001-container-list.md |
| 002 | 容器生命周期操作(启动/停止/重启/删除) | 进行中 | docs/specs/002-container-actions.md |
| 003 | 镜像管理 | 规划 | — |
| 004 | 容器日志 | 规划 | — |
| 005 | 进入容器(执行命令) | 规划 | — |
| 006 | 资源统计 stats | 规划 | — |
| 007 | 网络与卷 | 规划 | — |
| 008 | 系统信息与设置 | 规划 | — |

## 技术栈
- WinUI 3(Windows App SDK 2.5,.NET 9,非打包 unpackaged 模式)
- `WslcDesktop.Core`:全部业务逻辑(wslc 进程调用、NDJSON 解析、ViewModel),与 UI 解耦,可单测
- `WslcDesktop`:WinUI 视图壳
- `WslcDesktop.Tests`:xUnit

## 构建与运行
```powershell
dotnet build WslcDesktop.sln
dotnet test  WslcDesktop.sln
dotnet run --project src/WslcDesktop   # 启动桌面应用
```
要求:Windows 11 + 已安装 WSL 3(含 `wslc.exe`,默认位于 `C:\Program Files\WSL\wslc.exe`)。
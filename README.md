# wsld

管理 Windows WSL 容器(`wslc` CLI)的 WinUI 3 桌面应用:容器与镜像的列表、启动/停止/重启/删除、
日志、拉取镜像与实时进度、`wslc run` 命令编排,加上主题与多语言设置——把命令行里那些事做成
能点、能看、能复制的界面。

![容器列表](docs/screenshots/009-dark-zh-containers.png)

技术栈:WinUI 3(Windows App SDK 2.5,.NET 9,非打包模式)+ 独立的 `wsld.Core`
业务层(进程调用、解析、ViewModel,与 UI 解耦、可单测)。

## 功能

用户能看见什么,写在 `docs/specs/` 的用例册里 —— 一个能力一册,一条条 `Scenario` 就是验收标准,
每条带稳定编号(`CL-001` 这种),测试用 `[Trait("case", "CL-001")]` 挂上去对账。
为什么这么定、实测到的 `wslc` 行为在 [docs/notes/](docs/notes/index.md)。

| 能力 | 册子 | 编号 |
|---|---|---|
| 容器列表(刷新 / 搜索 / 汇总) | [container-list.md](docs/specs/container-list.md) | CL |
| 容器详情卡片 | [container-detail.md](docs/specs/container-detail.md) | CD |
| 容器生命周期操作(启动/停止/重启/删除) | [container-actions.md](docs/specs/container-actions.md) | CA |
| 容器日志(查看 / 实时) | [container-logs.md](docs/specs/container-logs.md) | LG |
| 镜像列表(搜索 / 排序 / 汇总) | [images.md](docs/specs/images.md) | IM |
| 镜像操作(拉取 / 删除 / 强制删除) | [image-actions.md](docs/specs/image-actions.md) | IA |
| 拉取实时进度(分层屏幕 + 汇总) | [pull-progress.md](docs/specs/pull-progress.md) | PP |
| 镜像加速(引用改写与拉后改名) | [registry-mirror.md](docs/specs/registry-mirror.md) | RM |
| `wslc run` 命令编排 | [run-builder.md](docs/specs/run-builder.md) | RB |
| 界面外壳(顶栏 / 导航 / 图标键 / 几何) | [shell.md](docs/specs/shell.md) | SH |
| 外观主题 | [theme.md](docs/specs/theme.md) | TH |
| 界面语言与词条 | [i18n.md](docs/specs/i18n.md) | LANG |
| 设置读写与关于卡片 | [settings.md](docs/specs/settings.md) | SET |

还没动的规划项:进入容器(执行命令)、资源统计 stats、网络与卷 —— 动手时各开一册。

## 开始使用

- **只要用起来**:拿 `wsld-Setup-<版本>-x64.exe` 安装包双击安装,客户机不需要预装 .NET
  或 Windows App SDK;实际使用仍需本机已装 WSL 3。出包与分发见
  [docs/release-packaging.md](docs/release-packaging.md)。
- **要改代码**:环境要求、构建/运行/测试命令、工程结构见
  [docs/development.md](docs/development.md)。

```powershell
just dev    # 编译并启动(需要 just;没有就用 dotnet build + dotnet run,见开发指南)
just test   # 单元测试
```

## 文档

| 文档 | 讲什么 |
|---|---|
| [docs/development.md](docs/development.md) | 环境、命令、工程结构、单元与端到端测试、规格册怎么写 |
| [docs/release-packaging.md](docs/release-packaging.md) | 打交付给客户安装包:方案、图标、代码签名/SmartScreen、常见问题 |
| [docs/specs/](docs/specs/) | 用例册:用户能看见什么(一条条 Scenario,测试照着对账) |
| [docs/notes/](docs/notes/index.md) | 依据记录:为什么这么定、实测到的 `wslc` / WinUI 外部行为 |
| [docs/dev-notes.md](docs/dev-notes.md) | 会反复踩到、网上查不到的坑(WinUI 3 / 构建 / 布局) |

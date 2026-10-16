# 功能规格 001 · 容器列表

- **状态**:已实现 · **日期**:2026-10-11 · **版本**:v1

## 功能是什么
在主窗口的「容器」页中列出本机 WSL 容器(wslc)的全部容器,包含实时状态:
- 数据源为 `wslc list -a --format json` 的机器可读输出;
- 每条记录的字段:名称、镜像、命令、状态(运行中/已停止等)、端口、网络、创建时间、短 ID、平台;列表行只呈现名称、状态与镜像+命令,其余明细在点该行后的右栏详情里看;
- 支持手动刷新与定时自动刷新(固定 3 秒一次,不开放调节;界面上不再标注这个节奏,工具栏只留刷新键);
- 支持关键字搜索(按名称/镜像/ID 的子串,不区分大小写);
- 支持"显示已停止的容器"开关(默认显示);
- 顶部汇总:共 N 个容器 · M 个运行中,以及最近一次刷新时间。

## 为什么需要
wslc 是命令行工具,快速浏览"现在有什么容器在跑、状态如何"需要反复敲命令、肉眼扫描表格。
桌面应用提供持续可见的全局视图与一键刷新/搜索,是后续所有管理功能(生命周期操作、
日志、exec 等)的入口页面。

## 预期行为(验收标准)
- AC1 启动应用后自动加载一次列表;失败不崩溃,在页面顶部显示错误条,并保留上一次成功数据。
- AC2 `wslc list` 无输出(没有任何容器)时,显示"没有容器",不视为错误。
- AC3 输出为 NDJSON(每行一个 JSON 对象);解析器必须同时兼容:空串 / 单个对象 / 多行对象 / 标准 JSON 数组 / 美化缩进 JSON。解析失败抛出带行号与原文片段的错误。
- AC4 字段映射基于真实输出(见 tests/wsld.Tests/Fixtures/*.ndjson):ID、Names、Image、Command、Status、State、RunningFor、CreatedAt、Ports、Networks、Labels、Size、HealthStatus、Platform{architecture,os}。
- AC5 State 为 `running`(不区分大小写)判定为运行中,计入"运行中"数量并显示绿色状态点,否则灰色。
- AC6 搜索框输入立即过滤当前已加载数据(不重新调用 CLI);切换"显示已停止"同理。
- AC7 自动刷新定时器触发时若上一次刷新未完成(防抖动/重入),跳过本次。
- AC8 CLI 进程定位:环境变量 `WSLD_WSLC_PATH` > `%ProgramFiles%\WSL\wslc.exe` > PATH 中的 wslc.exe。找不到时错误信息明确指引用户安装 WSL 3。
- AC9 非零退出码视为错误,错误文案包含 wslc 的 stderr 内容。

## 接口与数据
- `IWslcCli.RunAsync(args, ct) -> WslcCliResult(ExitCode, Stdout, Stderr)`:进程调用抽象,测试注入 Fake 实现。
- `ContainersService.ListAllAsync()`:执行 `list -a --format json` 并解析为 `ContainerInfo` 列表。
- `ContainersPageViewModel`:Items/汇总/错误/过滤逻辑,全部可在无 UI 环境下单元测试。

## 测试策略
单元测试(xUnit,针对 Core):
1. 解析器:空输出、单行、多行、数组、美化 JSON、坏行(行号)、真实夹具字段映射。
2. 服务:调用参数正确(`list -a --format json`)、非零退出的错误传播。
3. ViewModel:刷新成功填充、错误保留旧数据、搜索过滤、显示已停止开关、运行中计数、防重入。
端到端手动验证:本机运行应用,对照 `wslc list -a` 输出。
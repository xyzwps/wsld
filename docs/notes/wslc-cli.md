# wslc CLI 的公共事实

依据来源:本机 WSLC 3.0.1 实测,命令原文、退出码、错误码都是跑出来的。
这一册只管**跨命令通用**的部分;分命令的形态见 `wslc-images.md`(镜像)、`wslc-logs.md`(日志)、
`run-command.md`(`run`)、`registry-mirror.md`(镜像加速)、`conpty-pull-progress.md`(拉取进度)。

## 退出码与错误流

- **非 0 即失败**,没有"警告性退出码"这一档。
- 错误信息一律写在 **stderr**,stdout 保持空。参数非法也是这个形态。
- 退出码 0 **不代表有内容**:`wslc logs <已停止的容器>` 是空输出 + 退出码 0(见 `wslc-logs.md`)。
- 失败输出末尾固定带一句样板话:"如果此错误是意外错误,请考虑搜索现有问题或在 … 提交新问题。"

生命周期四命令的实测形态:

| 命令 | 成功 | 失败 |
|---|---|---|
| `wslc start <id>` | 退出码 0,无输出 | stderr + 非 0 |
| `wslc stop <id>` | 退出码 0,无输出 | stderr + 非 0 |
| `wslc restart <id>` | 退出码 0,无输出 | stderr + 非 0 |
| `wslc remove [-f] <id>` | 退出码 0,无输出 | stderr + 非 0 |

`wslc logs <不存在的容器>` 的原文(错误码与样板句同批出现):

```
找不到容器 'nosuchcontainer'。
错误代码： WSLC_E_CONTAINER_NOT_FOUND
```

## 错误文案的清洗约定(`WslcError.Describe`)

界面不直接贴 stderr 原文。`WslcError.Describe(stderr)` 的规则:

- 去掉空行与"如果此错误是意外错误…"那句样板提示;
- 保留真正的错误描述与 `错误代码:` 行,多行用空格连成一句;
- 空 stderr 时返回 `"wslc 命令执行失败。"`。

所以 `找不到容器 'x'。` + `错误代码： WSLC_E_CONTAINER_NOT_FOUND` + 样板行,最终显示为
`找不到容器 'x'。 错误代码： WSLC_E_CONTAINER_NOT_FOUND`。
列表刷新失败、日志读取失败、命令编排就地执行失败都套这一套,保证界面里错误风格一致。

## 生命周期四命令的幂等与错误码

- **幂等**:对"运行中"的容器执行 `start`、对"已停止"的容器执行 `stop`,都返回退出码 0。
  应用因此不需要自己预判状态再决定是否调用 —— 按钮的可用性只是体验优化,不是正确性前提。
- `restart` 对已停止的容器会直接启动它(CLI 帮助原文:"未运行的容器将启动")。
- `remove` 对**运行中**的容器失败,错误码 `WSLC_E_CONTAINER_IS_RUNNING`,并提示使用 `-f`。
- `remove` 对不存在的容器失败,错误码 `WSLC_E_CONTAINER_NOT_FOUND`。

## 容器标识能传什么

`start` / `stop` / `restart` / `remove` / `logs` 都接受**名称、12 位短 ID、完整 ID**(`logs probe1` 与
`logs f47fdddf9219` 都是退出码 0)。`wslc list --format json` 的 `ID` 字段本身就是 12 位短 ID,可以直接当参数用。

取舍:发起操作时一律用 `ID` 字段,**不用名称** —— 名称可能有别名、可能重名。

## 机器可读输出的通用形态

`wslc list -a --format json` 与 `wslc images --format json` 都是 **NDJSON**:每行一个 JSON 对象,不是数组。

- 没有任何容器 / 没有镜像时输出**空串**,退出码 0 —— 这不是错误。
- 字段名**首字母大写**(`ID`、`Names`、`Image`…),解析必须大小写不敏感。
- **数字字段是字符串**:`"Containers":"2"` 而不是 `"Containers":2`。未来 CLI 若改成真正的数字,解析也必须接受;
  无法识别成数字时(如 `N/A`)按 0 处理,不抛异常。
- 解析器必须同时兼容:空串 / 单个对象 / 多行对象 / 标准 JSON 数组 / 美化缩进 JSON。
  坏行抛出的错误要带行号与原文片段。
- `wslc images`(不带 `--format json`)输出 Docker 风格表格,只能人工核对用。

`list -a --format json` 一条记录的字段:`ID`、`Names`、`Image`、`Command`、`Status`、`State`、`RunningFor`、
`CreatedAt`、`Ports`、`Networks`、`Labels`、`Size`、`HealthStatus`、`Platform{architecture,os}`。

- `State` 为 `running`(不区分大小写)判定为运行中。
- `Labels` 是**一整条逗号拼接的串**,不是一份字典。拆成"键一行、值一行"时的规则(`ContainerLabelParser`):
  逗号在引号/括号之外、且后面紧跟 `键名=` 才算边界。这串本身有歧义 —— 值里写了 `,foo=bar` 会被多拆一条,
  但不会丢数据。

## logs 没有机器可读输出

`wslc logs` 的选项只有 `--details`、`-f/--follow`、`-n/--tail`、`-t/--timestamps`、`--since`、`--until`,
**没有 `--format json`**,输出是原始文本行。逐条实测见 `wslc-logs.md`。

## `wslc.exe` 从哪儿找

定位顺序(实测可用):环境变量 `WSLD_WSLC_PATH` > `%ProgramFiles%\WSL\wslc.exe` > PATH 里的 `wslc.exe`。
找不到时的提示要指得下去装 WSL 3,或者把那个环境变量设上(界面上的话见 `specs/container-list.md` CL-021,
用的是哪一份在关于卡片里读得到,见 `specs/settings.md` SET-010)。

## 参数怎么传给进程

`--format json` 必须作为**两个**参数传(`--format`、`json`),不能并成一个字符串 ——
真机出过这个错(003 的回归测试当场抓到),并成一个参数时 `wslc` 认不出这个选项。

## 应用侧的并发与刷新取舍

- **全局单操作**:`wslc` 的容器操作可能耗时数秒,并发发起多个操作会让随后的刷新互相踩踏 →
  界面同一时刻只放一个动作在飞(册子 `specs/container-actions.md` CA-008/CA-009)。
- **操作期间不刷列表**:免得列表在操作中途被换血、用户看到不上不下的中间态(CA-010)。
- **行对象按 ID / 引用续用**:全部重建会让按钮闪断、把忙碌状态与"等待强制删除"那一档冲掉(CA-023、IA-020)。
- **列表与过滤不进程**:搜索、切"显示已停止"都在已读回的数据里挑,一次 `wslc` 都不起(IM 同一条)。
- **容器页 3 秒一轮、镜像页不轮询**:容器状态会被别处改动,3 秒足够看不出延迟(再密就是白起进程);
  镜像本机变化极少,轮询只是无意义的进程调用(册子 CL-007 与 IM-019)。
- **镜像按"被引用数"排序**:引用数**不代表运行状态**,只是清理时想先看有用的那几条。


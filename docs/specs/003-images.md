# 功能 003 · 镜像列表与应用导航

状态:已实现
相关:功能 001(容器列表)、功能 004(镜像操作)

## 是什么

新增一个 **镜像** 页面,展示本机 WSLC 容器运行时里已有的镜像(仓库名、标签、镜像 ID、大小、创建时间、
被多少容器引用),数据来自 `wslc images --format json`。

同时把主窗口改成 **左侧导航(NavigationView)**,在"容器"和"镜像"两个页面之间切换,
为后续的功能(日志、统计、网络与卷)留出位置。

## 为什么

容器离不开镜像:要跑一个新容器,先得知道本机有哪些镜像、叫什么名字、占多大空间。
只有容器列表的话,用户想启动新容器就必须回到命令行去查镜像名,这个应用的价值就打折了。

镜像列表还是功能 004(拉取 / 删除镜像)的载体 —— 没有列表页,拉取和删除就没有落脚点。

## 实测到的 wslc 行为(3.0.1,作为本规格的依据)

| 命令 | 结果 |
|---|---|
| `wslc images --format json` | 退出码 0,NDJSON,每行一个镜像对象;无镜像时输出空 |
| `wslc images` | 退出码 0,Docker 风格表格(仅用于人工核对,应用不使用) |
| `wslc images -a --format json` | 与不带 `-a` 相同(本机暂无中间层镜像) |
| `wslc images -q` | 每行一个 12 位镜像 ID |

真实的一行 JSON(已核对):

```json
{"Containers":"2","CreatedAt":"2026-05-13 10:21:49 +0800 GMT+8","CreatedSince":"5 months ago",
 "Digest":"<none>","ID":"aaef90e06523","Repository":"docker.1ms.run/library/busybox",
 "SharedSize":"N/A","Size":"4.45MB","Tag":"latest","UniqueSize":"N/A"}
```

要点:

- 字段名首字母大写,解析必须大小写不敏感(沿用功能 001 的做法)。
- **数字字段是字符串**:`"Containers":"2"` 而不是 `"Containers":2`。未来 CLI 若改成真正的数字,
  解析也必须能接受(见 AC3)。
- `ID` 是 12 位短 ID,`Digest` 常态是 `<none>`;`SharedSize` / `UniqueSize` 常态是 `N/A`。
- 同一个镜像 ID 可以有多个标签:`wslc tag` 之后 `images --format json` 会为每个 `仓库:标签`
  输出一行,`ID` 相同、`Containers` 相同(实测)。因此 **列表按行展示,不按 ID 去重**,
  删除时也必须按"仓库:标签"这个引用去删,而不是按 ID。
- 没有标签的镜像其 `Repository` / `Tag` 为 `<none>`(Docker 惯例,应用需读得出"无标签")。

## 预期行为(验收条件)

### AC1 · 命令映射
`ImagesService.ListAsync()` 必须精确调用 `images --format json`,并解析其 stdout。

### AC2 · 解析字段
`Repository` `Tag` `ID` `Size` `CreatedAt` `CreatedSince` `Containers` `Digest` 映射到
`ImageInfo` 的同义属性;缺字段留空字符串,不报错。

### AC3 · 数字/字符串都接受
`"Containers":"2"` 与 `"Containers":2` 都能解析成 `ContainerCount == 2`;
无法识别成数字(如 `N/A`)时 `ContainerCount == 0`,不抛异常。

### AC4 · 输出形态兼容
空输出 → 空集合;标准 JSON 数组 → 正常解析;NDJSON 多行 → 正常解析;
坏行抛 `FormatException`,消息含行号与"镜像"字样(与功能 001 的容器解析行为一致)。

### AC5 · 引用(Reference)
`Reference` = `仓库名:标签`,供功能 004 的 `rmi` 使用;
`Repository` 为空或为 `<none>` 时,`IsTagged == false`,此时 `Reference` 退化为镜像 ID,
`DisplayName` 显示"无标签 · <短 ID>"。

### AC6 · 展示文本
`DisplayName` = `仓库名:标签`(有标签时);`SizeText` = 大小,`UsageText` = `被 N 个容器引用` / `未被容器引用`,`SubtleLine` 含 ID 与创建时间(摘要为 `<none>` 时不显示);
`CreatedText` 优先把 `CreatedAt` 解析成本地可读时间(解析失败原样显示)。

### AC7 · 启动即加载 / 失败不崩
页面 ViewModel 构造时自动加载一次;失败时 `HasError` 为真、`Error` 为清洗后的单句文案,
**保留上一次成功的数据**,界面不崩溃(与功能 001 一致)。

### AC8 · 搜索过滤
搜索框对 `仓库名`、`标签`、`ID` 做不区分大小写的"包含"匹配,实时过滤(与功能 001 一致)。

### AC9 · 排序
默认"被容器引用的在前",其次按仓库名、标签字母序;引用数相同不代表运行状态,只是便于清理时先看有用的。

### AC10 · 汇总
`Summary` 形如 `共 3 个镜像 · 2 个有标签 · 1 个正被容器引用`;一条都没有时显示 `本机还没有镜像`。数字来自当前 **全量** 数据
(不受搜索过滤影响,过滤影响的是列表行数)。

### AC11 · 导航
主窗口左侧出现导航栏,包含"容器""镜像"两项;切换页面不丢失各自的加载状态
(页面实例常驻,不重复创建);窗口标题保持 `WSLC Desktop`。

### AC12 · 不自动轮询
镜像变化很少,本页 **不做自动刷新**,只提供手动刷新按钮(避免无意义的进程调用)。

## 测试清单

| 测试文件 | 覆盖 |
|---|---|
| `WslcJsonParserTests` 的镜像用例 | AC2 AC3 AC4 |
| `ImageInfoTests` | AC5 AC6 |
| `ImagesServiceTests` | AC1 + 失败传播 |
| `ImagesPageViewModelTests` | AC7 AC8 AC9 AC10 AC12 |
| `RealWslcImagesIntegrationTests` | 真实 `wslc images --format json` 能解析出至少 1 条(只读,不创建不删除) |
| 夹具 `wslc-images.ndjson` | 本机真实输出 |
| 夹具 `wslc-images-untagged.ndjson` | 构造的 `<none>` 场景(按真实字段形状) |

## 验证结果

- 单元测试:`dotnet test` 110 个全部通过(功能 003 新增 50 个)。
- 真实 CLI 冒烟:`WSLC_DESKTOP_INTEGRATION=1` 时 `wslc images --format json` 解析通过(只读)。
- 界面:应用默认停在"容器"页;用 UI Automation 选中左侧"镜像"后切到镜像页(见开发备忘第 11 条),
  两条镜像的名称、大小、引用数、创建时间与 `wslc images` 表格逐条一致。
- 截图:`docs/screenshots/003-images.png`(镜像页)、`docs/screenshots/003-containers.png`
  (容器页,确认导航改造没有影响功能 001 / 002)。
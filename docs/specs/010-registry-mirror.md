# 功能 010 · 镜像加速(registry mirror)配置与拉取改名

状态:已完成(2026-10-12,含真机验证)
相关:功能 004(拉取 / 删除编排)、功能 009(设置页与持久化)

## 是什么

设置页多一张「镜像加速」卡片:填一个镜像站地址(例如 `docker.1ms.run`),
外加一个勾选项"拉取后把镜像改名回我输入的名字"(默认开着)。
之后在镜像页只写 `redis:7` 就够了,应用替你做三步:

1. 把引用改写成 `docker.1ms.run/library/redis:7`,交给 `wslc pull`;
2. 拉成功之后 `wslc tag <镜像站全名> <redis:7>`;
3. 再 `wslc rmi <镜像站全名>` 把带前缀的那个标签去掉。

界面上配两处实时说明,让人不用猜应用到底要执行什么:

- 设置页:填完地址当场显示"输入 redis:7 时,实际会拉取 …";这串不像注册地址时换成一句提醒;
- 镜像页:输入框下方只在**真的发生了改写**时多出一行"镜像源实际会拉取:…"。

## 为什么

直连 `registry-1.docker.io` 经常超时,而 `wslc` 自己**没有**镜像源开关(实测见下表)——
Docker Desktop 那个 `daemon.json → registry-mirrors` 在 wslc 上没有对应物,只能做在应用这一层。

如果偷懒只让用户自己抄全名,会留下三个问题:每次都要抄一长串前缀;
在容器页创建容器时还得把前缀再写一遍;镜像列表里躺着的永远是镜像站的名字,
而不是用户心里的那个名字。第 2、3 步的"改名"把这三个问题一起收掉:
列表、提示、后续操作全都按用户输入的那个引用说话。

## 实测到的行为(本规格的依据)

| 主题 | 实测结论 |
|---|---|
| wslc 没有镜像源开关 | `wslc settings` 读写的 `%LOCALAPPDATA%\wslc\settings.yaml` 只有 `session.*`(cpuCount / memorySize / maxStorageSize / storagePath / networkingMode / hostFileShareMode / dnsTunneling / hostLoopback / defaultBindingAddress / idleTimeout)、`credentialStore`、`experimental.portRelay`;`wslc registry` 只管登录凭据;`wslc pull` 只有 `-a` / `-q`。所以"配置 registry-mirror"只能是应用侧的引用改写(开发笔记 #26) |
| 带前缀的引用能正常拉 | `wslc pull docker.1ms.run/library/alpine:3.20` → 退出码 0、约 7.5 秒;`wslc images` 里那一行的名字就是这个全名 |
| tag 改名 | `wslc tag <镜像站全名> <短名>` → 退出码 0、**没有任何输出**;`wslc images` 变成两行,**ID 完全相同**(数据没动) |
| tag 的目标不带标签 | `wslc tag <源> busybox-tmp` → 列表里是 `busybox-tmp:latest`(功能 004 也实测过同一条) |
| rmi 去掉其中一个标签 | 输出只有 `已取消标记:`,**没有** `已删除:`;另一个标签与镜像数据都还在。别把它当成"删掉了镜像" |
| 三步顺序是硬约束 | 先 `rmi` 后 `tag` 等于真删镜像 —— tag 必须在 rmi 之前(开发笔记 #26) |
| 改名失败不等于拉取失败 | `wslc pull` 已经成功、镜像就在本机;tag 或 rmi 任一步失败时,绿色成功提示照常出现,红色错误条只补一句说明 |

## 预期行为(验收条件)

### AC1 · 镜像源地址的规范化与校验
规范化规则(`RegistryMirrorPolicy.TryNormalize`):

- 容忍用户连协议一起粘贴(`https://docker.1ms.run/`、结尾斜杠都行),协议与首尾空白被剥掉;
- 主机名不区分大小写(存进规范化结果时转小写),允许带端口(`mirror.example.com:5000`)
  和路径前缀(`/v2/private`)—— **路径部分原样保留大小写**,只有主机名转小写;
- 判"像不像注册地址":含空白、或者主机段既没有点也没有端口又不是 `localhost` → 无效;
- 无效时**不静默丢弃**:设置页在预览位置显示一句提醒,行为等同"不使用镜像源";
- 空串 = 不使用镜像源。

存盘的却是用户原样输入的那串(只去首尾空白),规范化每次使用时现算 ——
这样用户回到设置页看到的还是自己写的东西,不会被应用"改写"掉。

### AC2 · Docker Hub 的引用按镜像站改写
改写规则对齐 Docker 的引用规范化,规则集中在一处可单测的 `RegistryMirrorPolicy`:

| 用户输入 | 实际拉取 |
|---|---|
| `nginx` / `redis:7` / `busybox@sha256:…` | `<mirror>/library/nginx`、`<mirror>/library/redis:7`、`<mirror>/library/busybox@sha256:…` |
| `bitnami/redis` | `<mirror>/bitnami/redis`(用户仓库,不补 `library/`) |
| `docker.io/nginx`、`index.docker.io/library/redis:7`、`registry-1.docker.io/…` | 三种 Hub 等价写法都换掉主机名,官方库仍补 `library/` |
| `ghcr.io/owner/repo`、`localhost:5000/nginx` | 见 AC3,原样不动 |

尾巴(`:tag` / `@digest`)始终跟在最后;`localhost:5000/nginx` 里最后一个斜杠之前的冒号属于端口,
不会被误当成标签分隔符。只写了 `docker.io` 而没有任何仓库路径 → 不改写,不猜用户要拉什么。

### AC3 · 不命中规则时原样拉取
已经写了全域名的引用(非 Hub)、未配置镜像源、配置非法、以及空输入 ——
一律"原样不动":前者照抄,中两者照抄,空输入绝不启动进程(沿用功能 004 · AC2 的那句人话提示)。

### AC4 · 界面上实时说明
- 设置页:打字时只刷预览、**不落盘**(每按一个键写一次设置文件没意义);
  回车 / 焦点离开 / 离开本页三种方式都会落盘,与"选中即生效"的既有约定对齐。
- 镜像页:那行说明只在 `Rewritten == true` 时占位置;输入变化即时重算。
- 页面是缓存的(切走再回来不重建),所以设置页改完镜像源要能通知到已经存在的镜像页:
  `AppState.MirrorChanged` → 主窗口找到缓存的镜像页 → `RefreshMirrorState()` 重算文案。
  镜像页 `Loaded` 时也重算一次,兜住"在别的页面改过"的情形。

### AC5 · 拉完之后改名
只有"本次真的被改写过的引用"才做 tag + rmi;勾选项关掉时跳过改名,列表里保留带前缀的名字。
改名属于同一次拉取操作的收尾,**不放开全局互斥**,免得中途被刷新 / 删除插队。
改名成功后,`PullResult.Reference` 换成用户写的名字 —— 绿色提示按用户写的名字报,
而不是按镜像站的全名报。整个收尾之后只刷新列表一次。
取消路径不改名:半途终止不会留下不完整的镜像(功能 004 实测),此时根本没有可改名的对象。

### AC6 · 改名失败不误报成拉取失败
- tag 失败:绿色提示仍按**镜像站全名**说"已拉到本机 / 已是最新",红色错误条补
  "镜像已经拉到本机(…),但没能改名回你输入的名字:…";
- rmi 失败:新名字已经打上,绿色提示按**用户写的名字**报,红色错误条补
  "已经改名为 …,但带镜像站前缀的那个标签没能去掉:…";
- 两条补充说明的措辞都点明"镜像已在本机",不把成功说成失败,也不留下"到底删了没"的悬念;
- **补充说明必须写在刷新之后**:实测 `RefreshAsync()` 成功会把 `Error` 清成 null,
  先写 `Error` 再刷新 = 这句说明被冲掉,用户看到的是"改完名什么都没发生"。
  两件事同时出错时用 `" · "` 拼接,不互相吞。

### AC7 · 即时生效与持久化
`settings.json` 新增两个字段:`RegistryMirror`(字符串)、`RenameMirrorPull`(布尔,默认 true)。

- 改完即生效、即存盘,没有"保存"按钮;
- 老配置文件里没有这两个字段 → 走默认值(不使用镜像源 / 改名开着),照样读得动;
- `RegistryMirror` 的 setter 把 `null` 归一成空串,配置文件里写 `"RegistryMirror": null` 也不会漏进 null。

### AC8 · 不破坏功能 004 / 009 的约定
`ImagesService.PullAsync` 的语义不变(传给它的引用就是要拉的那个,服务层不掺改写规则);
`RemoveAsync` 仍坚持"一次只删一个引用";`rmi` 不带 `-f`。
001–009 的既有断言写的都是改版前的中文原句,新增文案必须中英两套键集合完全一致(由 `LocalizerTests` 钉住)。

## 测试清单

| 测试文件 | 覆盖 |
|---|---|
| `RegistryMirrorPolicyTests`(新增,58 个用例) | AC1(接受 / 拒绝 / 大小写 / 端口 / 路径前缀)+ AC2(裸名、用户仓库、三种 Hub 等价写法、tag 与 digest 尾巴、端口不是标签)+ AC3(全名不动、无镜像源时原样) |
| `ImagesPageMirrorPullTests`(新增,15 个用例) | AC4(预览文案与设置变更后的重算)+ AC5(改写 → 拉 → tag → rmi 的顺序、刷新恰好一次、勾选项关掉、非 Hub 不改名、取消不改名、进度文案用改写后的引用)+ AC6(tag 失败与 rmi 失败的两种说法) |
| `ImageActionsServiceTests`(原有 20 + 新增 8 = 28 个用例) | 新增的 `wslc tag` 调用:参数拼装、两侧去空白、空引用不启动进程、退出码非 0 抛错并带上命令名与原始输出、取消传播 |
| `SettingsStoreTests`(原有 10 + 新增 5 = 15 个用例) | AC7(两个新字段往返、老文件读默认值、`null` 归一、原样保存不做规范化) |
| 001–009 的 264 项(已有) | AC8 回归 |

## 验证结果

### 自动化(2026-10-12)

| 项目 | 结果 |
|---|---|
| `dotnet build WslcDesktop.sln` | 成功,**0 警告 0 错误** |
| `dotnet test` | **350 项全绿**(264 项为 001–009 回归,新增 86 项) |

### 真机验证(Windows 11 + 真实 wslc 3.0.1)

| 场景 | 结果 | 截图 |
|---|---|---|
| 设置页"镜像加速"卡片 | 通过:标题 / 输入框 / 预览行 / 勾选项 / 说明齐全;填 `https://Docker.1ms.run/` 时预览当场显示"输入 redis:7 时,实际会拉取 docker.1ms.run/library/redis:7"(AC1/AC4) | `docs/screenshots/010-settings-mirror.png` |
| 原样存盘 | 通过:离开页面后 `%LOCALAPPDATA%\WslcDesktop\settings.json` 出现 `"RegistryMirror": "https://Docker.1ms.run/"` 与 `"RenameMirrorPull": true` —— 带协议、原大小写(AC7) | — |
| 这串不像地址 | 通过:预览位置显示"这一串不像注册地址:应写成 registry.example.com,可以带端口或路径前缀。"(AC1) | — |
| 镜像页的实时预览 | 通过:`redis:7` → "镜像源实际会拉取:docker.1ms.run/library/redis:7";裸名 `hello-world` → "…docker.1ms.run/library/hello-world"(AC2/AC4) | `docs/screenshots/010-images-mirror-pull.png` |
| 拉取中的进度文案 | 通过:报的是改写后的引用 —— "正在拉取 docker.1ms.run/library/redis:7 · 已等待 N 秒";N 涨到 242 仍未完成,是当时 `docker.1ms.run` 拉 redis 极慢(网络侧),不是应用问题 | 同上 |
| 取消 | 通过:绿色提示"已取消拉取 docker.1ms.run/library/redis:7";`wslc images` 里没有半截镜像、没有残留的 `wslc` 进程、**没有**发出 tag / rmi(AC5 的取消路径) | — |
| 端到端改名 | 通过:`hello-world` → 拉取(本机已有,走"已是最新")→ `wslc images` 里 `docker.1ms.run/library/hello-world` 变成 `hello-world`,ID `e2ac70e7319a` 不变 → 提示按用户写的名字报"hello-world 已是最新" → 列表只刷新一次(前后都 4 行)(AC5) | — |
| 中英双语 | 通过:新增的 10 个文案键在中英两套里都存在、占位符一致 | — |

走查结束后本机状态已复原:`wslc images` 恢复成走查前那 4 个带 `docker.1ms.run/…` 前缀的镜像
(`wslc tag hello-world docker.1ms.run/library/hello-world` + `wslc rmi hello-world`,ID 不变),
`settings.json` 从临时备份还原成只有 `Theme` / `Language`,应用与多余 `wslc` 进程全部结束。

## 已知边界

- **成败主要由镜像站决定**:同一台机器上 redis 等了 242 秒还没拉完(已取消),
  `hello-world` 却秒回。应用只负责改写、执行、改名,不做超时策略 —— 等不下去由用户点"取消"。
- **改名只针对被改写过的引用**:用户本来就写全名(`docker.1ms.run/library/redis:7`)时,
  列表里保留全名,那是用户自己的选择,不是本功能的输出。
- **tag 会抢占同名镜像**:如果本机已经存在与目标同名的镜像,`wslc tag` 会把那个**已有镜像**
  改成用户输入的名字,而刚拉下来的那份则在随后的 `rmi` 里失去旧标签。语义上没错,
  但和"我刚拉了一个新版本"的直觉可能不一致。
- **中途被杀进程**可能同时留下两个名字(数据无损,ID 相同),再点一次删除即可。
- **不含登录**:需要凭据的私有镜像站得先用 `wslc registry` 登录,本功能不碰凭据。
- **只对 Docker Hub 生效**:GHCR、Quay、自建仓库等带全域名的引用按原样拉,
  这是刻意对齐 Docker `registry-mirrors` 行为的取舍。
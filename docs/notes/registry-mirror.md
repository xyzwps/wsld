# 镜像加速(registry mirror)的依据

## 为什么只能做在应用这一层

直连 `registry-1.docker.io` 经常超时,而 `wslc` 自己**没有**镜像源开关。实测(3.0.1):

| 主题 | 实测结论 |
|---|---|
| wslc 没有镜像源开关 | `wslc settings` 读写的 `%LOCALAPPDATA%\wslc\settings.yaml` 只有 `session.*`(cpuCount / memorySize / maxStorageSize / storagePath / networkingMode / hostFileShareMode / dnsTunneling / hostLoopback / defaultBindingAddress / idleTimeout)、`credentialStore`、`experimental.portRelay` 这些键;`wslc registry` 只管登录凭据;`wslc pull` 只有 `-a` / `-q` |
| 结论 | Docker Desktop 那个 `daemon.json → registry-mirrors` 在 wslc 上没有对应物,所以"配置镜像源"只能是应用侧的引用改写。另见 dev-notes 第 26 条 |

如果偷懒只让用户自己抄全名,会留下三个问题:每次都要抄一长串前缀;在容器页创建容器时还得把前缀再写一遍;
镜像列表里躺着的永远是镜像站的名字,而不是用户心里的那个名字。拉完之后"改名"把这三个问题一起收掉:
列表、提示、后续操作全都按用户输入的那个引用说话。

## 改写 + 改名这三步

1. 把引用改写成 `docker.1ms.run/library/redis:7`,交给 `wslc pull`;
2. 拉成功之后 `wslc tag <镜像站全名> <redis:7>`;
3. 再 `wslc rmi <镜像站全名>` 把带前缀的那个标签去掉。

规则对齐 Docker 的引用规范化:裸名字属于官方库要补 `library/`,`bitnami/redis` 直接接在镜像站后面,
`docker.io/…` 三种等价写法都算 Hub;已经写全域名的(`ghcr.io/…`、`localhost:5000/…`)一律不碰。
只写了 `docker.io` 而没有任何仓库路径 → 不改写,不猜用户要拉什么。
尾巴(`:tag` / `@digest`)始终跟在最后;`localhost:5000/nginx` 里最后一个斜杠之前的冒号属于端口,
不会被误当成标签分隔符。地址本身的合法性判定规则见 `settings-storage.md`。

## 实测表

| 主题 | 实测结果 |
|---|---|
| 带前缀的引用能正常拉 | `wslc pull docker.1ms.run/library/alpine:3.20` → 退出码 0、约 7.5 秒;`wslc images` 里那一行的名字就是这个全名 |
| tag 改名 | `wslc tag <镜像站全名> <短名>` → 退出码 0、**没有任何输出**;`wslc images` 变成两行,**ID 完全相同**(数据没动) |
| tag 的目标不带标签 | `wslc tag <源> busybox-tmp` → 列表里是 `busybox-tmp:latest` |
| rmi 去掉其中一个标签 | 输出只有 `已取消标记:`,**没有** `已删除:`;另一个标签与镜像数据都还在。别把它当成"删掉了镜像"(见 dev-notes 第 26 条) |
| 三步顺序是硬约束 | 先 `rmi` 后 `tag` 等于真删镜像 —— **tag 必须在 rmi 之前** |
| 改名失败不等于拉取失败 | `wslc pull` 已经成功、镜像就在本机;tag 或 rmi 任一步失败时,绿色成功提示照常出现,红色错误条只补一句说明 |
| 端到端改名 | `hello-world` → 拉取(本机已有,走"已是最新")→ `wslc images` 里 `docker.1ms.run/library/hello-world` 变成 `hello-world`,ID `e2ac70e7319a` 不变 → 提示按用户写的名字报"hello-world 已是最新" → 列表只刷新一次(前后都 4 行) |
| 取消路径 | 绿色提示"已取消拉取 docker.1ms.run/library/redis:7";`wslc images` 里没有半截镜像、没有残留的 `wslc` 进程、**没有**发出 tag / rmi |

## 改名失败的两条说法(刻意不把成功说成失败)

- tag 失败:绿色提示仍按**镜像站全名**说"已拉到本机 / 已是最新",红色错误条补
  "镜像已经拉到本机(…),但没能改名回你输入的名字:…";
- rmi 失败:新名字已经打上,绿色提示按**用户写的名字**报,红色错误条补
  "已经改名为 …,但带镜像站前缀的那个标签没能去掉:…";
- 两条补充说明都点明"镜像已在本机",不把成功说成失败,也不留下"到底删了没"的悬念;
- **补充说明必须写在列表刷新之后**:刷新成功会把错误条清空,先写再刷就等于这句被冲掉,
  用户看到的是"改完名什么都没发生"。两件事同时出错时用 `" · "` 拼接,不互相吞;
- 改名成功后 `PullResult.Reference` 换成用户写的名字 —— 提示按用户写的名字报,不按镜像站全名报。
  整个收尾之后只刷新列表一次;改名属于同一次拉取的收尾,不放开全局互斥,免得中途被刷新 / 删除插队。

## 已知边界

- **成败主要由镜像站决定**:同一台机器上 redis 等了 242 秒还没拉完(已取消),`hello-world` 却秒回。
  应用只负责改写、执行、改名,不做超时策略 —— 等不下去由用户点"取消"。
- **改名只针对本次真的被改写过的引用**:用户本来就写全名(`docker.1ms.run/library/redis:7`)时,
  列表里保留全名,那是用户自己的选择,不是本功能的输出。勾选项关掉时跳过改名,列表里保留带前缀的名字。
- **tag 会抢占同名镜像**:本机已存在与目标同名的镜像时,`wslc tag` 会把那个**已有镜像**改成用户输入的名字,
  而刚拉下来的那份在随后的 `rmi` 里失去旧标签。语义上没错,但和"我刚拉了一个新版本"的直觉可能不一致。
- **中途被杀进程**可能同时留下两个名字(数据无损,ID 相同),再点一次删除即可。
- **不含登录**:需要凭据的私有镜像站得先用 `wslc registry` 登录,本功能不碰凭据。
- **只对 Docker Hub 生效**:GHCR、Quay、自建仓库等带全域名的引用按原样拉 ——
  这是刻意对齐 Docker `registry-mirrors` 行为的取舍。

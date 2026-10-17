# registry-mirror

## Purpose
「设置」页那张「镜像加速」卡片与拉取时的引用改写: 只写 `redis:7` 就够了,应用替你把引用
改到镜像站上去拉,拉完再改名回你写的名字。两个动作本身在 `image-actions.md`(IA),
拉取途中的进度在 `pull-progress.md`(PP),两个字段怎么存盘在 `settings.md`。

### Requirement: 地址这一栏认得下用户怎么写
规范化每次使用时现算,判"像不像注册地址"的规则与 Docker 的引用规范化对齐
(依据见 `docs/notes/registry-mirror.md` 与开发备忘第 26 条: wslc 3.0.1 自己没有镜像源开关)。

#### Scenario: RM-001 连协议一起粘贴也认
- **WHEN** 在「镜像源地址」框里粘贴 `https://Docker.1ms.run/`(带协议、大写、结尾斜杠)
- **THEN** 下面那行预览读得到 "输入 redis:7 时,实际会拉取 docker.1ms.run/library/redis:7"
- **AND** 主机名部分被写成小写,协议与首尾空白不再出现在预览里

#### Scenario: RM-002 端口与路径前缀都留着
- **WHEN** 地址写成 `mirror.example.com:5000/v2/Private`
- **THEN** 预览里的实际拉取地址仍是这个主机名带端口,后面接 `/v2/Private/library/redis:7`
- **AND** 只有主机名转小写,路径前缀的大小写原样不动

#### Scenario: RM-003 不像地址时换成一句提醒
- **WHEN** 框里写的串含空格,或主机段既没有点也没有端口又不是 `localhost`(例如 `redis 镜像`)
- **THEN** 预览位置变成 "这一串不像注册地址:应写成 registry.example.com,可以带端口或路径前缀。"
- **AND** 行为等同于不使用镜像源: 「镜像」页发出去的就是用户写的那个引用,也没有那行改写说明

#### Scenario: RM-004 留空就是不启用
- **WHEN** 「镜像源地址」框是空的(卡片上写着 "镜像源地址(留空 = 不使用)")
- **THEN** 预览位置空着,「镜像」页拉 `redis:7` 时发出的就是 `pull redis:7`

#### Scenario: RM-005 存下来的还是用户写的那串
- **WHEN** 填过 `https://Docker.1ms.run/` 之后离开「设置」页,再打开「设置」页「关于」里写着的那份设置文件
- **THEN** 镜像源那一项的值原样是 `https://Docker.1ms.run/`,协议和大小写都没被改写
- **AND** 回到卡片上看见的也仍是自己写的那一串

#### Scenario: RM-006 老配置没有这两项时走默认档
- **WHEN** 一份早就在用的设置里没有镜像源地址与改名这一项
- **THEN** 「设置」页里地址框是空的、"拉取后把镜像改名回我输入的名字"这一勾是选中的
- **AND** 拉取按用户写的引用原样发出,一点都没被改写

### Requirement: 改写表说的是 Docker 那一套规矩
裸名字属于官方库要补 `library/`,用户仓库直接接在镜像站后面,已经写了全域名的一律不碰
(依据见 `docs/notes/registry-mirror.md`)。

#### Scenario: RM-007 官方库补 library
- **WHEN** 镜像源是 `docker.1ms.run`,在拉取框里写 `redis:7` 或裸名 `nginx`
- **THEN** 「镜像」页那行说明读得到 "镜像源实际会拉取:docker.1ms.run/library/redis:7"
- **AND** 写裸名 `nginx` 时同样是 `docker.1ms.run/library/nginx`

#### Scenario: RM-008 用户仓库不补 library
- **WHEN** 拉取框里写 `bitnami/redis`
- **THEN** 那行说明读得到 `docker.1ms.run/bitnami/redis`,中间没多出 `library/`

#### Scenario: RM-009 三种 Hub 等价写法都换主机名
- **WHEN** 写的是 `docker.io/nginx`、`index.docker.io/library/redis:7` 或 `registry-1.docker.io/…`
- **THEN** 预览与那行说明都把主机名换成镜像站,官方库照旧补 `library/`

#### Scenario: RM-010 摘要尾巴跟到最后
- **WHEN** 写的是 `busybox@sha256:…`
- **THEN** 改写后的引用是 `docker.1ms.run/library/busybox@sha256:…`, `@sha256:…` 仍在最末尾

#### Scenario: RM-011 已经写了域名的一律不动
- **WHEN** 写的是 `ghcr.io/owner/repo`,或自建仓库 `localhost:5000/nginx`
- **THEN** 那行改写说明不出现,发出去的就是用户写的那一条
- **AND** `localhost:5000/nginx` 里最后一个斜杠之前的冒号被当端口看,没被当成标签分隔符拆走

#### Scenario: RM-012 只写了 docker.io 不猜要拉什么
- **WHEN** 拉取框里只有 `docker.io` 这一截,后面没有任何仓库路径
- **THEN** 不改写: 那行说明不出现,发出去的还是 `pull docker.io`

### Requirement: 两处实时说明各说各的,但都跟着当前设置走
设置页打字只刷预览不落盘,镜像页那一行只在**真的发生了改写**时才占位置(依据见开发备忘第 26 条)。

#### Scenario: RM-013 打字只刷预览,三种方式才落盘
- **WHEN** 在地址框里逐字输入,一路没离开这个框
- **THEN** 预览那一行逐字跟着变,设置文件里仍是上一次落盘的内容
- **AND** 按回车、焦点离开这个框、切到别的页面 —— 三种都做这一次落盘

#### Scenario: RM-014 那一行只在改写真的发生时占位置
- **WHEN** 在「镜像」页的拉取框里交替输入 `redis:7` 与 `ghcr.io/owner/repo`
- **THEN** 前者下面多出 "镜像源实际会拉取:docker.1ms.run/library/redis:7",后者下面那一行不占位置
- **AND** 输入变化即时重算,不用点任何东西

#### Scenario: RM-015 切走再回来文案跟着最新设置
- **WHEN** 地址还空着时停在「镜像」页,拉取框里写着 `redis:7`(那一行说明不占位置)
- **THEN** 切到「设置」页填好镜像站,再切回「镜像」页,那一行立刻按新地址写
- **AND** 反过来把地址清空再切回来,那一行又不占位置了,不用重建页面也不用重开应用

### Requirement: 拉完之后把名字改回用户写的那个
三步的顺序是硬约束: 先改写引用去拉,再 `tag` 上新名字,最后 `rmi` 去掉带前缀的那个标签 ——
`wslc rmi` 对多标签镜像只取消标记,镜像数据不动,反过来先 `rmi` 就是真删(依据见开发备忘第 26 条)。

#### Scenario: RM-016 三条命令的顺序看得见
- **WHEN** 配好镜像源,在「镜像」页拉 `redis:7`
- **THEN** `wslc` 那侧依次收到 `pull docker.1ms.run/library/redis:7`、`tag docker.1ms.run/library/redis:7 redis:7`、`rmi docker.1ms.run/library/redis:7`
- **AND** 改名是同一次拉取的收尾,中途不放开互斥,也不被插进来的刷新或删除打断

#### Scenario: RM-017 只有被改写过的才改名
- **WHEN** 用户本来就写全名 `docker.1ms.run/library/redis:7`
- **THEN** 拉完之后不发 `tag`,也不发 `rmi`,列表里保留这个全名
- **AND** 提示按用户写的这一串报

#### Scenario: RM-018 勾选项关掉就保留带前缀的名字
- **WHEN** 把 "拉取后把镜像改名回我输入的名字" 这一勾关掉,再拉 `redis:7`
- **THEN** 只发 `pull`,不发 `tag` 与 `rmi`
- **AND** 列表里那一行是带镜像站前缀的名字,提示也按那个前缀名字说

#### Scenario: RM-019 成功的话按用户的名字说,列表只刷一次
- **WHEN** 改写、拉取、`tag`、`rmi` 一路都成
- **THEN** 绿色提示读得到 "已拉取 redis:7" 或 "redis:7 已是最新",不是镜像站那一长串
- **AND** `wslc` 那侧只收到一条 `images --format json`,列表里出现的是 `redis:7` 这一行

#### Scenario: RM-020 取消了就不改名
- **WHEN** 拉取途中点"取消"
- **THEN** 不发 `tag` 也不发 `rmi`,提示仍是 "已取消拉取 X"

#### Scenario: RM-021 改名没做干净不等于拉取失败
- **WHEN** `wslc pull` 已经成功,但紧接着的 `tag` 失败
- **THEN** 绿色提示照常,说的是镜像已经在本机(按镜像站全名报)
- **AND** 红色错误条补一句 "镜像已经拉到本机(…),但没能改名回你输入的名字:…"
- **AND** 这句补充说明在这一轮刷新之后写出来,不会被刷新冲成"改完名什么都没发生"

#### Scenario: RM-022 前缀那个标签没去掉也照样说清
- **WHEN** `tag` 成了,收尾的 `rmi` 失败
- **THEN** 绿色提示按用户写的名字报成功
- **AND** 红色错误条补一句 "已经改名为 redis:7,但带镜像站前缀的那个标签没能去掉:…"

#### Scenario: RM-023 两件事同时出错就两句都留
- **WHEN** 改名没做干净,同一轮里列表刷新也失败了
- **THEN** 红色错误条里两句都在,中间用 " · " 隔开,谁也没把谁吞掉

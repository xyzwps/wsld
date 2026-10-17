# wslc 镜像侧实测

依据来源:本机 WSLC 3.0.1 实测。覆盖 `images` / `pull` / `rmi` / `tag` 四条命令的外部形态,
以及由这些形态直接决定的做法。通用事实(退出码、stderr、NDJSON)在 `wslc-cli.md`。

## images 的输出形态

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

- 字段名首字母大写,`Containers` 这类数字字段是**字符串**(通用规则见 `wslc-cli.md`)。
- `ID` 是 12 位短 ID;`Digest` 常态是 `<none>`;`SharedSize` / `UniqueSize` 常态是 `N/A`。
- **同一个镜像 ID 可以有多个标签**:`wslc tag` 之后 `images --format json` 会为每个 `仓库:标签` 输出一行,
  `ID` 相同、`Containers` 相同(实测)。因此列表**按行展示,不按 ID 去重**,删除也必须按"仓库:标签"这个引用去删,
  而不是按 ID。
- **没有标签的镜像**其 `Repository` / `Tag` 都是 `<none>`(Docker 惯例),界面必须读得出"无标签"。
  无标签时能拿去操作的引用退化成镜像 ID。

## pull 的三种结果形态

| 场景 | 实测结果 |
|---|---|
| `wslc pull <新镜像>` | 退出码 0(hello-world 约 4–5 秒);stdout 为分层进度行,末尾依次是 `Digest: sha256:...`、`Status: Downloaded newer image for <ref>`、完整引用 |
| `wslc pull <已存在的镜像>` | 退出码 0,1–2 秒;`Status: Image is up to date for <ref>` |
| `wslc pull <拉不到的镜像>` | 退出码 1(约 10 秒超时);stderr `来自引擎的错误信息: Get "https://registry-1.docker.io/v2/": ...` + `错误代码： E_FAIL` |
| `wslc pull -q <ref>` | 退出码 0,stdout 只有完整引用一行,**没有 `Status:`** |

- 保留详细输出而**不用 `-q`**,是因为 `Status:` 那一行能区分"新下载的"和"本来就最新",值得给用户不同提示。
  `-q` 把这行一起抹掉了,所以不用。
- 输出里没有 `Status:` 行时(例如未来格式变化)按"不是已是最新"处理,不抛异常。
- **拉取途中终止 wslc 进程**:`wslc images` 里没有半截镜像,也没有残留的 wslc 进程。
  取消 = 结束子进程,不留垃圾,因此取消后不需要刷新列表。
- 进度输出的到达时机(普通重定向管道下 45 行全憋在进程退出那一刻)是另一件事,依据见
  `conpty-pull-progress.md`。

## rmi 的两种"删除"

| 场景 | 实测结果 |
|---|---|
| `wslc rmi <未被使用的引用>` | 退出码 0;`已取消标记: <ref>` + `已删除: sha256:...` |
| `wslc rmi <多标签镜像的某一个标签>` | 退出码 0,**只有 `已取消标记: ...`**;镜像本体仍在(其它标签行照旧列出) |
| `wslc rmi <被容器引用的镜像>` | 退出码 1;stderr `conflict: unable to remove repository reference "..." (must force) - container f47fdddf9219 is using its referenced image aaef90e06523` + `错误代码： ERROR_SHARING_VIOLATION` |
| `wslc rmi -f <被容器引用的镜像>` | **退出码 0**:`已取消标记` 两行 + `已删除: sha256:...`;容器仍留在 `ps -a` 里,但 `wslc start` 它必定失败(实测 `E_INVALIDARG`,OCI runtime create failed) |
| `wslc rmi -f <不存在的引用>` | 退出码 1;`No such image: <ref>` + `错误代码： WSLC_E_IMAGE_NOT_FOUND` |
| `wslc rmi A <不存在的> C` | 退出码 1,A 已被删除、C 没被处理 |

- **"已取消标记"不等于"已删除"**:只有 `已取消标记:` 时镜像数据一格没动,别把它当成删掉了镜像
  (镜像加速那三步的收尾正是靠这条,见 `registry-mirror.md`;dev-notes 第 26 条记过同一个坑)。
- 真机冒烟复核过:给已有镜像打临时标签再删掉这个标签 → 标签消失、镜像数据无损。
- 判定"被容器占用"靠引擎原文里的三段文本之一:`must force`、`unable to remove repository reference`、
  `ERROR_SHARING_VIOLATION`。其它错误(如 `WSLC_E_IMAGE_NOT_FOUND`)**不**算占用,
  不能顺手给用户一个破坏性选项。
- **`-f` 是真删除**:即使容器还引用着它,也会把镜像从磁盘上拿掉,留下的容器再也起不来。
  所以强制删除绝不能是默认路径,必须是"常规删除失败 → 用户第二次显式确认"。
- **批量删除遇错即停**:一条 `rmi` 里多个引用,中间一个失败,后面的根本不被处理。
  所以应用一次只删一个引用,不批量。

## tag

| 场景 | 实测结果 |
|---|---|
| `wslc tag <src> <dst>` | 退出码 0,**无任何输出** |
| `wslc tag <src> busybox-tmp`(目标不带标签) | 列表里是 `busybox-tmp:latest` |
| `wslc tag` 之后 `wslc images` | 变成两行,**ID 完全相同**(数据没动) |

静默成功意味着"打标签到底成没成"只能靠后续 `images` 的列表核对;目标不带标签时被补 `:latest`,
这是改名收尾那一步必须把目标写成用户原样引用的原因。

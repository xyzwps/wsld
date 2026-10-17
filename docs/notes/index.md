# 依据记录(按主题)

这儿放的是"这块契约为什么长这样":实测到的 wslc / WinUI 外部行为、刻意做的取舍、被推翻的假设。
`docs/specs/` 只写用户可观察的结果(WHEN/THEN),不写为什么;`docs/dev-notes.md` 只写"这样写会炸"的坑
(症状 / 根因 / 修复),继续按 `## N` 编号。同一条事实只写一处正文:dev-notes 已经记过的,这儿只写
"见 dev-notes 第 N 条",不复制。

动对应那块代码之前先扫一眼,或写册子时要用到"为什么"就引到这儿。

| 文件 | 什么时候读 |
| --- | --- |
| [`wslc-cli.md`](wslc-cli.md) | 新增一条 `wslc` 调用之前:退出码与错误流的通用形态、错误文案清洗约定、生命周期四命令的幂等、容器标识能传什么、NDJSON 通用解析假设 |
| [`wslc-images.md`](wslc-images.md) | 碰镜像列表解析、`pull` / `rmi` / `tag` 的结果判定之前:那三张实测表与真实 JSON 样例 |
| [`wslc-logs.md`](wslc-logs.md) | 碰日志读取之前:`logs` 全部选项的实测行为(空输出、非法 `-n`、`-t` 的 UTC 纳秒、`-f` 的历史冲刷、跟随结束与终止) |
| [`registry-mirror.md`](registry-mirror.md) | 动镜像加速之前:wslc 侧根本没有镜像源开关、tag→rmi 的顺序是硬约束、改名失败不等于拉取失败、四条已知边界 |
| [`conpty-pull-progress.md`](conpty-pull-progress.md) | 动拉取实时进度 / 伪终端之前:非终端为什么不流式、进度为什么是"原地回写"、分层行形态与字节进制 |
| [`winui-shell.md`](winui-shell.md) | 动顶栏、导航、图标按钮、主题换皮之前:接管标题栏那三条几何事实、主题生效范围、纯图标的强制约定 |
| [`settings-storage.md`](settings-storage.md) | 加设置字段、动读写之前:文件位置、字段清单、容错档位、"存原样、规范化用时现算"这条取舍 |
| [`i18n-keys.md`](i18n-keys.md) | 往界面里写文案之前:两套键集合一致这条纪律、缺键怎么兜、换语言为什么要整页重建、测试语言为什么要钉住 |
| [`run-command.md`](run-command.md) | 动命令编排之前:`wslc run` 的选项集合怎么对齐、`--pull` / `-d` / `--rm` 的真实语义、裸镜像名为什么不等于本机镜像 |
| [`test-verification.md`](test-verification.md) | 想知道"这块界面到底怎么验证"之前:哪些只能真机点击加截图、哪些能自动化、以及那些只能靠静态闸的视觉错 |

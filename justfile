# wsld · 开发用快捷命令
# 需要 just(https://github.com/casey/just)。最常用:just dev —— 编译并启动桌面应用。
# 小提示:just 在 Windows 上每行单独调用一次 shell,所以"上一行的变量下一行读不到";
# 需要连着写的地方一律用 "; \" 续行,把它们并成一条命令。

set windows-shell := ["powershell.exe", "-NoLogo", "-NoProfile", "-Command"]

sln   := "wsld.sln"
tests := "tests/wsld.Tests/wsld.Tests.csproj"

# 不带参数时列出所有可用命令
default:
    @just --list

# 编译整个解决方案(x64)
build:
    @dotnet build {{sln}} --nologo -v m

# 启动桌面应用:先编译,再把刚构建出来的 exe 拉起来(应用窗口独立于终端,不占着命令行)
dev: build
    @$e = Get-ChildItem src/wsld/bin -Recurse -Filter wsld.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1; \
    if ($e) { "启动:$($e.FullName)"; Start-Process -FilePath $e.FullName } else { "没找到 wsld.exe —— 先看上面的编译输出是否报错"; exit 1 }

# 跑单元测试(不依赖真实 wslc,460 项)
test:
    @dotnet test {{tests}} --nologo -v q

# 册子编号与用例对账:docs/specs 里的 Scenario 编号 ↔ 测试里的 [Trait("case","XX-001")]
# 默认"册里有号、测试没挂"只提醒;加 -Strict 连这种也算失败
casecov *args:
    @powershell -NoProfile -ExecutionPolicy Bypass -File tools\spec-casecov.ps1 {{args}}

# 端到端冒烟:要本机真的有 wslc.exe。会自建一个专用容器,跑完 启动/停止/重启/删除 后自动清理
itest:
    @$env:WSLD_INTEGRATION = "1"; \
    dotnet test {{tests}} --nologo -v q --filter FullyQualifiedName~Integration

# 结束正在运行的应用实例(编译时提示"文件被占用"就先跑这个)
kill:
    @Stop-Process -Name wsld -ErrorAction SilentlyContinue; "没有在跑的实例也算正常"

# 清理编译产物:dotnet clean + 各项目 bin\obj + artifacts\(发布目录与旧 setup.exe)
# 应用还开着会删不动,先 just kill;带 -NoDotnetClean 跳过 dotnet clean
clean *args:
    @powershell -NoProfile -ExecutionPolicy Bypass -File tools\clean.ps1 {{args}}

# 清 Windows 图标缓存:换了 assets\app.ico 之后资源管理器/快捷方式还显示旧图就是它
# 会重启资源管理器(桌面闪一下);带 -Yes 跳过确认
clean-iconcache *args:
    @powershell -NoProfile -ExecutionPolicy Bypass -File tools\clean-iconcache.ps1 {{args}}

# 需要 Inno Setup 6:winget install JRSoftware.InnoSetup
# 参数原样透传给 tools/package.ps1,例如:just package -Version 1.2.0 -Publisher "某某公司"
# 不写 -Version 就用 csproj 里的 <Version>
# 打安装包:自包含发布 + Inno Setup 编译,产物在 artifacts\installer\*.exe
package *args:
    @powershell -NoProfile -ExecutionPolicy Bypass -File tools\package.ps1 {{args}}

# 只发布(生成 artifacts\publish 目录),不打安装包
publish:
    @powershell -NoProfile -ExecutionPolicy Bypass -File tools\package.ps1 -NoInstaller

# 由母版 assets\app-source.png 重新生成应用图标 assets\app.ico(换了母版图之后跑)
icon:
    @powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-icon.ps1
# 加 -KeepInstalled 装完不卸载,方便手工点一遍界面
# 安装包自检:静默安装 -> 启动 -> 卸载 -> 查残留(只装给当前用户,不需要管理员)
verify-installer *args:
    @powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-installer.ps1 {{args}}

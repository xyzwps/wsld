# WSLC Desktop · 开发用快捷命令
# 需要 just(https://github.com/casey/just)。最常用:just dev —— 编译并启动桌面应用。
# 小提示:just 在 Windows 上每行单独调用一次 shell,所以"上一行的变量下一行读不到";
# 需要连着写的地方一律用 "; \" 续行,把它们并成一条命令。

set windows-shell := ["powershell.exe", "-NoLogo", "-NoProfile", "-Command"]

sln   := "WslcDesktop.sln"
tests := "tests/WslcDesktop.Tests/WslcDesktop.Tests.csproj"

# 不带参数时列出所有可用命令
default:
    @just --list

# 编译整个解决方案(x64)
build:
    @dotnet build {{sln}} --nologo -v m

# 启动桌面应用:先编译,再把刚构建出来的 exe 拉起来(应用窗口独立于终端,不占着命令行)
dev: build
    @$e = Get-ChildItem src/WslcDesktop/bin -Recurse -Filter WslcDesktop.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1; \
    if ($e) { "启动:$($e.FullName)"; Start-Process -FilePath $e.FullName } else { "没找到 WslcDesktop.exe —— 先看上面的编译输出是否报错"; exit 1 }

# 跑单元测试(不依赖真实 wslc,405 项)
test:
    @dotnet test {{tests}} --nologo -v q

# 端到端冒烟:要本机真的有 wslc.exe。会自建一个专用容器,跑完 启动/停止/重启/删除 后自动清理
itest:
    @$env:WSLC_DESKTOP_INTEGRATION = "1"; \
    dotnet test {{tests}} --nologo -v q --filter FullyQualifiedName~Integration

# 结束正在运行的应用实例(编译时提示"文件被占用"就先跑这个)
kill:
    @Stop-Process -Name WslcDesktop -ErrorAction SilentlyContinue; "没有在跑的实例也算正常"

# 清理编译产物(交给 dotnet 自己做,不手动删目录)
clean:
    @dotnet clean {{sln}} --nologo; "已清理编译产物"

# 打包分发(给客户的安装包)

一条命令产出一个 `setup.exe`,拷给客户双击安装即可,**客户机不需要装 .NET、不需要装 Windows App SDK 运行时**。

```powershell
just package                 # 或:powershell -File tools\package.ps1
```

产物:

| 路径 | 说明 |
|---|---|
| `artifacts\publish\` | 自包含发布目录(几百个文件,给客户看的是下面的 setup.exe,不是这个) |
| `artifacts\installer\wsld-Setup-<版本>-x64.exe` | 安装包,直接分发这一个文件 |
| `...exe.sha256` | 同内容的 SHA256,发给客户防止下载/传输被改动 |

## 命令速查

| 命令 | 做什么 |
|---|---|
| `just package` | 发布 + 补运行库 + 编译安装包,全程一条命令 |
| `just package -Version 1.2.0` | 临时换版本号出包 |
| `just package -Publisher "某某公司"` | 在"应用和功能"里写上厂商名 |
| `just package -InstallerOnly` | 跳过发布,拿现成的 `artifacts\publish` 只重编安装包(调 `.iss` 时用) |
| `just package -NoInstaller` | 只发布,不出安装包 |
| `just package -Runtime framework-dependent` | 出小包(客户机需自装 .NET 9 Desktop 运行时,一般不用) |
| `just publish` | 同上 `-NoInstaller` |
| `just icon` | 由母版 `assets\app-source.png` 重新生成应用图标 `assets\app.ico` |
| `just clean` | 清编译产物:各项目 `bin\`、`obj\` 加 `artifacts\`(发布目录与旧 setup.exe) |
| `just clean-iconcache` | 清 Windows 图标缓存 —— 换了图标后资源管理器/快捷方式还显示旧图就是它,会重启资源管理器 |
| `just verify-installer` | 出包后自检 |

## 里面装了什么

1. `dotnet publish -c Release -r win-x64 --self-contained true`
   —— .NET 9 运行时连同应用一起打进目录;`WindowsAppSDKSelfContained=true`(见
   `src/wsld/wsld.csproj`)把 WinUI 3 / Windows App SDK 也带上,所以不依赖
   客户机上的"Windows App 运行时"。
2. VC++ 运行库 DLL(`vcruntime140*.dll`、`msvcp140*.dll`、`vccorlib140.dll`)拷进应用目录。
   Windows App SDK 的原生 DLL 要用它们,而自包含发布只管 .NET 不管 CRT —— 干净的系统上少了
   这几个文件,应用会在启动前弹"缺少 vcruntime140.dll"。这里属于微软允许的应用私有部署
   (只放进应用目录,不写系统目录、不做全局安装)。
   来源优先级:`installer\prereq\crt\` → Visual Studio 的 `VC\Redist\MSVC\...\x64\Microsoft.VC*.CRT` → `C:\Windows\System32`。
   想固定用某个版本的官方 redist,把 DLL 放到 `installer\prereq\crt\` 即可。
3. Inno Setup 6 编译 `installer\wsld.iss`:LZMA2 极限压缩(约 226 MB → 约 60 MB),
   带开始菜单/桌面快捷方式、控制面板里的卸载项、"只为我安装"(无管理员权限也能装)、
   以及安装界面的中文文案(`installer\messages.zh-CN.isl`)。

## 版本号

只有 `src\wsld\wsld.csproj` 里的 `<Version>0.0.3</Version>` 一处:
它同时决定 exe 的文件版本和安装包文件名。临时改一次用 `just package -Version 1.2.0`。

## 客户机要求

- Windows 10 19041 起(安装脚本里 `MinVersion` 拦了一道),实际建议 Windows 11;
- 已安装并启用 WSL 3,能跑 `wslc`。安装结束前脚本会找 `C:\Program Files\WSL\wslc.exe`
  和 PATH,找不到只是提示一句,不拦安装 —— 应用本身还能打开,只是列不出容器。

## 出包后自测(建议每次都过一遍)

```powershell
just verify-installer                  # 装 -> 启动 -> 卸载 -> 查残留,任一项不过就报错退出
just verify-installer -KeepInstalled   # 装完不卸载,自己点一遍中文向导
```

脚本拿 `artifacts\installer` 里最新的 setup.exe,用 `/CURRENTUSER /VERYSILENT` 装到
`%LOCALAPPDATA%\Programs\wsld`(不需要管理员),确认文件数、CRT 运行库、卸载程序都在,
再启动主程序确认不闪退,然后静默卸载,确认安装目录、开始菜单快捷方式、"应用和功能"里的条目零残留。
它会在卸载前先把应用关掉 —— 带着运行中的进程卸载会留下几十个被占住的文件。

不想用脚本时的等价手工命令:

```powershell
& .\artifacts\installer\wsld-Setup-0.0.3-x64.exe `
  /CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=$env:TEMP\wslc-install.log
& "$env:LOCALAPPDATA\Programs\wsld\wsld.exe"
& "$env:LOCALAPPDATA\Programs\wsld\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

## 代码签名(现在没做)

安装包和 exe 都是未签名的,客户第一次下载会遇到 SmartScreen 的"Windows 已保护你的电脑 →
仍要运行"。要消掉这一步需要:

- 买代码签名证书(普通 OV 证书即可,想立刻不被 SmartScreen 拦要 EV 证书或走 Azure Trusted Signing),
  然后用 `signtool` 签 `wsld.exe` 与安装包;
- 或者把安装包放到客户内部的文件服务器上,并给出 `.sha256` 让客户核对。

签名步骤接到 `tools\package.ps1` 里(Inno Setup 也支持 `SignedUninstaller=yes`),目前先留空。

## 常见问题

- **`ISCC.exe` 没找到** → `winget install JRSoftware.InnoSetup`,或用 `-InnoSetup` 指到安装路径。
- **客户装完打不开,报缺少 DLL** → 看 `artifacts\publish\` 里有没有那 5 个 CRT DLL;
  没有说明本机既没 VS redist 也没 System32 里的对应文件,把官方 redist 的 DLL 放进
  `installer\prereq\crt\` 再重跑。
- **升级安装后行为怪异** → `[InstallDelete]` 会先清空安装目录,正常不会残留旧 DLL;
  若客户手动往安装目录塞过文件,那些会被删掉(设置存在 `%LOCALAPPDATA%\wsld`,卸载不动它)。
- **想发绿色版** → 直接把 `artifacts\publish\` 打 zip 给客户也能跑,只是没有快捷方式和卸载项。

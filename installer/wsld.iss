; wsld · Windows x64 安装包脚本(Inno Setup 6)
;
; 别直接跑这个文件 —— 用 `just package` 或 tools/package.ps1,它会先把应用发布成
; 自包含版本(带 .NET 运行时 + Windows App SDK),再来编译这里,产出一个 setup.exe。
; 手工编译(仅调试脚本时):
;   iscc.exe /DMyAppVersion=0.0.3 /DPublishDir=..\artifacts\publish installer\wsld.iss

; ---- 可由命令行覆盖的参数 ----
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef MyAppVersionNumber
  #define MyAppVersionNumber "0.0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define MyAppName "wsld"
#define MyAppExeName "wsld.exe"

[Languages]
; 按系统界面语言自动选:英文系统出英文向导,中文系统出简体中文向导;
; 都没命中时退回列表第一种(英文)。可用 /LANG=en 或 /LANG=zh 强制指定。
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "messages.zh-CN.isl"

[Setup]
; AppId 一旦发布就不要改:升级安装与卸载都靠它认出同一个产品。
AppId={{7498251D-DA2A-4952-85A8-71C8BB64A173}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppCopyright=Copyright 2026
; 厂商名(可选):tools/package.ps1 -Publisher "某某公司" 才会写进"应用和功能"。
#ifdef MyAppPublisher
AppPublisher={#MyAppPublisher}
#endif
VersionInfoVersion={#MyAppVersionNumber}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
OutputDir={#OutputDir}
OutputBaseFilename=wsld-Setup-{#MyAppVersion}-x64
SetupIconFile=..\assets\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 2004(19041)起;实际使用建议 Windows 11(需要 WSL 3)。
MinVersion=10.0.19041
; 允许客户在没有管理员权限的机器上选"只为我安装"。
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
ShowLanguageDialog=no

[InstallDelete]
; 自包含发布是几百个文件,升级时先清空目录,免得旧版本 DLL 残留下来互相打架。
Type: filesandordirs; Name: "{app}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "*.pdb"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// wslc.exe 在哪:默认安装位置,或 PATH 里能找到。
function WslcFound: Boolean;
var
  Rest, Dir: string;
begin
  Result := False;
  if FileExists(ExpandConstant('{commonpf}\WSL\wslc.exe')) or
     FileExists(ExpandConstant('{autopf}\WSL\wslc.exe')) then
    Result := True;
  if Result then Exit;
  Rest := GetEnv('PATH');
  while Rest <> '' do
  begin
    Dir := Rest;
    if Pos(';', Rest) > 0 then
    begin
      Dir := Copy(Rest, 1, Pos(';', Rest) - 1);
      Rest := Copy(Rest, Pos(';', Rest) + 1, Length(Rest));
    end
    else
      Rest := '';
    if (Dir <> '') and FileExists(Trim(Dir) + '\wslc.exe') then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeedsRestart := False;
  if not WslcFound then
    MsgBox('本机没有检测到 wslc.exe(通常在 C:\Program Files\WSL\ 下)。' + #13#10#13#10 +
           '安装可以继续,但应用打开后无法管理容器。' +
           '请先安装并启用 WSL 3,再运行 wsl --version 确认。', mbInformation, MB_OK);
  Result := '';
end;

; wsld · 安装界面的简体中文文案(作为 [Languages] 里独立语言注册,不是 include 片段)
; Inno Setup 6.7 官方包里没有简体中文语言文件,这里覆盖向导实际会显示的那些消息;
; 没覆盖到的(极少数内部错误文案)会退回英文。占位符([name]、%1、%n)必须原样保留。

[LangOptions]
LanguageName=简体中文
LanguageID=$0804

[Messages]
; ---- 按钮 ----
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonFinish=完成(&F)
ButtonCancel=取消
ButtonOK=确定
ButtonYes=是(&Y)
ButtonNo=否(&N)
ButtonYesToAll=全是(&A)
ButtonNoToAll=全否(&O)
ButtonBrowse=浏览(&B)...
ButtonWizardBrowse=浏览(&R)...
ButtonNewFolder=新建文件夹(&N)
ButtonStopDownload=停止下载
ButtonStopExtraction=停止解压
ClickNext=单击“下一步”继续,或单击“取消”退出安装。

; ---- 各页标题 ----
WizardSelectDir=选择安装位置
WizardSelectTasks=选择附加任务
WizardSelectProgramGroup=选择“开始”菜单文件夹
WizardReady=准备安装
WizardPreparing=正在准备安装
WizardInstalling=正在安装

; ---- 欢迎页 ----
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=即将在你的电脑上安装 [name/ver](64 位)。%n%n本应用是 WSL 容器命令行 wslc 的图形化管理工具,运行需要 Windows 11 与已安装的 WSL 3(含 wslc.exe)。%n%n建议先关闭其他程序再继续。

; ---- 安装位置 ----
SelectDirDesc=安装到哪里?
SelectDirLabel3=[name] 将安装到下面的文件夹。
SelectDirBrowseLabel=点击“下一步”继续;若想换个位置,点击“浏览”。
DiskSpaceGBLabel=当前选择至少需要 [gb] GB 磁盘空间。
DiskSpaceMBLabel=当前选择至少需要 [mb] MB 磁盘空间。

; ---- 附加任务 ----
SelectTasksDesc=还要执行哪些附加任务?
SelectTasksLabel2=选择安装 [name] 时要一并执行的附加任务,然后点击“下一步”。

; ---- 确认页 ----
ReadyLabel1=安装程序已准备就绪,可以开始安装 [name]。
ReadyLabel2a=点击“安装”继续;若想检查或修改设置,点击“上一步”。
ReadyLabel2b=点击“安装”继续。
ReadyMemoUserInfo=用户信息:
ReadyMemoDir=安装位置:
ReadyMemoType=安装类型:
ReadyMemoComponents=已选组件:
ReadyMemoGroup=“开始”菜单文件夹:
ReadyMemoTasks=附加任务:

; ---- 安装过程 ----
PreparingDesc=安装程序正在准备安装 [name]。
InstallingLabel=正在把 [name] 安装到你的电脑,请稍候...
ExtractingLabel=正在解压文件...
StopDownload=确定要停止下载吗?
StopExtraction=确定要停止解压吗?
StatusClosingApplications=正在关闭应用程序...
StatusCreateDirs=正在创建目录...
StatusExtractFiles=正在解压文件...
StatusCreateIcons=正在创建快捷方式...
StatusCreateIniEntries=正在写入 INI 项...
StatusCreateRegistryEntries=正在写入注册表...
StatusRegisterFiles=正在注册文件...
StatusSavingUninstall=正在保存卸载信息...
StatusRunProgram=正在完成安装...
StatusRestartingApplications=正在重启应用程序...
StatusRollback=正在回滚更改...

; ---- 完成页 ----
FinishedHeadingLabel=正在完成 [name] 安装向导
FinishedLabel=已在你的电脑上安装好 [name]。可以从桌面或“开始”菜单的快捷方式启动。
FinishedLabelNoIcons=已在你的电脑上安装好 [name]。
FinishedRestartLabel=要完成 [name] 的安装,必须重启电脑。要现在重启吗?
FinishedRestartMessage=要完成 [name] 的安装,必须重启电脑。%n%n要现在重启吗?
ClickFinish=点击“完成”退出安装程序。

; ---- 卸载 ----
UninstallAppTitle=卸载
UninstallAppFullTitle=%1 卸载
UninstallStatusLabel=正在从你的电脑移除 %1,请稍候...
UninstalledAll=%1 已成功从你的电脑移除。
ConfirmUninstall=确定要完全移除 %1 及其全部内容吗?
UninstallAppRunningError=卸载程序检测到 %1 正在运行。%n%n请先关闭它,然后点击“确定”继续,或点击“取消”退出。
ConfirmTitle=确认

; ---- 安装模式选择(管理员 / 仅当前用户) ----
PrivilegesRequiredOverrideTitle=选择安装模式
PrivilegesRequiredOverrideInstruction=选择安装模式
PrivilegesRequiredOverrideText1=%1 可以为所有用户安装(需要管理员权限),或只为你自己安装。
PrivilegesRequiredOverrideText2=%1 可以只为你自己安装,或为所有用户安装(需要管理员权限)。
PrivilegesRequiredOverrideAllUsers=为所有用户安装(&A)
PrivilegesRequiredOverrideAllUsersRecommended=为所有用户安装(推荐)(&A)
PrivilegesRequiredOverrideCurrentUser=只为我安装(&M)
PrivilegesRequiredOverrideCurrentUserRecommended=只为我安装(推荐)(&M)
PowerUserPrivilegesRequired=安装本程序需要以管理员账户或 Power Users 组成员身份登录。

; ---- 常见错误与冲突 ----
ErrorTitle=错误
SetupAlreadyRunning=安装程序已在运行。
SetupAborted=安装未完成。%n%n请解决问题后重新运行安装程序。
SetupAppRunningError=安装程序检测到 %1 正在运行。%n%n请先关闭它,然后点击“确定”继续,或点击“取消”退出。
ErrorCreatingDir=安装程序无法创建目录“%1”
ErrorTooManyFilesInDir=目录“%1”内文件过多,无法在其中创建文件
ErrorCloseApplications=安装程序无法自动关闭全部应用程序。建议先手动关闭正在使用待更新文件的程序,然后继续。
ErrorExecutingProgram=无法执行文件:%n%1
ErrorCopying=复制文件时出错:
ErrorReadingSource=读取源文件时出错:
ErrorChangingAttr=修改现有文件属性时出错:
ErrorCreatingTemp=在目标目录创建文件时出错:
ErrorReadingExistingDest=读取现有文件时出错:
ErrorInternal2=内部错误:%1
ErrorRegOpenKey=打开注册表项出错:%n%1\%2
ErrorRegCreateKey=创建注册表项出错:%n%1\%2
ErrorRegWriteKey=写入注册表项出错:%n%1\%2
ErrorIniEntry=在文件“%1”中创建 INI 项出错。
ErrorExtractionFailed=解压失败:%1
ErrorDownloadFailed=下载失败:%1 %2
ErrorProgress=进度值无效:%1 / %2
ErrorFileSize=文件大小无效:应为 %1,实际为 %2
FileExistsSelectAction=选择操作
FileExists2=该文件已存在。
FileExistsOverwriteExisting=覆盖现有文件(&O)
FileExistsKeepExisting=保留现有文件(&K)
FileExistsOverwriteOrKeepAll=对后续冲突同样处理(&D)
ExistingFileNewerSelectAction=选择操作
ExistingFileNewer2=现有文件比要安装的文件更新。
ExistingFileNewerOverwriteExisting=覆盖现有文件(&O)
ExistingFileNewerKeepExisting=保留现有文件(不推荐)(&K)
ExistingFileNewerOverwriteOrKeepAll=对后续冲突同样处理(&D)
RetryCancelSelectAction=选择操作
RetryCancelRetry=重试(&T)
RetryCancelCancel=取消
AbortRetryIgnoreSelectAction=选择操作
AbortRetryIgnoreRetry=重试(&T)
AbortRetryIgnoreIgnore=忽略该错误并继续(&I)
AbortRetryIgnoreCancel=取消安装
SharedFileNameLabel=文件名:
SharedFileLocationLabel=位置:
NotOnThisPlatform=本程序无法在 %1 上运行。
; ---- {cm:...} 自定义消息(任务页勾选项、完成页"现在运行"那一行) ----
[CustomMessages]
NameAndVersion=%1 版本 %2
AdditionalIcons=附加快捷方式:
CreateDesktopIcon=创建桌面快捷方式(&D)
UninstallProgram=卸载 %1
LaunchProgram=现在运行 %1
ProgramOnTheWeb=访问 %1 的网站
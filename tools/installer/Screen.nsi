; Current-user, offline Screen installer. Build with tools/build-installer.ps1.
Unicode true
ManifestSupportedOS all
ManifestDPIAware true
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
SetDatablockOptimize on
CRCCheck on
ShowInstDetails show
ShowUninstDetails show

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "WinVer.nsh"
!include "x64.nsh"
!include "nsDialogs.nsh"
!include "${PAYLOAD_INCLUDE}"

!define APP_ID "Screen.Desktop.x64"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"
!define APP_MUTEX "Local\Screen.NativeScreenshot.SingleInstance.v1"

Name "Screen ${APP_VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\Screen"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
BrandingText "Screen"
VIProductVersion "${APP_FILE_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "Screen"
VIAddVersionKey /LANG=2052 "FileDescription" "Screen 离线安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_FILE_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" ""

Var ExistingInstall
Var DesktopShortcut
Var DesktopCheckbox
Var OwnsRegistration
Var AppMutexHandle
Var InstallerMutexHandle

!define MUI_ICON "${ICON_FILE}"
!define MUI_UNICON "${ICON_FILE}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "安装 Screen"
!define MUI_WELCOMEPAGE_TEXT "安装截图、录屏和文字识别功能。所需运行组件已包含在安装包中，无需联网下载，无需管理员权限。$\r$\n$\r$\n支持 Windows 11 24H2（26100）或更新版本的 x64 系统。$\r$\n$\r$\n安装或升级前，请先保存录屏并从系统托盘退出 Screen。安装程序不会强行结束截图或录屏。"
!insertmacro MUI_PAGE_WELCOME
!define MUI_LICENSEPAGE_TEXT_TOP "以下协议仅适用于随附的 Microsoft Visual C++ 运行库。Screen 和其他开源组件的许可说明保存在安装目录的 licenses 文件夹中。"
!define MUI_LICENSEPAGE_CHECKBOX
!define MUI_LICENSEPAGE_CHECKBOX_TEXT "我接受 Microsoft Visual C++ 运行库许可协议"
!insertmacro MUI_PAGE_LICENSE "${VC_LICENSE_FILE}"
!define MUI_PAGE_CUSTOMFUNCTION_PRE DirectoryPagePre
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE DirectoryPageLeave
!insertmacro MUI_PAGE_DIRECTORY
Page custom OptionsPageCreate OptionsPageLeave
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "Screen 已安装"
!define MUI_FINISHPAGE_TEXT "可以从开始菜单或桌面启动 Screen。启动后会显示在系统托盘中。$\r$\n$\r$\n默认快捷键：$\r$\n截图：Ctrl + Shift + A$\r$\n录屏：Ctrl + Shift + R$\r$\n$\r$\n开机启动可在软件设置中自行开启。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\Screen.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动 Screen"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

!macro VisibleError Text
  DetailPrint "${Text}"
  ${IfNot} ${Silent}
    MessageBox MB_OK|MB_ICONSTOP "${Text}"
  ${EndIf}
!macroend

!macro DefineApplicationMutex Prefix
Function ${Prefix}AcquireApplicationMutex
  ; Hold the application's existing mutex during file changes. This also closes
  ; the race where the user launches Screen after the initial running check.
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "${APP_MUTEX}") p.r0 ?e'
  Pop $1
  ${If} $0 == 0
  ${OrIf} $1 == 183
    ${If} $0 != 0
      System::Call 'kernel32::CloseHandle(p r0)'
    ${EndIf}
    !insertmacro VisibleError "Screen 正在运行，或无法确认程序已退出。请先保存录屏并从系统托盘退出 Screen，然后重试。程序文件未修改。"
    SetErrorLevel 2
    Abort
  ${EndIf}
  StrCpy $AppMutexHandle $0
FunctionEnd

Function ${Prefix}ReleaseApplicationMutex
  ${If} $AppMutexHandle != 0
    System::Call 'kernel32::CloseHandle(p $AppMutexHandle)'
    StrCpy $AppMutexHandle 0
  ${EndIf}
FunctionEnd
!macroend
!insertmacro DefineApplicationMutex ""
!insertmacro DefineApplicationMutex "un."

Function .onInit
  SetShellVarContext current
  SetRegView 64
  StrCpy $AppMutexHandle 0
  StrCpy $DesktopShortcut ${BST_CHECKED}
  ${IfNot} ${IsNativeAMD64}
    !insertmacro VisibleError "此安装包仅支持 x64 系统，需要 Windows 11 24H2（26100）或更新版本。"
    SetErrorLevel 1
    Quit
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
  ${OrIfNot} ${AtLeastBuild} 26100
  ${OrIf} ${IsServerOS}
    !insertmacro VisibleError "此安装包需要 Windows 11 24H2（26100）或更新版本。"
    SetErrorLevel 1
    Quit
  ${EndIf}
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\Screen.Desktop.Installer.v1") p.r0 ?e'
  Pop $1
  ${If} $0 == 0
  ${OrIf} $1 == 183
    !insertmacro VisibleError "另一个 Screen 安装程序正在运行，请等待其结束后重试。"
    SetErrorLevel 2
    Quit
  ${EndIf}
  StrCpy $InstallerMutexHandle $0
  ; Give early feedback without holding Screen's mutex while the user reads UI.
  Call AcquireApplicationMutex
  Call ReleaseApplicationMutex
  ReadRegStr $ExistingInstall HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $ExistingInstall != ""
    StrCpy $INSTDIR $ExistingInstall
  ${EndIf}
  ${GetParameters} $0
  StrCpy $1 ""
  ${GetOptions} $0 "/DESKTOP=" $1
  ${If} $1 == "0"
    StrCpy $DesktopShortcut ${BST_UNCHECKED}
  ${EndIf}
FunctionEnd

Function DirectoryPagePre
  ${If} $ExistingInstall != ""
    StrCpy $INSTDIR $ExistingInstall
    Abort
  ${EndIf}
FunctionEnd

Function DirectoryPageLeave
  Call ValidateInstallDirectory
FunctionEnd

Function ValidateInstallDirectory
  ${If} $ExistingInstall != ""
    StrCpy $INSTDIR $ExistingInstall
  ${EndIf}
  System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r0, p 0) i.r1'
  ${If} $1 == 0
  ${OrIf} $1 >= ${NSIS_MAX_STRLEN}
    !insertmacro VisibleError "无法解析安装位置，请选择长度合适的独立文件夹。"
    SetErrorLevel 1
    Abort
  ${EndIf}
  StrCpy $INSTDIR $0
  ${GetRoot} "$INSTDIR" $0
  ${If} $INSTDIR == "$0\"
  ${OrIf} $INSTDIR == "$0"
  ${OrIf} $INSTDIR == "$PROFILE"
  ${OrIf} $INSTDIR == "$LOCALAPPDATA"
  ${OrIf} $INSTDIR == "$LOCALAPPDATA\Screen"
  ${OrIf} $INSTDIR == "$LOCALAPPDATA\Programs"
  ${OrIf} $INSTDIR == "$WINDIR"
  ${OrIf} $INSTDIR == "$SYSDIR"
  ${OrIf} $INSTDIR == "$PROGRAMFILES64"
    !insertmacro VisibleError "请选择 Screen 专用的安装文件夹，不能使用磁盘根目录、系统目录或个人设置目录。"
    SetErrorLevel 1
    Abort
  ${EndIf}
  CreateDirectory "$INSTDIR"
  ClearErrors
  GetTempFileName $0 "$INSTDIR"
  ${If} ${Errors}
    !insertmacro VisibleError "当前用户无法写入此安装位置，请选择可写文件夹。"
    SetErrorLevel 1
    Abort
  ${EndIf}
  Delete "$0"
FunctionEnd

Function OptionsPageCreate
  !insertmacro MUI_HEADER_TEXT "安装选项" "为当前 Windows 用户创建快捷方式。"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 30u "安装后可以从开始菜单启动 Screen。截图、录屏、OCR 模型和运行环境均已包含在安装包中。"
  Pop $0
  ${NSD_CreateCheckbox} 0 42u 100% 14u "创建桌面快捷方式"
  Pop $DesktopCheckbox
  ${NSD_SetState} $DesktopCheckbox $DesktopShortcut
  ${NSD_CreateLabel} 0 74u 100% 40u "开机启动由软件设置管理。升级会保留已有设置，卸载也会保留个人设置和截图、录屏文件。"
  Pop $0
  nsDialogs::Show
FunctionEnd

Function OptionsPageLeave
  ${NSD_GetState} $DesktopCheckbox $DesktopShortcut
FunctionEnd

!macro DefineLockCheck Prefix
Function ${Prefix}CheckFileUnlocked
  Exch $0
  Push $1
  IfFileExists "$0" 0 unlocked
  System::Call 'kernel32::CreateFileW(w r0, i 0xC0000000, i 0, p 0, i 3, i 0x80, p 0) p.r1'
  ${If} $1 == -1
    !insertmacro VisibleError "安装文件正在使用或无法写入。请关闭 Screen 及使用此安装目录的程序后重试。"
    SetErrorLevel 2
    Abort
  ${EndIf}
  System::Call 'kernel32::CloseHandle(p r1)'
unlocked:
  Pop $1
  Pop $0
FunctionEnd
!macroend
!insertmacro DefineLockCheck ""
!insertmacro DefineLockCheck "un."

Function MigrateExistingStartup
  ; The application owns this value. Migrate only its exact command form,
  ; preserving an existing opt-in; absence or a different command is untouched.
  ReadRegStr $0 HKCU "${RUN_KEY}" "Screen"
  ClearErrors
  StrCpy $1 $0 1
  ${If} $1 != '$\"'
    Return
  ${EndIf}
  StrCpy $1 $0 11 -11
  ${If} $1 != '$\" --startup'
    Return
  ${EndIf}
  StrLen $1 $0
  IntOp $1 $1 - 12
  ${If} $1 <= 0
    Return
  ${EndIf}
  StrCpy $2 $0 $1 1
  ${GetFileName} "$2" $3
  ${If} $3 == "Screen.exe"
    WriteRegStr HKCU "${RUN_KEY}" "Screen" '$\"$INSTDIR\Screen.exe$\" --startup'
  ${EndIf}
FunctionEnd

Section "Screen" MainSection
  Call AcquireApplicationMutex
  Call ValidateInstallDirectory
  !insertmacro CheckPayloadLocks ""
  SetOverwrite on
  ClearErrors
  !insertmacro InstallPayload
  SetOutPath "$INSTDIR"
  WriteINIStr "$INSTDIR\Screen.installation.ini" "Product" "Id" "${APP_ID}"
  WriteINIStr "$INSTDIR\Screen.installation.ini" "Product" "Version" "${APP_VERSION}"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  IfErrors install_failed

  CreateDirectory "$SMPROGRAMS\Screen"
  CreateShortcut "$SMPROGRAMS\Screen\Screen.lnk" "$INSTDIR\Screen.exe" "" "$INSTDIR\Screen.exe" 0
  CreateShortcut "$SMPROGRAMS\Screen\卸载 Screen.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0
  ${If} $DesktopShortcut == ${BST_CHECKED}
    CreateShortcut "$DESKTOP\Screen.lnk" "$INSTDIR\Screen.exe" "" "$INSTDIR\Screen.exe" 0
  ${EndIf}
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "Screen"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Screen.exe,0"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" ${INSTALLED_SIZE}
  IfErrors install_failed
  Call MigrateExistingStartup
  IfErrors install_failed
  SetErrorLevel 0
  Goto installed
install_failed:
  !insertmacro VisibleError "安装未能完成。请确认安装目录可写、磁盘空间充足，并关闭占用文件的程序后重新运行安装包。"
  Call ReleaseApplicationMutex
  SetErrorLevel 1
  Abort
installed:
SectionEnd

Function .onInstSuccess
  ; Release before the finish-page Launch action, so the new app can start.
  Call ReleaseApplicationMutex
  SetErrorLevel 0
FunctionEnd

Function .onInstFailed
  Call ReleaseApplicationMutex
  SetErrorLevel 1
FunctionEnd

Function .onGUIEnd
  Call ReleaseApplicationMutex
FunctionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  StrCpy $AppMutexHandle 0
  StrCpy $OwnsRegistration 0
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $0 == $INSTDIR
    StrCpy $OwnsRegistration 1
  ${EndIf}
  ReadINIStr $0 "$INSTDIR\Screen.installation.ini" "Product" "Id"
  ${If} $0 != "${APP_ID}"
    !insertmacro VisibleError "无法验证此目录的 Screen 安装身份，卸载已取消。"
    SetErrorLevel 1
    Quit
  ${EndIf}
  Call un.AcquireApplicationMutex
  Call un.ReleaseApplicationMutex
FunctionEnd

Section "Uninstall"
  Call un.AcquireApplicationMutex
  !insertmacro CheckPayloadLocks "un."
  ClearErrors
  !insertmacro RemovePayload
  ReadRegStr $0 HKCU "${RUN_KEY}" "Screen"
  ${If} $0 == '$\"$INSTDIR\Screen.exe$\" --startup'
    DeleteRegValue HKCU "${RUN_KEY}" "Screen"
  ${EndIf}
  ${If} $OwnsRegistration == 1
    Delete "$SMPROGRAMS\Screen\Screen.lnk"
    Delete "$SMPROGRAMS\Screen\卸载 Screen.lnk"
    RMDir "$SMPROGRAMS\Screen"
    Delete "$DESKTOP\Screen.lnk"
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
  ${EndIf}
  Delete "$INSTDIR\Screen.installation.ini"
  Delete "$INSTDIR\Uninstall.exe"
  ; Remove only an empty program directory. Never delete user settings/captures.
  RMDir "$INSTDIR"
  Call un.ReleaseApplicationMutex
  SetErrorLevel 0
  Goto uninstalled
uninstall_failed:
  !insertmacro VisibleError "部分安装文件无法删除。请关闭占用文件的程序后重试。个人设置和截图、录屏文件未删除。"
  Call un.ReleaseApplicationMutex
  SetErrorLevel 1
  Abort
uninstalled:
SectionEnd

Function un.onUninstFailed
  Call un.ReleaseApplicationMutex
  SetErrorLevel 1
FunctionEnd

Function un.onGUIEnd
  Call un.ReleaseApplicationMutex
FunctionEnd

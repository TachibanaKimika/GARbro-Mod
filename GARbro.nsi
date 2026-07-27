Unicode true
!include "MUI2.nsh"
!include "WinMessages.nsh"
!define RELEASE_DIR bin\Release
!define APP_REG_KEY "Software\Onachi\Onachi-GARbro"

Name "Onachi-GARbro"
OutFile "bin\Package\Onachi-GARbro-setup.exe"

RequestExecutionLevel admin
ShowInstDetails show
BrandingText "$(^Name)"
InstallDir "$PROGRAMFILES\$(^Name)"

Var StartMenuFolder

!define MUI_FINISHPAGE_SHOWREADME
;!define MUI_FINISHPAGE_SHOWREADME $INSTDIR\README.txt
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Create desktop shortcut"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION CreateDesktopShortCut
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_STARTMENU GARbro $StartMenuFolder
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

; Uninstaller
;!insertmacro MUI_UNPAGE_WELCOME
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_LANGUAGE "Korean"
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "Japanese"

!macro InstallSubDir dir
    CreateDirectory $INSTDIR\${dir}
    SetOutPath "$INSTDIR\${dir}"
    File "${RELEASE_DIR}\${dir}\*.*"
!macroend

!macro InstallRecursiveSubDir dir
    CreateDirectory $INSTDIR\${dir}
    SetOutPath "$INSTDIR\${dir}"
    File /r "${RELEASE_DIR}\${dir}\*.*"
!macroend

!macro CloseProcess process
    DetailPrint "Closing ${process} if it is running..."
    nsExec::ExecToLog 'taskkill /IM "${process}" /T /F'
!macroend

Function CreateDesktopShortCut
    CreateShortCut "$DESKTOP\$(^Name).lnk" "$INSTDIR\Onachi-GARbro.exe"
FunctionEnd

Function CloseRunningApplications
    !insertmacro CloseProcess "Onachi-GARbro.exe"
    !insertmacro CloseProcess "Onachi-GARbro.Cli.exe"
    !insertmacro CloseProcess "Onachi-GARbro.Console.exe"
    !insertmacro CloseProcess "Onachi-GARbro.Image.Convert.exe"
    !insertmacro CloseProcess "SchemeTool.exe"
    Sleep 1000
FunctionEnd

Function AddInstallDirToPath
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File /oname=Update-Path.ps1 "Installer\Update-Path.ps1"
    nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\Update-Path.ps1" -Action Add -Scope Machine -TargetPath "$INSTDIR"'
    Pop $0
    Pop $1
    StrCmp $0 "0" add_path_write
    StrCmp $0 "10" add_path_exists
    DetailPrint "Could not add the GARbro CLI directory to PATH: $1"
    MessageBox MB_ICONEXCLAMATION|MB_OK "GARbro was installed, but its CLI directory could not be added to the system PATH.$\r$\n$\r$\n$1"
    Return

add_path_write:
    WriteRegStr HKLM "${APP_REG_KEY}" "CliPathAdded" "$INSTDIR"
    SendMessage ${HWND_BROADCAST} ${WM_SETTINGCHANGE} 0 "STR:Environment" /TIMEOUT=5000
    DetailPrint "Added $INSTDIR to the system PATH."
    Return

add_path_exists:
    DetailPrint "$INSTDIR is already present in the system PATH."
FunctionEnd

Function un.RemoveInstallDirFromPath
    ClearErrors
    ReadRegStr $4 HKLM "${APP_REG_KEY}" "CliPathAdded"
    IfErrors remove_path_done
    StrCmp $4 "" remove_path_done

    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File /oname=Update-Path.ps1 "Installer\Update-Path.ps1"
    nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\Update-Path.ps1" -Action Remove -Scope Machine -TargetPath "$4"'
    Pop $0
    Pop $1
    StrCmp $0 "0" remove_path_cleanup
    DetailPrint "Could not remove the GARbro CLI directory from PATH: $1"
    MessageBox MB_ICONEXCLAMATION|MB_OK "GARbro was uninstalled, but its CLI directory could not be removed from the system PATH.$\r$\n$\r$\n$1"
    Return

remove_path_cleanup:
    SendMessage ${HWND_BROADCAST} ${WM_SETTINGCHANGE} 0 "STR:Environment" /TIMEOUT=5000
    DetailPrint "Removed $4 from the system PATH."
    DeleteRegValue HKLM "${APP_REG_KEY}" "CliPathAdded"
    DeleteRegKey /ifempty HKLM "${APP_REG_KEY}"
remove_path_done:
FunctionEnd

Section "Onachi-GARbro application" SEC_MAIN
    SectionIn RO
    SetOutPath $INSTDIR
    Call CloseRunningApplications

    File "${RELEASE_DIR}\*.*"
    File /oname=README.txt "README.md"
    File /oname=LICENSE.txt "LICENSE"
    File /oname=supported.html "docs\supported.html"

    !insertmacro InstallSubDir GameData
    !insertmacro InstallSubDir ja-JP
    !insertmacro InstallSubDir ko-KR
    !insertmacro InstallSubDir ru-RU
    !insertmacro InstallSubDir zh-Hans
    !insertmacro InstallSubDir zh-Hant
    !insertmacro InstallSubDir x64
    !insertmacro InstallSubDir x86
    !insertmacro InstallRecursiveSubDir Tools\KrkrDump

    SetOutPath $INSTDIR
    WriteUninstaller "$INSTDIR\uninstall.exe"

    !insertmacro MUI_STARTMENU_WRITE_BEGIN GARbro
	CreateDirectory "$SMPROGRAMS\$StartMenuFolder"
	CreateShortCut "$SMPROGRAMS\$StartMenuFolder\$(^Name).lnk" "$INSTDIR\Onachi-GARbro.exe"
	CreateShortCut "$SMPROGRAMS\$StartMenuFolder\Read me.lnk" "$INSTDIR\README.txt"
	CreateShortCut "$SMPROGRAMS\$StartMenuFolder\Supported formats.lnk" "$INSTDIR\supported.html"
	CreateShortCut "$SMPROGRAMS\$StartMenuFolder\Uninstall $(^Name).lnk" "$INSTDIR\uninstall.exe"
    !insertmacro MUI_STARTMENU_WRITE_END
SectionEnd

Section /o "Add GARbro CLI to system PATH" SEC_CLI_PATH
    Call AddInstallDirToPath
SectionEnd

Section "uninstall"
    Call un.RemoveInstallDirFromPath
    !insertmacro MUI_STARTMENU_GETFOLDER GARbro $StartMenuFolder
    Delete "$SMPROGRAMS\$StartMenuFolder\$(^Name).lnk"
    Delete "$SMPROGRAMS\$StartMenuFolder\Read me.lnk"
    Delete "$SMPROGRAMS\$StartMenuFolder\Supported formats.lnk"
    Delete "$SMPROGRAMS\$StartMenuFolder\Uninstall $(^Name).lnk"
    RMDir "$SMPROGRAMS\$StartMenuFolder"
    Delete "$DESKTOP\$(^Name).lnk"
    ClearErrors

    Delete $INSTDIR\Onachi-GARbro.exe
    Delete $INSTDIR\Onachi-GARbro.Cli.exe
    Delete $INSTDIR\Onachi-GARbro.Console.exe
    Delete $INSTDIR\Onachi-GARbro.Image.Convert.exe
    Delete $INSTDIR\*.exe.config
    Delete $INSTDIR\*.dll
    Delete $INSTDIR\*.dll.config
    Delete $INSTDIR\*.xml
    Delete $INSTDIR\README.txt
    Delete $INSTDIR\LICENSE.txt
    Delete $INSTDIR\THIRD-PARTY-NOTICES.txt
    Delete $INSTDIR\supported.html
    Delete $INSTDIR\garbro-cli-skill.zip
    RMDir /r $INSTDIR\Tools
    RMDir /r $INSTDIR\GameData
    RMDir /r $INSTDIR\ja-JP
    RMDir /r $INSTDIR\ko-KR
    RMDir /r $INSTDIR\ru-RU
    RMDir /r $INSTDIR\zh-Hans
    RMDir /r $INSTDIR\zh-Hant
    RMDir /r $INSTDIR\x64
    RMDir /r $INSTDIR\x86
    Delete $INSTDIR\uninstall.exe
    RMDir $INSTDIR
SectionEnd

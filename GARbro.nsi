Unicode true
!include "MUI2.nsh"
!define RELEASE_DIR bin\Release

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

Function CreateDesktopShortCut
    CreateShortCut "$DESKTOP\$(^Name).lnk" "$INSTDIR\Onachi-GARbro.exe"
FunctionEnd

Section "install"
    SetOutPath $INSTDIR

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

Section "uninstall"
    !insertmacro MUI_STARTMENU_GETFOLDER GARbro $StartMenuFolder
    Delete "$SMPROGRAMS\$StartMenuFolder\$(^Name).lnk"
    Delete "$SMPROGRAMS\$StartMenuFolder\Read me.lnk"
    Delete "$SMPROGRAMS\$StartMenuFolder\Supported formats.lnk"
    Delete "$SMPROGRAMS\$StartMenuFolder\Uninstall $(^Name).lnk"
    RMDir "$SMPROGRAMS\$StartMenuFolder"
    Delete "$DESKTOP\$(^Name).lnk"
    ClearErrors

    Delete $INSTDIR\Onachi-GARbro.exe
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

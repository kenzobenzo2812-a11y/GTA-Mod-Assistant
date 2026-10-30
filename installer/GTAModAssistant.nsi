; ============================================================================
; GTA Mod Assistant - NSIS Windows x64 Installer Script
; Builds a standalone Windows Setup executable (no separate .NET install needed)
; ============================================================================

!include "MUI2.nsh"

Name "GTA Mod Assistant"
OutFile "${OUTPUT_SETUP_EXE}"
Unicode True
SetCompressor /SOLID zlib

; Install per-user into C:\Users\<user>\AppData\Local\Programs\GTAModAssistant
; so no Administrator elevation is required to install or update the app itself.
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\Programs\GTAModAssistant"
InstallDirRegKey HKCU "Software\GTAModAssistant" "InstallDir"

!define MUI_ICON "${APP_ICON_PATH}"
!define MUI_UNICON "${APP_ICON_PATH}"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\GTAModAssistant.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch GTA Mod Assistant now"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Section "GTA Mod Assistant (Self-Contained Windows x64)" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"

  File /r "${PUBLISH_DIR}\*.*"

  WriteRegStr HKCU "Software\GTAModAssistant" "InstallDir" "$INSTDIR"

  ; Register in Windows Settings > Installed Apps (per-user)
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GTAModAssistant" "DisplayName" "GTA Mod Assistant"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GTAModAssistant" "DisplayIcon" "$INSTDIR\GTAModAssistant.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GTAModAssistant" "DisplayVersion" "1.1.0"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GTAModAssistant" "Publisher" "GTA Mod Assistant"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GTAModAssistant" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GTAModAssistant" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GTAModAssistant" "NoRepair" 1

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Create Start Menu & Desktop Shortcuts
  CreateDirectory "$SMPROGRAMS\GTA Mod Assistant"
  CreateShortcut "$SMPROGRAMS\GTA Mod Assistant\GTA Mod Assistant.lnk" "$INSTDIR\GTAModAssistant.exe" "" "$INSTDIR\GTAModAssistant.exe" 0
  CreateShortcut "$SMPROGRAMS\GTA Mod Assistant\Uninstall GTA Mod Assistant.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\GTA Mod Assistant.lnk" "$INSTDIR\GTAModAssistant.exe" "" "$INSTDIR\GTAModAssistant.exe" 0
SectionEnd

Section "Uninstall"
  Delete "$DESKTOP\GTA Mod Assistant.lnk"
  Delete "$SMPROGRAMS\GTA Mod Assistant\GTA Mod Assistant.lnk"
  Delete "$SMPROGRAMS\GTA Mod Assistant\Uninstall GTA Mod Assistant.lnk"
  RMDir "$SMPROGRAMS\GTA Mod Assistant"

  RMDir /r "$INSTDIR"

  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\GTAModAssistant"
  DeleteRegKey HKCU "Software\GTAModAssistant"
SectionEnd

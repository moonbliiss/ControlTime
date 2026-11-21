Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

Name "ControlTime"
OutFile "..\artifacts\ControlTime-Setup-x64.exe"
InstallDir "$PROGRAMFILES64\ControlTime"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\app\ControlTime.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Открыть родительские настройки"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Для ControlTime требуется 64-разрядная Windows."
    Abort
  ${EndIf}
  SetRegView 64
  SetShellVarContext all
  StrCpy $INSTDIR "$PROGRAMFILES64\ControlTime"
FunctionEnd

Function un.onInit
  SetRegView 64
  SetShellVarContext all
FunctionEnd

!macro StopControlTime
  ExecWait '"$SYSDIR\sc.exe" stop ControlTime' $0
  ${If} $0 == 1060
  ${OrIf} $0 == 1062
    Return
  ${EndIf}
  ${If} $0 != 0
  ${AndIf} $0 != 1061
    Abort "Не удалось остановить службу ControlTime."
  ${EndIf}

  System::Call 'advapi32::OpenSCManagerW(p 0, p 0, i 1) p.r0'
  System::Call 'advapi32::OpenServiceW(p r0, w "ControlTime", i 4) p.r1'
  System::Alloc 28
  Pop $2
  StrCpy $3 0
  loop:
    System::Call 'advapi32::QueryServiceStatus(p r1, p r2) i.r4'
    ${If} $4 == 0
      Goto cleanup
    ${EndIf}
    System::Call '*$2(i, i.r5)'
    ${If} $5 == 1
      Goto cleanup
    ${EndIf}
    Sleep 500
    IntOp $3 $3 + 1
    ${If} $3 < 40
      Goto loop
    ${EndIf}
  cleanup:
    System::Free $2
    System::Call 'advapi32::CloseServiceHandle(p r1)'
    System::Call 'advapi32::CloseServiceHandle(p r0)'
    ${If} $4 == 0
    ${OrIf} $5 != 1
      Abort "Служба ControlTime не остановилась."
    ${EndIf}
!macroend

Function StopService
  !insertmacro StopControlTime
FunctionEnd

Function un.StopService
  !insertmacro StopControlTime
FunctionEnd

Section "ControlTime"
  Call StopService
  SetOutPath "$INSTDIR\app"
  File /r /x "*.pdb" "..\artifacts\windows\*"
  SetOutPath "$INSTDIR"

  ExecWait '"$SYSDIR\sc.exe" qc ControlTime' $0
  ${If} $0 == 1060
    ExecWait '"$SYSDIR\sc.exe" create ControlTime binPath= "\$\"$INSTDIR\app\ControlTime.exe\$\" --service" start= auto DisplayName= ControlTime' $0
  ${Else}
    ExecWait '"$SYSDIR\sc.exe" config ControlTime binPath= "\$\"$INSTDIR\app\ControlTime.exe\$\" --service" start= auto' $0
  ${EndIf}
  ${If} $0 != 0
    Abort "Не удалось зарегистрировать службу ControlTime."
  ${EndIf}
  ExecWait '"$SYSDIR\sc.exe" failure ControlTime reset= 86400 actions= restart/1000/restart/1000/restart/1000' $0
  ${If} $0 != 0
    Abort "Не удалось настроить восстановление службы ControlTime."
  ${EndIf}
  ExecWait '"$SYSDIR\sc.exe" start ControlTime' $0
  ${If} $0 != 0
    Abort "Не удалось запустить службу ControlTime."
  ${EndIf}

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortCut "$SMPROGRAMS\ControlTime.lnk" "$INSTDIR\app\ControlTime.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ControlTime" "DisplayName" "ControlTime"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ControlTime" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ControlTime" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ControlTime" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Call un.StopService
  ExecWait '"$SYSDIR\sc.exe" delete ControlTime' $0
  ${If} $0 != 0
  ${AndIf} $0 != 1060
  ${AndIf} $0 != 1072
    Abort "Не удалось удалить службу ControlTime."
  ${EndIf}
  RMDir /r /REBOOTOK "$INSTDIR\app"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\settings.json"
  Delete "$INSTDIR\usage.json"
  Delete "$SMPROGRAMS\ControlTime.lnk"
  RMDir /REBOOTOK "$INSTDIR"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ControlTime"
SectionEnd

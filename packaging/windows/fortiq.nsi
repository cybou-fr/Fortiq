!ifndef VERSION
  !define VERSION "0.1.0"
!endif
!ifndef STAGE
  !define STAGE "target\dist\windows-payload"
!endif

Name "FORTIQ Node ${VERSION}"
OutFile "target\FORTIQ-Setup-${VERSION}-x64.exe"
InstallDir "$PROGRAMFILES64\FORTIQ"
RequestExecutionLevel admin
Unicode True

Page directory
Page instfiles

Section "FORTIQ Node"
  SetOutPath "$INSTDIR"
  File "${STAGE}\fortiq-service.exe"
  File "${STAGE}\fortiq.exe"
  File "${STAGE}\fortiq-desktop.exe"
  File "${STAGE}\fortiq.toml"
  File "${STAGE}\install.ps1"
  File "${STAGE}\uninstall.ps1"

  nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\install.ps1"'
  Pop $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "FORTIQ installation failed (code $0)."
    Abort
  ${EndIf}
  WriteUninstaller "$INSTDIR\Uninstall FORTIQ.exe"
SectionEnd

Section "Uninstall"
  nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\uninstall.ps1"'
  Delete "$INSTDIR\Uninstall FORTIQ.exe"
SectionEnd

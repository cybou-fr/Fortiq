Unicode true
RequestExecutionLevel admin
ManifestDPIAware true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "nsDialogs.nsh"

!ifndef VERSION
  !define VERSION "0.1.0"
!endif
!ifndef VERSION_NUM
  !define VERSION_NUM "0.1.0.0"
!endif
!ifndef DISTDIR
  !error "DISTDIR must point to the complete Windows payload"
!endif
!ifndef PACKAGE_ROLE
  !error "PACKAGE_ROLE must be Operator or Client"
!endif
!ifndef OUTDIR
  !define OUTDIR "."
!endif

!ifndef SRCDIR
  !define SRCDIR "..\.."
!endif

!define PRODUCT_NAME "FORTIQ ${PACKAGE_ROLE}"
Name "${PRODUCT_NAME}"
OutFile "${OUTDIR}\FORTIQ-${PACKAGE_ROLE}-Setup-${VERSION}-x64.exe"
InstallDir "$PROGRAMFILES64\FORTIQ"
BrandingText "FORTIQ — Assistance & Sécurité Souveraine"
VIProductVersion "${VERSION_NUM}"
VIAddVersionKey /LANG=1033 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1033 "CompanyName" "FORTIQ"
VIAddVersionKey /LANG=1033 "FileDescription" "${PRODUCT_NAME} Setup"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright 2026 FORTIQ Contributors"

!define MUI_ICON "${SRCDIR}\apps\fortiq-desktop\src-tauri\icons\icon.ico"
!define MUI_UNICON "${SRCDIR}\apps\fortiq-desktop\src-tauri\icons\icon.ico"

Var NodeName
Var NodeNameInput
Var ConfigDialog
!if "${PACKAGE_ROLE}" == "Client"
  Var OperatorPeerId
  Var OperatorPeerIdInput
!endif

!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Installation de ${PRODUCT_NAME}"
!if "${PACKAGE_ROLE}" == "Client"
  !define MUI_WELCOMEPAGE_TEXT "Bienvenue dans l'assistant d'installation de FORTIQ Client.\r\n\r\nCe logiciel permet à votre administrateur ou support informatique de vous assister à distance de manière souveraine et sécurisée (connexions chiffrées P2P, zéro port entrant ouvert).\r\n\r\nCliquez sur Suivant pour configurer et démarrer l'installation."
!else
  !define MUI_WELCOMEPAGE_TEXT "Bienvenue dans l'assistant d'installation de FORTIQ Operator.\r\n\r\nCette console d'administration souveraine vous permet de superviser votre parc de postes clients et d'ouvrir des sessions de terminal à distance sécurisées.\r\n\r\nCliquez sur Suivant pour démarrer l'installation."
!endif
!insertmacro MUI_PAGE_WELCOME
Page custom ConfigPageCreate ConfigPageLeave
!insertmacro MUI_PAGE_INSTFILES

!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!define MUI_FINISHPAGE_RUN_TEXT "Lancer FORTIQ maintenant"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchDesktopUnelevated
!define MUI_FINISHPAGE_LINK "Visiter le site officiel fortiq.fr"
!define MUI_FINISHPAGE_LINK_LOCATION "https://fortiq.fr"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "French"

Function LaunchDesktopUnelevated
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\fortiq-desktop.exe"'
FunctionEnd

Function .onInit
  ReadEnvStr $NodeName "COMPUTERNAME"
FunctionEnd

Function ConfigPageCreate
  !insertmacro MUI_HEADER_TEXT "Configuration ${PACKAGE_ROLE}" "Configurez ce poste avant l'installation."
  nsDialogs::Create 1018
  Pop $ConfigDialog
  ${If} $ConfigDialog == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 14u "Nom du poste :"
  Pop $0
  ${NSD_CreateText} 0 17u 100% 13u "$NodeName"
  Pop $NodeNameInput
  !if "${PACKAGE_ROLE}" == "Client"
    ${NSD_CreateLabel} 0 45u 100% 14u "Identifiant FORTIQ de votre support (Operator PeerId) :"
    Pop $0
    ${NSD_CreateText} 0 62u 100% 13u ""
    Pop $OperatorPeerIdInput
    ${NSD_CreateLabel} 0 82u 100% 28u "Obligatoire. L'installation est impossible sans un PeerId valide commençant par 12D3KooW."
    Pop $0
  !else
    ${NSD_CreateLabel} 0 45u 100% 38u "Ce poste sera configuré en mode OPERATOR. Son PeerId sera affiché dans FORTIQ après le premier démarrage."
    Pop $0
  !endif
  nsDialogs::Show
FunctionEnd

Function ConfigPageLeave
  ${NSD_GetText} $NodeNameInput $NodeName
  ${If} $NodeName == ""
    MessageBox MB_ICONSTOP "Le nom du poste est obligatoire."
    Abort
  ${EndIf}
  !if "${PACKAGE_ROLE}" == "Client"
    ${NSD_GetText} $OperatorPeerIdInput $OperatorPeerId
    ${If} $OperatorPeerId == ""
      MessageBox MB_ICONSTOP "L'identifiant Operator PeerId est obligatoire. Ce client ne peut pas devenir Operator par défaut."
      Abort
    ${EndIf}
    StrCpy $0 $OperatorPeerId 8
    ${If} $0 != "12D3KooW"
      MessageBox MB_ICONSTOP "Operator PeerId invalide : il doit commencer par 12D3KooW."
      Abort
    ${EndIf}
  !endif
FunctionEnd

Section "FORTIQ ${PACKAGE_ROLE}" SecMain
  SetOutPath "$TEMP\FORTIQ-${PACKAGE_ROLE}-Setup"
  File "${DISTDIR}\fortiq-service.exe"
  File "${DISTDIR}\fortiq.exe"
  File "${DISTDIR}\fortiq-desktop.exe"
  File "${DISTDIR}\install.ps1"
  File "${DISTDIR}\uninstall.ps1"
  !if "${PACKAGE_ROLE}" == "Client"
    nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$TEMP\FORTIQ-${PACKAGE_ROLE}-Setup\install.ps1" -Role Client -NodeName "$NodeName" -OperatorPeerId "$OperatorPeerId"'
  !else
    nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$TEMP\FORTIQ-${PACKAGE_ROLE}-Setup\install.ps1" -Role Operator -NodeName "$NodeName"'
  !endif
  Pop $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "L'installation FORTIQ a échoué (code $0)."
    SetErrorLevel $0
    Abort
  ${EndIf}
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall FORTIQ.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\FORTIQ" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\FORTIQ" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\FORTIQ" "Publisher" "FORTIQ"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\FORTIQ" "UninstallString" '"$INSTDIR\Uninstall FORTIQ.exe"'
  RMDir /r "$TEMP\FORTIQ-${PACKAGE_ROLE}-Setup"
  IfSilent 0 +2
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\fortiq-desktop.exe"'
SectionEnd

Section "Uninstall"
  nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\uninstall.ps1"'
  Pop $0
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\FORTIQ"
  Delete "$INSTDIR\Uninstall FORTIQ.exe"
  RMDir "$INSTDIR"
SectionEnd

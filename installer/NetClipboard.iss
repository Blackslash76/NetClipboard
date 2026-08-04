; Installer classico Windows per NetClipboard (Inno Setup 6).
; Installazione PER-UTENTE in %LocalAppData%\Programs\NetClipboard: nessun admin
; richiesto, e l'auto-update può sostituire l'eseguibile senza elevazione.
;
; Build: ISCC.exe /DMyAppVersion=1.2.3 /DSourceExe=path\NetClipboard.exe NetClipboard.iss

#define MyAppName "NetClipboard"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\src\NetClipboard\bin\Release\net9.0-windows\win-x64\publish\NetClipboard.exe"
#endif

[Setup]
AppId={{9F2C7A18-1D4B-4E2A-8C3F-2A6B5E9D0C11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Francesco Papeo
DefaultDirName={localappdata}\Programs\NetClipboard
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\NetClipboard.exe
OutputBaseFilename=NetClipboard-Setup-{#MyAppVersion}
OutputDir=.
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Avvia NetClipboard all'accesso a Windows"; GroupDescription: "Avvio automatico:"
Name: "desktopicon"; Description: "Crea un'icona sul desktop"; GroupDescription: "Icone aggiuntive:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "NetClipboard.exe"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\NetClipboard"; Filename: "{app}\NetClipboard.exe"
Name: "{userstartup}\NetClipboard"; Filename: "{app}\NetClipboard.exe"; Tasks: startup
Name: "{userdesktop}\NetClipboard"; Filename: "{app}\NetClipboard.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\NetClipboard.exe"; Description: "Avvia NetClipboard ora"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

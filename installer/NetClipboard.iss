; Installer classico Windows per NetClipboard (Inno Setup 6).
;
; Installazione PER MACCHINA in C:\Program Files\NetClipboard (in italiano Esplora
; risorse la mostra come "Programmi": il percorso vero e' quello inglese). Serve
; UAC all'installazione.
;
; Attenzione: da qui l'applicazione NON puo' piu' sostituire il proprio eseguibile
; da sola, perche' gira senza privilegi. Ci pensa la modalita' --apply-update di
; Updater, che chiede l'elevazione al momento dell'aggiornamento e poi riavvia
; l'app SENZA privilegi. Se si torna a un'installazione per-utente, quel percorso
; smette semplicemente di servire (si accorge da solo che la cartella e' scrivibile).
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
DefaultDirName={autopf}\NetClipboard
DisableProgramGroupPage=yes
SetupIconFile=..\src\NetClipboard\appicon.ico
UninstallDisplayIcon={app}\NetClipboard.exe
OutputBaseFilename=NetClipboard-Setup-{#MyAppVersion}
OutputDir=.
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

; L'eseguibile e' win-x64. Senza queste due righe Inno gira a 32 bit e {autopf}
; finirebbe in "C:\Program Files (x86)", che per un binario a 64 bit e' il posto
; sbagliato e per giunta si nota.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Avvia NetClipboard all'accesso a Windows"; GroupDescription: "Avvio automatico:"
Name: "desktopicon"; Description: "Crea un'icona sul desktop"; GroupDescription: "Icone aggiuntive:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "NetClipboard.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\NetClipboard"; Filename: "{app}\NetClipboard.exe"
Name: "{autodesktop}\NetClipboard"; Filename: "{app}\NetClipboard.exe"; Tasks: desktopicon

[Run]
; L'avvio automatico NON si fa con un collegamento. In un'installazione per macchina
; {userstartup} e' il profilo di chi ha elevato, che non e' detto sia chi usera' il
; programma — Inno stesso lo segnala. Si chiede invece all'applicazione di registrarsi
; da sola, lanciandola come l'utente vero (runasoriginaluser). Cosi' l'avvio automatico
; ha un padrone solo, HKCU\...\Run, lo stesso che usa la casella in Impostazioni: le
; due cose non possono piu' dire il contrario l'una dell'altra.
Filename: "{app}\NetClipboard.exe"; Parameters: "--set-autostart"; Tasks: startup; Flags: runhidden runasoriginaluser waituntilterminated
Filename: "{app}\NetClipboard.exe"; Description: "Avvia NetClipboard ora"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
{ Fino alla 3.0.0 l'installazione era per-utente in %LocalAppData%\Programs. Quella
  registrazione sta in HKCU e un'installazione per macchina non la vede: senza questo
  passo l'utente si ritroverebbe DUE NetClipboard installati, il vecchio ancora in
  avvio automatico. Si disinstalla il precedente prima di copiare il nuovo.

  Se l'elevazione e' avvenuta con le credenziali di un altro account, HKCU e' quello
  dell'amministratore e qui non si trova niente: in quel caso la vecchia copia resta,
  e non c'e' modo di accorgersene da qui. }

const
  PerUserUninstallKey =
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F2C7A18-1D4B-4E2A-8C3F-2A6B5E9D0C11}_is1';

function PreviousPerUserUninstaller(): String;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, PerUserUninstallKey, 'UninstallString', Result) then
    Result := '';
end;

procedure RemovePreviousPerUserInstall();
var
  Cmd: String;
  ResultCode: Integer;
begin
  Cmd := RemoveQuotes(PreviousPerUserUninstaller());
  if Cmd = '' then
    Exit;
  if not FileExists(Cmd) then
    Exit;
  Exec(Cmd, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    RemovePreviousPerUserInstall();
end;

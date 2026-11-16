; Installer for NDS Station (Inno Setup 6). Built by build.ps1 -Installer, which passes
; the version and the folder produced by dotnet publish:
;   ISCC.exe /DAppVersion=2.0.2 /DSourceDir=..\dist\NDSStation installer\NDSStation.iss

#ifndef AppVersion
  #define AppVersion "2.0.2"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\NDSStation"
#endif

#define AppName "NDS Station"
#define AppExe "NdsStation.exe"
#define AppPublisher "Federico Castellani"
#define FirewallRule "NDS Station (rete locale)"
#define AppUrl "https://github.com/federico-castellani/nds-server-emulator-desktop"

[Setup]
; Keep AppId stable forever: it is how upgrades find the previous installation.
AppId={{6B1E4C52-3F0A-4D7E-9B8C-5A2F1D9E7C34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
VersionInfoVersion={#AppVersion}
; Per-user install by default, no administrator rights needed; the user can still
; choose "all users" in the first page.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809, the minimum supported by the Windows App SDK.
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=NDSStation-Setup-{#AppVersion}
SetupIconFile=..\src\NdsStation\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
; Close a running copy through the Restart Manager instead of failing on locked files.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
italian.NpcapTitle=Connessione diretta per il gioco online (facoltativa)
italian.NpcapInfo=Con Npcap e un cavo Ethernet la console emulata si collega alla rete come un DS vero. Senza Npcap NDS Station usa la rete integrata, che funziona ovunque.
italian.OpenNpcap=Apri la pagina di download di Npcap
italian.DeleteData=Vuoi eliminare anche i dati di NDS Station su questo PC?%n%nVerranno cancellati ROM in cache, copie locali dei salvataggi, stati salvati e impostazioni. I salvataggi già sincronizzati restano sul server.%n%nAttenzione: un salvataggio non ancora inviato al server andrebbe perso.
italian.FirewallTask=Consenti lotte e scambi con altri PC della rete locale (regola firewall: UDP 28950, solo rete locale)
italian.FirewallGroup=Gioco online:
english.FirewallTask=Allow battles and trades with other PCs on the local network (firewall rule: UDP 28950, local subnet only)
english.FirewallGroup=Online play:
english.NpcapTitle=Direct connection for online play (optional)
english.NpcapInfo=With Npcap and an Ethernet cable the emulated console joins your network like a real DS. Without Npcap NDS Station uses its built-in network, which works everywhere.
english.OpenNpcap=Open the Npcap download page
english.DeleteData=Do you also want to delete NDS Station data on this PC?%n%nCached ROMs, local save copies, save states and settings will be removed. Saves already synced stay on the server.%n%nWarning: a save not yet sent to the server would be lost.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
; Creating firewall rules needs administrator rights, i.e. an "all users" install.
Name: "firewall"; Description: "{cm:FirewallTask}"; GroupDescription: "{cm:FirewallGroup}"; Check: IsAdminInstallMode

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\external\melonDS\LICENSE"; DestDir: "{app}\licenses"; DestName: "melonDS-LICENSE.txt"; Flags: ignoreversion

[InstallDelete]
; Drop files left by an older version so stale DLLs never shadow new ones. Only when the
; folder really holds a previous NDS Station, never in a folder chosen by mistake.
Type: filesandordirs; Name: "{app}\*"; Check: IsUpgradeOfThisApp

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow program=""{app}\{#AppExe}"" protocol=UDP localport=28950 remoteip=localsubnet profile=any enable=yes"; Flags: runhidden; Tasks: firewall
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
Filename: "https://npcap.com/#download"; Description: "{cm:OpenNpcap}"; Flags: shellexec postinstall skipifsilent unchecked; Check: not NpcapInstalled

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"; Check: IsAdminInstallMode

[Code]
function IsUpgradeOfThisApp: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\{#AppExe}')) and
            FileExists(ExpandConstant('{app}\ndscore.dll'));
end;

function NpcapInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\Npcap\wpcap.dll'));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // Explain the optional Npcap checkbox on the final page.
  if (CurPageID = wpFinished) and not NpcapInstalled then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      CustomMessage('NpcapTitle') + ': ' + CustomMessage('NpcapInfo');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{localappdata}\NDSStation');
  if not DirExists(DataDir) or UninstallSilent then Exit;
  if MsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;

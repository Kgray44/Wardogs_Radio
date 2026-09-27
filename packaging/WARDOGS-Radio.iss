#ifndef MyAppVersion
  #error MyAppVersion must be supplied by build-installer.ps1
#endif
#ifndef SourceDir
  #error SourceDir must be supplied by build-installer.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by build-installer.ps1
#endif
#ifndef IconFile
  #error IconFile must be supplied by build-installer.ps1
#endif

[Setup]
AppId={{D98F98D6-5A4C-4B2F-BB79-2211BBE4A5D7}
AppName=WARDOGS Radio
AppVersion={#MyAppVersion}
AppPublisher=Kgray44
DefaultDirName={localappdata}\Programs\WARDOGS Radio
DefaultGroupName=WARDOGS Radio
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=WARDOGS-Radio-Setup-v{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
SetupIconFile={#IconFile}
UninstallDisplayName=WARDOGS Radio
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Prerequisites\*"
Source: "{#SourceDir}\Prerequisites\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: ShouldInstallWebView2
Source: "{#SourceDir}\Prerequisites\VoicemeeterBananaSetup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: ShouldInstallVoicemeeterBanana

[Icons]
Name: "{autoprograms}\WARDOGS Radio"; Filename: "{app}\WARDOGS Radio Launcher.exe"
Name: "{autodesktop}\WARDOGS Radio"; Filename: "{app}\WARDOGS Radio Launcher.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\WARDOGS Radio Launcher.exe"; Description: "Launch WARDOGS Radio"; Flags: nowait postinstall skipifsilent

[Code]
var
  PrerequisitePage: TInputOptionWizardPage;
  WebView2Missing: Boolean;
  VoicemeeterBananaMissing: Boolean;

function IsWebView2Installed: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

function IsVoicemeeterBananaInstalled: Boolean;
begin
  Result :=
    FileExists(ExpandConstant('{pf32}\VB\Voicemeeter\voicemeeterpro_x64.exe')) or
    FileExists(ExpandConstant('{pf}\VB\Voicemeeter\voicemeeterpro_x64.exe')) or
    FileExists(ExpandConstant('{pf32}\VB\Voicemeeter\voicemeeter8x64.exe')) or
    FileExists(ExpandConstant('{pf}\VB\Voicemeeter\voicemeeter8x64.exe'));
end;

procedure InitializeWizard;
begin
  WebView2Missing := not IsWebView2Installed;
  VoicemeeterBananaMissing := not IsVoicemeeterBananaInstalled;
  PrerequisitePage := CreateInputOptionPage(wpSelectDir,
    'WARDOGS Radio prerequisites',
    'Install missing dependencies',
    'WARDOGS Radio bundles these official prerequisites. Leave missing items selected for the full supported experience. ' +
    'If you uncheck a missing item, the application installs, but the related feature will not work. Voicemeeter Banana installs an audio driver and requires administrator approval and a Windows restart.',
    False, False);
  if WebView2Missing then
    PrerequisitePage.Add('Install Microsoft Edge WebView2 Runtime (needed for YouTube and web playback)')
  else
    PrerequisitePage.Add('Microsoft Edge WebView2 Runtime is already installed (no action required)');
  if VoicemeeterBananaMissing then
    PrerequisitePage.Add('Install Voicemeeter Banana (needed for supported game/music routing)')
  else
    PrerequisitePage.Add('Voicemeeter Banana or Potato is already installed (no action required)');
  PrerequisitePage.Values[0] := WebView2Missing;
  PrerequisitePage.Values[1] := VoicemeeterBananaMissing;
end;

function ShouldInstallWebView2: Boolean;
begin
  Result := (not IsWebView2Installed) and PrerequisitePage.Values[0];
end;

function ShouldInstallVoicemeeterBanana: Boolean;
begin
  Result := (not IsVoicemeeterBananaInstalled) and PrerequisitePage.Values[1];
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = PrerequisitePage.ID) and
     ((WebView2Missing and (not PrerequisitePage.Values[0])) or
      (VoicemeeterBananaMissing and (not PrerequisitePage.Values[1]))) then
    Result := MsgBox('One or more required WARDOGS Radio prerequisites are missing and unchecked. ' +
      'WARDOGS Radio will install, but web playback or supported Voicemeeter routing will not work until the prerequisite is installed. Continue anyway?',
      mbConfirmation, MB_YESNO) = IDYES;
end;

procedure InstallPrerequisites;
var
  ResultCode: Integer;
begin
  if ShouldInstallWebView2 then begin
    WizardForm.StatusLabel.Caption := 'Installing Microsoft Edge WebView2 Runtime...';
    if (not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
      MsgBox('Microsoft Edge WebView2 Runtime did not install. You can install it later from Microsoft, but YouTube and web playback will remain unavailable until then.', mbError, MB_OK);
  end;
  if ShouldInstallVoicemeeterBanana then begin
    WizardForm.StatusLabel.Caption := 'Opening the official Voicemeeter Banana installer...';
    if not ShellExec('runas', ExpandConstant('{tmp}\VoicemeeterBananaSetup.exe'), '', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
      MsgBox('Voicemeeter Banana was not installed. Administrator approval and a Windows restart are required before supported routing can work.', mbError, MB_OK)
    else if ResultCode <> 0 then
      MsgBox('The Voicemeeter Banana installer returned an error. Complete the official setup and restart Windows before configuring routing in WARDOGS Radio.', mbError, MB_OK)
    else
      MsgBox('Voicemeeter Banana setup completed. Restart Windows before configuring WARDOGS Radio routing.', mbInformation, MB_OK);
  end;
end;

procedure CurStep(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then InstallPrerequisites;
end;

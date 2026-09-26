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
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\WARDOGS Radio"; Filename: "{app}\WARDOGS Radio Launcher.exe"
Name: "{autodesktop}\WARDOGS Radio"; Filename: "{app}\WARDOGS Radio Launcher.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\WARDOGS Radio Launcher.exe"; Description: "Launch WARDOGS Radio"; Flags: nowait postinstall skipifsilent

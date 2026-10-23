#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#ifndef AppVersion
  #error Pass /DAppVersion with the published application version.
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{8DB3F083-A838-4942-8627-92F29511CF4E}
AppName=Senda
AppVersion={#AppVersion}
AppPublisher=Senda
DefaultDirName={localappdata}\Programs\Senda
DefaultGroupName=Senda
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=Senda-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Senda.exe
CloseApplications=yes
RestartApplications=no
AppMutex=Local\Senda.Dictation.Desktop
AllowNoIcons=yes

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
spanish.OpenSenda=Abrir Senda
english.OpenSenda=Open Senda

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Senda"; Filename: "{app}\Senda.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Senda"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Senda.exe"; Description: "{cm:OpenSenda}"; Flags: nowait postinstall skipifsilent

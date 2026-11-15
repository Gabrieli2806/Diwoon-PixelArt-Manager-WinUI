; Installer for the unpackaged build. Built by GitHub Actions:
;   iscc /DAppVersion=1.2.3 /DSourceDir=..\publish\win-x64 /DOutputDir=..\release installer\DiwoonPixelArtManager.iss
; Installs for the current user only (no administrator rights needed).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\release"
#endif

#define AppName "Diwoon PixelArt Manager"
#define AppExe "DiwoonPixelArtManager.exe"

[Setup]
AppId={{7E3C9B1A-52D4-4E0B-9C6F-5A2B8D1E4F70}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Gabrieli2806
AppPublisherURL=https://github.com/Gabrieli2806/Diwoon-PixelArt-Manager-WinUI
AppSupportURL=https://discord.gg/DhBbAzugZ9
DefaultDirName={localappdata}\Programs\Diwoon PixelArt Manager
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=DiwoonPixelArtManager-{#AppVersion}-Setup-x64
SetupIconFile=..\DiwoonPixelArtManager\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "StopApp"

[Registry]
; The app writes this Run entry itself when "Start with Windows" is on; uninstalling removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "DiwoonPixelArtManager"; Flags: uninsdeletevalue

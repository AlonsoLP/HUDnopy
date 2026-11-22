; -----------------------------------------------------------------------------
; Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
; File:        installer/HUDnopy.iss
; Author:      AlonsoLP
; Copyright:   Copyright (c) 2026 AlonsoLP
; License:     GPL-3.0-only (see LICENSE file in the repository root)
; Description: Inno Setup 6 per-user Windows x64 installer script for HUDnopy.
; -----------------------------------------------------------------------------

#ifndef MyAppVersion
  #define MyAppVersion "0.3.0"
#endif

#ifndef MyAppTag
  #define MyAppTag "v" + MyAppVersion
#endif

#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif

#define MyAppName "HUDnopy"
#define MyAppPublisher "AlonsoLP"
#define MyAppURL "https://github.com/AlonsoLP/HUDnopy"
#define MyAppExeName "HUDnopy.exe"

[Setup]
AppId={{8D6B4F39-7E12-4A4E-B7F9-2B9E44C3A108}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
; Per-user installation (%LocalAppData%\Programs\HUDnopy) avoids UAC prompts
; and ensures config.json and web-certificate.dat remain writable next to the executable.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=..\LICENSE
SetupIconFile=..\HUDnopy.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=Output
OutputBaseFilename={#MyAppName}-{#MyAppTag}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

; PCTransfer11 - Inno Setup installatiescript
; --------------------------------------------
; Bouwt een setup.exe rondom de al gepubliceerde portable build (map "publish"
; naast dit script, gemaakt door "dotnet publish ... -o publish").
;
; Lokaal bouwen:
;   1. Publiceer eerst de portable versie, bv. vanuit de projectmap:
;        dotnet publish PCTransfer11\PCTransfer11.csproj -c Release -r win-x64 ^
;          --self-contained true -p:PublishSingleFile=true -o publish
;   2. Installeer Inno Setup (gratis): https://jrsoftware.org/isinfo.php
;   3. Open dit bestand in Inno Setup en klik op "Compile", of via de
;      command line:
;        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\setup.iss
;   4. Het resultaat staat in installer\Output\PCTransfer11-Setup.exe
;
; De portable versie ("publish"-map / zip) blijft gewoon apart bruikbaar
; naast deze installer - dit script kopieert alleen de inhoud, het verandert
; er niets aan.

#define MyAppName "PCTransfer11"
#define MyAppVersion "1.2.0"
#define MyAppPublisher "Darkerst Inc."
#define MyAppURL "https://fcc.nu"
#define MyAppExeName "PCTransfer11.exe"

[Setup]
AppId={{6C6E9B7A-6E7C-4C9B-9B0E-2F6D3E8B5A11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=PCTransfer11-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Draait bewust zonder verplichte adminrechten - de app zelf heeft die ook
; niet nodig (asInvoker, zie app.manifest). "lowest" laat de gebruiker zelf
; kiezen tussen per-gebruiker en "Alle gebruikers" (dan wél met adminprompt).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; Verwijder de regel hieronder als je Inno Setup-installatie geen Nederlands
; taalbestand heeft (dan volstaat Engels als enige taal).
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

[Tasks]
Name: "desktopicon"; Description: "Snelkoppeling op bureaublad plaatsen"; GroupDescription: "Extra snelkoppelingen:"; Flags: unchecked

[Files]
; Neemt de volledige inhoud van de "publish"-map mee (self-contained
; single-file .exe, dus meestal is dit maar één of enkele bestanden).
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

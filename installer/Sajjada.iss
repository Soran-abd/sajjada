; Installer for Sajjada, built with Inno Setup 6:
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\Sajjada.iss
; Installs for the current user only (no administrator rights), adds a Start menu
; shortcut, optionally starts with Windows and a desktop shortcut, and registers an uninstaller.

#define AppName "سجادة"
#define AppExe "Sajjada.exe"
#define AppVersion "1.0.1"

[Setup]
; Fixed ID so later versions install over this one instead of beside it.
AppId={{6F1D3A52-8C4B-4E7A-9B2D-5A1C0E9F7B31}
AppName={#AppName}
AppVerName={#AppName} {#AppVersion}
AppVersion={#AppVersion}
AppPublisher=Sajjada
AppComments=سجادة للزينة على سطح المكتب تُخفي الأيقونات تحتها دون نقل الملفات. مبنية على Kilim (MIT).
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Sajjada
VersionInfoDescription=Sajjada Setup

; Current user only: %LOCALAPPDATA%\Programs\Sajjada, no UAC prompt.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Sajjada
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; The app holds this mutex while running, so setup and uninstall ask to close it first.
AppMutex=Sajjada.SingleInstance
CloseApplications=yes

SetupIconFile=..\src\Sajjada\Assets\Sajjada.ico
; The pictures inside the installer window: the first rug, Pebbles (tools\make_wizard_images.ps1).
WizardImageFile=wizard-164.bmp,wizard-246.bmp,wizard-328.bmp
WizardSmallImageFile=wizard-small-55.bmp,wizard-small-83.bmp,wizard-small-110.bmp
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} (Sajjada)
WizardStyle=modern
; The license agreement (Arabic, with the MIT text), accepted before anything is installed.
LicenseFile=license-ar.txt
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

Compression=lzma2/max
SolidCompression=yes
OutputDir=Output
; The download page links to this exact name, so it stays the same for every version.
OutputBaseFilename=Sajjada-Setup

[Languages]
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[Tasks]
Name: "startup"; Description: "تشغيل سجادة تلقائياً مع بدء Windows"; GroupDescription: "خيارات إضافية:"
Name: "desktopicon"; Description: "إنشاء اختصار على سطح المكتب"; GroupDescription: "خيارات إضافية:"; Flags: unchecked

[Files]
Source: "..\out\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\إلغاء تثبيت {#AppName}"; Filename: "{uninstallexe}"
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startup
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "تشغيل سجادة الآن"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The rug's own settings (position, size, pattern). Desktop icons are never touched:
; any that lay under the rug simply show again where they are.
Type: filesandordirs; Name: "{localappdata}\Sajjada"
; The start-with-Windows shortcut, also when it was switched on from the app's menu.
Type: files; Name: "{userstartup}\{#AppName}.lnk"

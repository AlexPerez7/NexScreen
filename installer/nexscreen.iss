; Instalador de NexScreen (Inno Setup 6). Se compila con installer\build.ps1, que define
; AppVersion, SourceDir (carpeta armada con la app, tools, driver y licencias) y OutputDir.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "staging\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "NexScreen"
#define AppExe "NexScreen.exe"
#define DriverLog "{commonappdata}\NexScreen\install-virtual-display.log"

[Setup]
; El AppId identifica a NexScreen para actualizar y desinstalar: no cambiarlo nunca.
AppId={{91BEACBA-8553-425E-A16C-98A04B2B703A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=NexScreen
AppPublisherURL=https://github.com/AlexPerez7/NexScreen
AppSupportURL=https://github.com/AlexPerez7/NexScreen/issues
AppUpdatesURL=https://github.com/AlexPerez7/NexScreen/releases
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
; El monitor virtual y el firewall necesitan permisos de administrador.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=NexScreen-Setup-{#AppVersion}
SetupIconFile=..\host\src\NexScreen.Tray\Assets\nexscreen.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Cierra NexScreen si está abierto al actualizar o desinstalar.
CloseApplications=force
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "virtualdisplay"; Description: "Instalar el monitor virtual (para usar la tablet como monitor extendido)"
Name: "firewall"; Description: "Permitir conexiones por WiFi en el Firewall de Windows (solo redes privadas)"
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\driver\install-virtual-display.ps1"" -DriverZip ""{app}\driver\VirtualDisplayDriver.zip"" -NefconZip ""{app}\driver\nefcon.zip"" -LogPath ""{#DriverLog}"""; \
  StatusMsg: "Instalando el monitor virtual..."; Tasks: virtualdisplay; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\driver\allow-firewall.ps1"""; \
  StatusMsg: "Configurando el Firewall de Windows..."; Tasks: firewall; Flags: runhidden waituntilterminated
; Se abre como el usuario que inició la instalación (no como el administrador que la autorizó),
; así NexScreen queda en su sesión y se configura para iniciar con su Windows.
Filename: "{app}\{#AppExe}"; Description: "Abrir NexScreen"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "CloseApp"
Filename: "taskkill.exe"; Parameters: "/F /IM adb.exe"; Flags: runhidden; RunOnceId: "CloseAdb"
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\driver\allow-firewall.ps1"" -Remove"; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewall"
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\driver\install-virtual-display.ps1"" -Uninstall -NefconZip ""{app}\driver\nefcon.zip"" -LogPath ""{#DriverLog}"""; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveVirtualDisplay"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\tools"

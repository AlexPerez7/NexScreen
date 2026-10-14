# CLAUDE.md

Guía para trabajar en NexScreen con Claude Code.

## Idioma

- **Todo en español neutro, tratando de "tú". Nunca voseo** (nada de "usá", "ejecutá", "podés",
  "tenés", "movelo"; sí "usa", "ejecuta", "puedes", "tienes", "muévelo").
- Aplica a respuestas en el chat, comentarios de código, mensajes de log, textos de la app
  (`strings.xml`), scripts, README, docs y mensajes de commit.
- Los identificadores de código (clases, métodos, variables) van en inglés.

## Qué es

NexScreen convierte una tablet Android (desarrollado con una Samsung Galaxy Tab S9 FE,
2304×1440, Android 16) en un monitor extendido de un PC con Windows, por USB o WiFi.

| Fase | Contenido | Estado |
|------|-----------|--------|
| 1 | Espejo de un monitor → tablet | ✅ |
| 2 | Monitor virtual para extender el escritorio | ✅ |
| 3 | Táctil y S Pen (presión) hacia Windows | ✅ |
| 4 | App de bandeja, autoconexión, instalador, releases | ✅ |
| 5 | Actualización automática, TLS y emparejamiento por WiFi (protocolo v2) | ✅ |

Ideas para próximas versiones, priorizadas y con notas técnicas: `PLAN.md`.

## Estructura

```
host/Directory.Build.props    Versión de NexScreen (única fuente) y configuración común
host/src/NexScreen.Core/      Lógica del host (librería, net8.0-windows)
  Protocol/                   Framing y mensajes (Wire.cs)
  Video/                      ffmpeg (ddagrab + h264_qsv/x264) -> FLV -> Annex-B
  Net/                        StreamServer, ClientSession, IStreamObserver (eventos para la UI)
  Display/                    Monitor virtual: API CCD, GDI, DXGI, driver VDD
  Input/                      Táctil y lápiz: mensaje POINTER, traductor de frames, inyección Win32
  Security/                   WiFi: certificado del host, PIN de emparejamiento, tablets emparejadas
  Update/                     Actualización automática: GitHub Releases, descarga y verificación
  HostStartup.cs              Preparación común (ffmpeg, modo, limpieza del monitor virtual)
  ToolLocator.cs              ffmpeg/adb en tools\ junto al exe (instalador) o en el PATH
host/src/NexScreen.Tray/      App de bandeja (WinForms) -> NexScreen.exe. Uso diario.
host/src/NexScreen.Host/      Consola de desarrollo y diagnóstico -> nexscreen-host.exe
host/tests/NexScreen.Core.Tests/  xUnit
host/tools/fake_client.py     Cliente que simula la tablet (prueba sin hardware)
host/tools/make-icon.ps1      Regenera Assets/nexscreen.ico
android/app/                  App Kotlin (paquete dev.nexscreen.client)
scripts/                      install-virtual-display.ps1, usb-connect.ps1, allow-firewall.ps1
docs/protocol.md              Especificación del protocolo
```

## Comandos

En este PC `dotnet` y `adb` pueden no estar en el PATH: usar rutas completas.

```powershell
# Host: compilar todo y probar (dotnet test solo compila los tests y Core, no la bandeja)
& "C:\Program Files\dotnet\dotnet.exe" build host
& "C:\Program Files\dotnet\dotnet.exe" test host

# App de bandeja (log en %LOCALAPPDATA%\NexScreen\logs\nexscreen.log,
# preferencias en %APPDATA%\NexScreen\settings.json)
Start-Process host\src\NexScreen.Tray\bin\Debug\net8.0-windows\NexScreen.exe

# App: compilar (el JDK es la JBR de Android Studio, Java 25)
$env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"
android\gradlew.bat -p android assembleDebug

# Instalar en la tablet y abrir el túnel USB
$adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
& $adb install -r android\app\build\outputs\apk\debug\app-debug.apk
& $adb reverse tcp:27190 tcp:27190
& $adb shell am start -n dev.nexscreen.client/.MainActivity

# Diagnóstico de pantallas (GDI, caminos CCD y salidas DXGI)
host\src\NexScreen.Host\bin\Debug\net8.0-windows\nexscreen-host.exe --list-displays
```

## Cómo probar

1. `dotnet test host`: debe pasar todo.
2. **Sin tablet:** lanzar el host con `Start-Process` (ver abajo) y ejecutar
   `python -I host\tools\fake_client.py --seconds 6 --out <scratchpad>\capture.h264`, luego
   validar con `ffprobe`. En modo extendido el video debe salir a 2304×1440.
3. **Con tablet:** lanzar el host en una ventana visible para que el usuario vea el log:
   ```powershell
   Start-Process powershell -WorkingDirectory "C:\Python Scripts\NexScreen" -ArgumentList "-NoExit", "-Command",
     "`$host.UI.RawUI.WindowTitle='nexscreen-host'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; & '<exe>' 2>&1 | Tee-Object -FilePath '<log>'"
   ```
   Logs de la tablet: `adb logcat -d -s StreamClient VideoDecoder AndroidRuntime`.

## Trampas conocidas del entorno

- **Antes de diagnosticar una captura que no arranca, verificar que la sesión no esté bloqueada**:
  `nexscreen-host.exe --list-displays` muestra "Sesión de Windows bloqueada" en la primera línea.
  Con la pantalla de bloqueo, Desktop Duplication devuelve "access denied" o se cuelga sin error, y
  la captura GDI sale negra. En Windows 10/11 la pantalla de bloqueo se dibuja en el escritorio
  "Default": se detecta con `WTSQuerySessionInformation` (`SessionLock.cs`), no con `OpenInputDesktop`.
  Una vez se perdió mucho tiempo persiguiendo un "bug" que era el PC bloqueado.
- **Desktop Duplication devuelve "access denied"** desde las tareas en segundo plano de Claude Code
  (`run_in_background`), servicios o con la sesión bloqueada. El host siempre se lanza con
  `Start-Process` en la sesión del usuario.
- **El host en ejecución bloquea sus ejecutables**: detenerlo antes de recompilar
  (`Get-Process nexscreen-host, NexScreen | Stop-Process -Force` y cerrar la ventana `nexscreen-host`).
  Solo puede correr uno a la vez (los dos usan el puerto 27190).
- **Scripts de Python que reescriben archivos**: `Path.write_text` en Windows convierte `\n` en
  CRLF y después Edit no encuentra los textos. Escribir bytes o usar `newline="\n"`.
- `InternalsVisibleTo` usa el nombre del **ensamblado** (`nexscreen-host`, `NexScreen`), no el del proyecto.
- **WiFi:** el PC tiene IP 192.168.1.88 en la red "Fenita_5G" (marcada como Privada el 2026-10-14
  a pedido del usuario) y la tablet 192.168.1.87. Reglas de firewall "NexScreen" (TCP 27190) y
  "NexScreen (descubrimiento)" (UDP 27191) creadas con `scripts/allow-firewall.ps1`.
- **Leer el log con PowerShell 5** requiere `Get-Content -Encoding UTF8`; sin eso los acentos salen
  mal y `Select-String` no encuentra palabras con tilde.
- Para ver la pantalla de la tablet sin pedirle nada al usuario: `adb shell uiautomator dump` +
  `adb pull` y leer los `text=` del XML (con `-Encoding UTF8`). `adb shell input tap x y` para tocar.
- **Los daemons de Gradle y Kotlin bloquean la carpeta del proyecto**: para moverla o renombrarla,
  `gradlew --stop` y matar los `java.exe` que queden.
- **Reiniciar el servidor adb** (`adb kill-server`) hace que la tablet vuelva a pedir autorización
  de depuración USB: avisarle al usuario.
- El PC no tiene GPU dedicada: Intel UHD (i7-13620H). Quick Sync funciona; x264 es el respaldo.
- No hay feed NuGet global: el repo trae `host/nuget.config`.
- Hay un hook de seguridad que bloquea comandos de PowerShell que contienen ciertos patrones
  (p. ej. `.Replace(` sobre rutas con espacios). Para editar archivos usar Edit/Write.

## Monitor virtual (fase 2)

- Driver: Virtual Display Driver 25.7.23, instalado con `scripts/install-virtual-display.ps1`
  (admin, una vez). Configuración en `C:\VirtualDisplayDriver\vdd_settings.xml` (editable sin admin).
  Control por el named pipe `\\.\pipe\MTTVirtualDisplayPipe` (`RELOAD_DRIVER`, `PING`...).
- **IddCx expone el monitor como un target del adaptador Intel** (EDID `MTT1337`, nombre
  "VDD by MTT"), no como el `\\.\DISPLAYn` del adaptador del driver. Se identifica por target
  con la API CCD (`DisplayConfig.cs`).
- `ChangeDisplaySettingsEx` **no puede agregar** una pantalla al escritorio (devuelve -1): para
  agregar o quitar se usa `SetDisplayConfig`; CDS solo fija el modo y la posición después.
- El driver no acepta 0 monitores: "apagar" es quitarlo del escritorio.
- **Escala (DPI) por monitor:** no hay API pública. Se usa la interna de Configuración →
  Pantalla: `DisplayConfigGet/SetDeviceInfo` con tipos -3/-4 (`DisplayConfig.cs`). Los valores
  son relativos a la escala recomendada por Windows (`DpiScale.cs`). Se aplica antes de capturar.
- Al reordenar pantallas, Windows invalida Desktop Duplication (`887a0026`); `ClientSession`
  reinicia la captura sin cortar la conexión.
- La posición elegida por el usuario se guarda en `%APPDATA%\NexScreen\host-state.json`
  (el usuario tiene la tablet a la izquierda del notebook).

## Input táctil y lápiz (fase 3)

- Se inyecta con `InjectSyntheticPointerInput` (táctil y lápiz reales de Windows, sin driver).
- **Las coordenadas son relativas al origen del escritorio virtual** (`SM_XVIRTUALSCREEN`,
  `SM_YVIRTUALSCREEN`), no absolutas. Con la tablet a la izquierda el origen es (-2304, 0): sin
  restarlo los toques se pierden sin ningún error. Con la tablet a la derecha no se nota.
- `GetCursorPos` **no sirve para verificar el táctil** (el cursor no siempre sigue al toque); el
  hover del lápiz sí lo mueve. Para probar toques, abrir una ventana en un punto conocido y
  verificar que reciba el clic.
- Las pruebas con `fake_client.py` agregan el monitor virtual aunque la tablet no esté conectada:
  avisarle al usuario, porque ve aparecer y desaparecer una pantalla.

## Instalador (fase 4e)

- `installer\build.ps1` publica la bandeja y la consola (self-contained win-x64), descarga con
  caché (`installer\.cache`) y SHA-256 fijo ffmpeg LGPL 9.0.2, adb r37.0.1, el driver y nefcon,
  arma `installer\staging\app` y compila `installer\nexscreen.iss` → `dist\NexScreen-Setup-<v>.exe`.
- La versión sale de `host\Directory.Build.props` (o `-Version`). El `AppId` del .iss no se cambia.
- **ffmpeg del instalador es LGPL (sin x264)**: el respaldo de Quick Sync es Media Foundation
  (`h264_mf`, `-scenario display_remoting`). x264 queda solo si el ffmpeg lo trae (desarrollo).
- El instalador corre como admin (puede ser otra cuenta): no escribe en HKCU. "Iniciar con
  Windows" lo activa la bandeja la primera vez (`AppSettings.StartupConfigured`) y la reapunta si
  quedó apuntando a otro exe (por ejemplo, el de desarrollo).
- Instalado en `C:\Program Files\NexScreen`. Log del driver: `C:\ProgramData\NexScreen\install-virtual-display.log`.
  Desinstalar quita también las reglas del firewall y el monitor virtual.
- **En el PC del usuario hay un NexScreen instalado** (0.4.1, compilación local con el
  actualizador; se actualiza solo desde la 0.5.0): para probar cambios del código hay que cerrar
  el NexScreen instalado (usa el mismo puerto). Para armar un instalador local:
  `installer\build.ps1 -Apk <APK firmado> -Version X.Y.Z` (en PowerShell, `'-PversionName=X.Y.Z'`
  va entre comillas al llamar a gradlew).

## Releases (fase 4f)

- **Antes de publicar, agregar la sección `## [X.Y.Z] - fecha` en `CHANGELOG.md`** con los cambios
  para el usuario (Novedades / Mejoras / Correcciones / Importante). El texto del release sale de
  ahí (`scripts/release-notes.ps1`, que se puede correr en local para revisarlo) y, si falta,
  el workflow falla antes de compilar. Ir agregando los cambios a la sección mientras se trabaja.
- Los `.ps1` del repo van en UTF-8 **con BOM**: sin BOM, PowerShell 5 (el del instalador y el del
  PC) lee los acentos mal.
- **Publicar:** `git tag vX.Y.Z` + `git push origin vX.Y.Z`. El workflow `.github/workflows/release.yml`
  corre los tests, firma el APK, arma el instalador con esa versión y crea el release con
  `NexScreen-Setup-X.Y.Z.exe` y `NexScreen-X.Y.Z.apk`. CI (`ci.yml`) compila y prueba cada push.
- **Clave de firma del APK:** `C:\Users\AlexPerez\.nexscreen\nexscreen-release.jks` (datos en
  `signing.json` de esa carpeta). Copia en los secretos del repo (`NEXSCREEN_KEYSTORE_BASE64`,
  `NEXSCREEN_KEYSTORE_PASSWORD`, `NEXSCREEN_KEY_ALIAS`). **Si se pierde, las tablets no pueden
  actualizar la app**: hay que desinstalarla (el host lo hace solo si detecta otra firma).
- Para firmar en el PC: cargar `signing.json` en las variables `NEXSCREEN_KEYSTORE`,
  `NEXSCREEN_KEYSTORE_PASSWORD` y `NEXSCREEN_KEY_ALIAS` y correr `gradlew assembleRelease`.
- `versionCode` del APK = mayor×10000 + menor×100 + parche. El host compara el del APK incluido
  (`tools\NexScreen.apk.json`, lo escribe build.ps1 con aapt2) con el de la tablet y actualiza la app.
- ffmpeg se descarga primero de la copia propia (release `build-deps`, pre-release) y si no, de BtbN.

## Seguridad por WiFi (protocolo v2)

- Por WiFi todo va por **TLS** y la tablet tiene que estar **emparejada** con un PIN de 6 dígitos
  que muestra la bandeja (`PairingForm`). Por USB (conexión desde 127.0.0.1) va sin cifrar y sin
  PIN. Detalle del protocolo y del cálculo del PIN en `docs/protocol.md`.
- Host: `Security/HostIdentity.cs` (certificado en `CurrentUser\My`, nombre "NexScreen host"; se
  crea solo), `Security/Pairing.cs`, `Security/PairedDevices.cs`
  (`%APPDATA%\NexScreen\paired-devices.json`), `Net/Handshake.cs`. En la bandeja, "Tablets
  emparejadas (WiFi)" permite olvidar una.
- Tablet: `net/TlsIdentity.kt` (clave en el Android Keystore), `net/Pairing.kt` (`TrustedHosts`
  en SharedPreferences `trusted_hosts`). Reinstalar la app (o que el host la reinstale por cambio
  de firma) borra su identidad: hay que volver a emparejar.
- **El handshake corre antes de tocar la sesión activa** (`StreamServer.HandleClientAsync`):
  una conexión no emparejada no puede cortar la de la tablet. Máximo 4 handshakes a la vez.
- Probar sin tablet: `fake_client.py --host 192.168.1.88` usa TLS con una identidad propia
  (`--identity`, requiere `cryptography`) y pide el PIN, que la consola `nexscreen-host` imprime
  (no va al log). **Ojo:** la consola guarda la emparejada en el mismo `paired-devices.json` que la
  bandeja; borrarla después de probar. `Tee-Object` de PowerShell 5 escribe el archivo en UTF-16.
- `HostIdentity` no usa `X509CertificateLoader` (es de .NET 9) y reimporta el certificado como
  PFX persistente: **Schannel no puede usar claves efímeras** (también en los tests).

## Actualización automática

- La bandeja consulta `api.github.com/repos/AlexPerez7/NexScreen/releases/latest` al minuto de
  iniciar y cada 24 h (`Update/UpdateClient.cs`; se desactiva con `"checkForUpdates": false` en
  `settings.json`). `/releases/latest` ignora los pre-release (como `build-deps`).
- Si hay una versión nueva aparece "Instalar la versión X.Y.Z" en el menú y un globo (una vez por
  versión, `notifiedUpdate`). Al instalar: descarga en `%LOCALAPPDATA%\NexScreen\updates`, verifica
  tamaño y el SHA-256 que publica GitHub (`digest` del asset), detiene el servidor (quita el monitor
  virtual) y corre el instalador con `/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1`. Pide UAC;
  si se cancela, el servidor vuelve a arrancar.
- `/RELAUNCH=1` hace que el instalador vuelva a abrir NexScreen como el usuario original
  (`ShouldRelaunch` en el `.iss`). Una instalación silenciosa sin ese parámetro no la abre.
- El asset debe llamarse `NexScreen-Setup-X.Y.Z.exe` y el tag `vX.Y.Z`: no cambiar esos nombres
  en `release.yml` sin actualizar `UpdateRelease.cs`.
- La 0.4.0 no tiene el actualizador: el salto a la primera versión con él se instala a mano.

## Protocolo

Al cambiar el protocolo hay que actualizar **siempre los cuatro lugares**:
`host/.../Protocol/Wire.cs`, `android/.../net/Protocol.kt`, `docs/protocol.md` y
`host/tools/fake_client.py`. Si el cambio es incompatible, subir `Version` en ambos lados.

## Convenciones

- Código del host: async/await con `ConfigureAwait(false)`, `CancellationToken` en todo lo que
  bloquea, P/Invoke en `NativeMethods.cs`/`DisplayConfig.cs`. Tests xUnit para la lógica pura
  (parsers, cálculo de posiciones, opciones).
- Las funciones de Windows que dependen del hardware se aíslan para poder testear la lógica sin él
  (p. ej. `VirtualDisplay.ComputePosition` recibe la lista de pantallas).
- Commits en español, con un resumen de la fase o del cambio y el detalle en viñetas.
- Repo público: https://github.com/AlexPerez7/NexScreen (rama `main`, sin licencia por ahora).
  No commitear `local.properties`, binarios ni capturas.

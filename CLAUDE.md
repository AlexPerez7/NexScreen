# CLAUDE.md

Guía para trabajar en NexScreen con Claude Code.

## Idioma

- **Todo en español neutro, tratando de "tú". Nunca voseo** (nada de "usá", "ejecutá", "podés",
  "tenés", "movelo"; sí "usa", "ejecuta", "puedes", "tienes", "muévelo").
- Aplica a respuestas en el chat, comentarios de código, mensajes de log, textos de la app
  (`strings.xml`), scripts, README, docs y mensajes de commit.
- Los identificadores de código (clases, métodos, variables) van en inglés.

## Qué es

NexScreen convierte una tablet o un celular Android en un monitor extendido de un PC con Windows,
por USB o WiFi. Se desarrolló con una Samsung Galaxy Tab S9 FE (2304×1440, 90 Hz, Android 16) y un
notebook sin GPU dedicada, pero **no debe depender de ellos**: nada de resoluciones, fps, escalas o
encoders fijos para ese hardware (ver "Ajustes según el dispositivo y la GPU"). En el código y los
docs "tablet" suele querer decir "el dispositivo Android"; en los textos para el usuario, usar
"dispositivo".

| Fase | Contenido | Estado |
|------|-----------|--------|
| 1 | Espejo de un monitor → tablet | ✅ |
| 2 | Monitor virtual para extender el escritorio | ✅ |
| 3 | Táctil y S Pen (presión) hacia Windows | ✅ |
| 4 | App de bandeja, autoconexión, instalador, releases | ✅ |
| 5 | Actualización automática, TLS y emparejamiento por WiFi (protocolo v2) | ✅ |
| 6 | Pedido de keyframe y bitrate adaptativo | ✅ |
| 7 | Cualquier GPU (NVENC, AMF, varias GPU), celulares, fps/bitrate/escala según el dispositivo | ✅ (sin probar en NVIDIA/AMD ni celular) |

Ideas para próximas versiones, priorizadas y con notas técnicas: `PLAN.md`.

## Equipos del usuario

El proyecto se trabaja en dos PCs. Los datos de uno no valen para el otro.

| | Trabajo (usuario `AlexPerez`) | Personal (usuario `alexm`) |
|---|---|---|
| CPU / GPU | i7-13620H, solo Intel UHD (Quick Sync) | i5-13420H, Intel UHD + NVIDIA RTX 4060 Laptop (NVENC) |
| Red WiFi | "Fenita_5G", PC 192.168.1.88, tablet 192.168.1.87 (red Privada desde el 2026-10-14) | "Universidad Catolica 5G", PC 192.168.100.24 (perfil Público el 2026-10-15) |
| NexScreen instalado | 0.4.1 local, se actualiza solo | 0.7.0 (el 2026-10-15; NVENC nativo probado con la tablet por USB) |
| Clave de firma del APK | Sí | No |
| Herramientas | Android Studio y SDK | .NET SDK 8, Inno Setup, platform-tools y Android Studio instalados con winget el 2026-10-15; el SDK de Android y el firewall/driver del monitor virtual faltan |

Antes de asumir un dato de red o de hardware, verificar en qué PC se está (`$env:USERNAME`).

## Estructura

```
host/Directory.Build.props    Versión de NexScreen (única fuente) y configuración común
host/src/NexScreen.Core/      Lógica del host (librería, net8.0-windows)
  Protocol/                   Framing y mensajes (Wire.cs)
  Video/                      ffmpeg (ddagrab + qsv/nvenc/amf/mf/x264) -> FLV -> Annex-B; StreamTuning (fps, bitrate, escala)
  Video/Native/               Pipeline propio sin ffmpeg: Desktop Duplication -> VideoProcessor -> NVENC o MFT de Media Foundation
  Net/                        StreamServer, ClientSession, VideoStreamer, IStreamObserver (eventos para la UI)
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
- **WiFi:** las reglas de firewall "NexScreen" (TCP 27190) y "NexScreen (descubrimiento)"
  (UDP 27191) se crean con `scripts/allow-firewall.ps1`. Datos de red por equipo en "Equipos del
  usuario".
- **Leer el log con PowerShell 5** requiere `Get-Content -Encoding UTF8`; sin eso los acentos salen
  mal y `Select-String` no encuentra palabras con tilde.
- Para ver la pantalla de la tablet sin pedirle nada al usuario: `adb shell uiautomator dump` +
  `adb pull` y leer los `text=` del XML (con `-Encoding UTF8`). `adb shell input tap x y` para tocar.
- **Los daemons de Gradle y Kotlin bloquean la carpeta del proyecto**: para moverla o renombrarla,
  `gradlew --stop` y matar los `java.exe` que queden.
- **Reiniciar el servidor adb** (`adb kill-server`) hace que la tablet vuelva a pedir autorización
  de depuración USB: avisarle al usuario.
- En un PC sin GPU NVIDIA/AMD (el del trabajo) NVENC y AMF **no se pueden probar**: fallan al
  instante y la cadena sigue con el siguiente encoder. Para probarlos hay que usar el PC personal.
- `scale_d3d11` del ffmpeg 9 incluido falla en este Intel ("Could not create the texture"): por
  eso NVENC/AMF escalan por CPU cuando hace falta, en vez de en la GPU.
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
- Al reordenar pantallas, Windows invalida Desktop Duplication (`887a0026`); `VideoStreamer`
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
- **En los PCs del usuario hay un NexScreen instalado** (trabajo: 0.4.1, compilación local con el
  actualizador, se actualiza solo desde la 0.5.0; personal: 0.7.0, ver "Equipos del usuario"): para probar cambios del código hay que cerrar
  el NexScreen instalado (usa el mismo puerto). Para armar un instalador local:
  `installer\build.ps1 -Apk <APK firmado> -Version X.Y.Z` (en PowerShell, `'-PversionName=X.Y.Z'`
  va entre comillas al llamar a gradlew).

## Firma del instalador (SignPath Foundation)

- Requisitos de SignPath Foundation (signpath.org/terms) ya cubiertos en el repo: licencia MIT,
  `PRIVACY.md`, sección "Política de firma de código" del README (con la atribución obligatoria),
  `installer\informacion.txt` (cambios en el sistema y privacidad, se muestra antes de instalar),
  opción "Buscar actualizaciones automáticamente" en la bandeja, y nombre/versión de producto
  iguales en todos los binarios (`Directory.Build.props` sin hash de commit, `VersionInfo*` del .iss).
- **La única conexión a internet es la consulta de actualizaciones a GitHub.** Si se agrega otra
  (telemetría, etc.) hay que declararla en `PRIVACY.md` e `informacion.txt` y poder desactivarla.
- Falta, tras la aprobación: integrar `signpath/github-action-submit-signing-request` en `release.yml`.
- Todos los miembros deben tener autenticación en dos pasos (GitHub y SignPath).

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
- **Clave de firma del APK:** `%USERPROFILE%\.nexscreen\nexscreen-release.jks` (datos en
  `signing.json` de esa carpeta). Solo existe en el PC del trabajo; el personal no la tiene (hay
  que copiarla, o firmar solo con el workflow de GitHub). Copia en los secretos del repo (`NEXSCREEN_KEYSTORE_BASE64`,
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

## Video: keyframe a pedido y bitrate adaptativo

- `Net/VideoStreamer.cs` mantiene la captura viva (reintentos, cambio de encoder, sesión
  bloqueada) y la **reemplaza en caliente**: arranca un ffmpeg nuevo mientras el viejo sigue
  transmitiendo y pasa al nuevo con su primer cuadro (SPS/PPS + IDR). Desktop Duplication admite
  dos capturas de la misma pantalla a la vez. El arranque de ffmpeg con Quick Sync tarda ~350 ms.
- Se reemplaza por un `REQUEST_KEYFRAME` de la tablet (máximo uno por segundo) o por un bitrate
  nuevo de `Video/BitrateController.cs` (AIMD, lógica pura con tests; reglas en docs/protocol.md).
  Al bajar por congestión se descartan los cuadros viejos hasta que entra la captura nueva.
- `--bitrate` (y la "Calidad" de la bandeja) es el **máximo**; `--fixed-bitrate` desactiva la
  adaptación. La bandeja muestra "red lenta" si el bitrate está por debajo del máximo.
- Probar sin tablet: `fake_client.py --limit-mbps 1 --limit-seconds 8 --seconds 50` (baja al piso y
  vuelve a subir) y `--request-keyframe 3`. Con `--mode mirror --no-usb` la consola no agrega el
  monitor virtual ni le avisa a la tablet por USB.
- En la tablet, `StreamClient` guarda los SPS/PPS: si el decoder falla lo recrea, pide un
  keyframe y descarta cuadros hasta recibirlo (más de 3 fallas en 10 s cortan la conexión).

## Pipeline nativo (sin ffmpeg)

- **Por qué existe:** `ddagrab` de ffmpeg no pasa de ~72 fps ni en una pantalla real de 144 Hz (medido
  en el PC personal), aunque el dispositivo pida 90 o 120 Hz. El pipeline propio sostiene los fps
  que pide el dispositivo (90,0 fps con la tablet por USB y movimiento real).
- **Cadena** (`Video/Native/`): `DesktopCapture` (Desktop Duplication en el adaptador de la
  pantalla, copia cada imagen a una textura propia, dibuja el cursor como segunda capa y convierte
  BGRA→NV12 BT.709 con `ID3D11VideoProcessor`, escalando en la GPU si hace falta) →
  `IGpuEncoder` (`NvencEncoder` para NVIDIA, `MfH264Encoder` para Intel y AMD) →
  `NativeVideoSource` (un hilo, `FramePacer` y una cola de salida). Es el primer encoder de
  `AutoEncoders` (`EncoderKind.Native`, `--encoder native`); si falla al arrancar (sin driver,
  GPU desconocida, HDR) la fuente termina vacía y el streamer sigue con ffmpeg.
- **Pacing** (`FramePacer`, lógica pura con tests): se manda por una rejilla regular de 1/fps; con
  la pantalla a 144 Hz y 90 fps se manda lo último que hay en cada turno. Con la pantalla quieta se
  repite el último cuadro cada 250 ms; por eso la bandeja marca menos fps con el escritorio quieto
  (ffmpeg duplicaba cuadros). Si la cola de salida tiene más de 3 cuadros no se codifica (la red no
  da abasto): así no se acumula atraso ni se rompe la cadena de referencias.
- **Keyframe y bitrate en marcha:** `IVideoSource.SupportsLiveControl`. `VideoStreamer` no
  reemplaza la captura con la fuente nativa: `RequestKeyframe()` y el bitrate de `BitrateController`
  llegan directo al encoder (NVENC: `NvEncReconfigureEncoder`; MFT: `ICodecAPI`).
- **NVENC directo** (`NvEncApi.cs`, `NvencEncoder.cs`): `nvEncodeAPI64.dll` por punteros a funciones,
  API 11.1 (drivers desde la serie 470). Las estructuras no se declaran como structs: se arman en
  memoria con los desplazamientos de `nvEncodeAPI.h` 11.1 (x64). **Hay que poner en cero la
  estructura entera antes de usarla** (NVENC rechaza el mapeo con `INVALID_PARAM` si los campos
  reservados traen basura). Preset P1 + `ULTRA_LOW_LATENCY`, CBR, sin B-frames, perfil Main, GOP de
  2 s, SPS/PPS en cada IDR y VUI BT.709 limitado. Las texturas NV12 se crean solo con `RenderTarget`.
- **Media Foundation** (`MfH264Encoder.cs`): el MFT de hardware se elige por el nombre del
  fabricante (los activadores no informan el adaptador). Es asíncrono: los eventos llegan por callback
  a una cola. **El MFT de NVIDIA falla al activarse (`E_UNEXPECTED`) en el PC personal**, por eso
  NVIDIA usa NVENC directo. El MFT de Intel no escribe la matriz de color en el VUI (sí el rango).
- **Sin probar:** AMD (MFT de AMF), HDR, rotación de pantalla, sesión bloqueada con la captura
  nativa, colores en la tablet con el MFT de Intel.
- **Medir fps de verdad:** hace falta contenido que cambie a la frecuencia de la pantalla. Una
  ventana de WinForms/GDI solo llega a ~63 fps y falsea la medición; hay que pintar con D3D11 y
  `Present(1)`. Desde Bash, pasar `\\.\DISPLAYn` a un exe lo deja mal escapado: lanzarlo desde la
  herramienta PowerShell. Para ver la tablet: `adb exec-out screencap -p > x.png` **desde Bash**
  (PowerShell 5 corrompe la redirección binaria).
- **Probar con `fake_client.py` con la tablet enchufada:** la tablet se reconecta sola y le quita la
  sesión. Antes `adb reverse --remove-all`, y al terminar `adb reverse tcp:27190 tcp:27190` y
  abrir la app en la tablet.

## Ajustes según el dispositivo y la GPU

- **Captura en la GPU correcta:** `DxgiOutputs` recorre todos los adaptadores (no solo el 0). Cada
  pantalla es un `CaptureTarget` (adaptador, salida, fabricante); ffmpeg recibe
  `-init_hw_device d3d11va=nexscreen:<adaptador> -filter_hw_device nexscreen` y ddagrab va en
  `-filter_complex` (así recibe ese dispositivo: el log en verbose dice "Using provided hw_device_ctx").
  `--monitor n` es el índice global de `--list-displays`.
- **Encoder automático** (`FfmpegVideoSource.AutoEncoders`): primero el pipeline nativo; si no
  puede, el de ffmpeg del fabricante de esa GPU
  (Quick Sync / NVENC / AMF), luego `h264_mf` por GPU (`-hw_encoding 1`, cualquier fabricante),
  luego por CPU y x264. NVENC, AMF y MF por GPU reciben la textura de ddagrab directa si la
  pantalla entra en el dispositivo (siempre en modo extendido); si no, escalan por CPU.
- **fps:** la frecuencia que informa la app en HELLO (`refreshRate`, entre 30 y 120; 60 si falta).
  El monitor virtual se crea a esa frecuencia. Con encoders por CPU la captura se limita a 60 fps.
- **Bitrate:** la "Calidad" de la bandeja y el valor por defecto de la consola (12) son la
  referencia para 2304×1440@60; `StreamTuning.ScaleBitrate` la ajusta por píxeles y √(fps/60).
  `--bitrate` en la consola es exacto (no se escala).
- **Escala automática:** `dpi × 150 / 280` (densityDpi de Android), al paso más cercano. En
  `settings.json`, `"tabletScale": 0` es "Automática" (nuevo valor por defecto); `null` no la toca.
- **App:** manda el tamaño sin la zona de la cámara (insets de `displayCutout`), deja ese borde en
  negro con padding, y pide el modo de pantalla de mayor frecuencia (`preferredDisplayModeId`).
- Probar perfiles sin hardware: `fake_client.py --width 3120 --height 1440 --dpi 600 --refresh-rate 120`.

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
- Repo público: https://github.com/AlexPerez7/NexScreen (rama `main`, licencia MIT).
  No commitear `local.properties`, binarios ni capturas.

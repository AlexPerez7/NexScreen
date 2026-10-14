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
| 4 | App de bandeja, autoconexión, instalador, releases | En curso (4a bandeja ✅, 4b USB ✅) |

Plan de la fase 4: 4a app de bandeja · 4b autoconexión USB · 4c descubrimiento WiFi ·
4d escala automática · 4e instalador (Inno Setup) · 4f releases con GitHub Actions + APK firmado.

## Estructura

```
host/Directory.Build.props    Versión de NexScreen (única fuente) y configuración común
host/src/NexScreen.Core/      Lógica del host (librería, net8.0-windows)
  Protocol/                   Framing y mensajes (Wire.cs)
  Video/                      ffmpeg (ddagrab + h264_qsv/x264) -> FLV -> Annex-B
  Net/                        StreamServer, ClientSession, IStreamObserver (eventos para la UI)
  Display/                    Monitor virtual: API CCD, GDI, DXGI, driver VDD
  Input/                      Táctil y lápiz: mensaje POINTER, traductor de frames, inyección Win32
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

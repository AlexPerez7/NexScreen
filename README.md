# NexScreen

Usa una tablet Android como **monitor extendido** de tu PC con Windows, por **USB** o **WiFi**, con
táctil y lápiz. Desarrollado y probado con una Samsung Galaxy Tab S9 FE (2304×1440) y un notebook
con gráficos Intel.

- **Monitor real:** Windows ve la tablet como una pantalla más (a la izquierda, derecha, arriba
  o abajo), con su resolución nativa y escala automática.
- **Táctil y S Pen:** tocar es clic, dos dedos desplazan y hacen zoom, y el lápiz tiene presión,
  inclinación y botón lateral. Rechazo de palma mientras el lápiz está cerca.
- **Sin configurar nada:** enchufas la tablet por USB y se conecta sola; por WiFi la tablet
  encuentra el PC en la red.
- **Baja latencia:** captura con Desktop Duplication y H.264 por hardware (Intel Quick Sync).

## Instalación

1. Descarga `NexScreen-Setup-<versión>.exe` desde
   [Releases](https://github.com/AlexPerez7/NexScreen/releases) e instálalo (pide permisos de
   administrador para el monitor virtual y el firewall).
2. En la tablet, activa *Opciones de desarrollador → Depuración USB* (una sola vez).
3. Enchufa la tablet: NexScreen instala la app en la tablet, la abre y se conecta.

Por WiFi: abre la app NexScreen en la tablet y toca tu PC en la lista "PCs en la red". El PC y la
tablet tienen que estar en la misma red, marcada como **Privada** en Windows.

NexScreen queda como un ícono junto al reloj. Con clic derecho eliges el modo (extender o espejar),
la posición de la tablet, la calidad y la escala.

## Estado

| Fase | Contenido | Estado |
|------|-----------|--------|
| 1 | Espejo de un monitor existente → tablet (USB y WiFi) | ✅ |
| 2 | Monitor virtual (driver IddCx) para **extender** el escritorio | ✅ |
| 3 | Input táctil y S Pen (presión) hacia Windows | ✅ |
| 4 | App de bandeja, autoconexión USB, descubrimiento WiFi, escala, instalador | ✅ (releases automáticos en curso) |

## Cómo funciona

```
monitor virtual (Virtual Display Driver, 2304x1440)
  → ddagrab (Desktop Duplication) → Quick Sync H.264 (respaldo: Media Foundation) → FLV por pipe
  → NexScreen (bandeja): FLV → Annex-B → protocolo propio sobre TCP 27190
  → tablet: MediaCodec (low latency) → SurfaceView
tablet: dedos y S Pen → mensaje POINTER → InjectSyntheticPointerInput (táctil y lápiz de Windows)
```

- Al conectarse la tablet se agrega un monitor virtual con su resolución nativa (API CCD de
  Windows) y se quita al desconectarse, así no quedan ventanas perdidas en un monitor invisible.
- La posición del monitor se recuerda (`%APPDATA%\NexScreen\host-state.json`).
- Si Windows invalida la captura (reordenar pantallas, UAC) se reinicia sin cortar la conexión;
  con la sesión bloqueada se espera el desbloqueo. La tablet se reconecta sola si se corta.
- USB: el host detecta la tablet con `adb`, arma el túnel `adb reverse` y abre la app.
- WiFi: la tablet busca hosts con un broadcast UDP al puerto 27191.
- Protocolo completo en [docs/protocol.md](docs/protocol.md).

## Estructura

```
host/          Windows (C# / .NET 8)
  src/NexScreen.Core/   lógica: protocolo, video, monitor virtual, input, USB, descubrimiento
  src/NexScreen.Tray/   app de bandeja (NexScreen.exe)
  src/NexScreen.Host/   consola de desarrollo y diagnóstico (nexscreen-host.exe)
  tests/                xUnit
android/       App de la tablet (Kotlin)
installer/     Instalador (Inno Setup) y script de build
scripts/       Monitor virtual, firewall y USB manual
docs/          Protocolo de red
```

## Desarrollo

Requisitos: Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download), ffmpeg con
`ddagrab` y `h264_qsv` en el PATH (`winget install Gyan.FFmpeg`), Android Studio (o Android SDK +
JDK) e [Inno Setup 6](https://jrsoftware.org/isinfo.php) para el instalador.

```powershell
# Host
dotnet build host
dotnet test host
host\src\NexScreen.Tray\bin\Debug\net8.0-windows\NexScreen.exe        # app de bandeja
host\src\NexScreen.Host\bin\Debug\net8.0-windows\nexscreen-host.exe --help   # consola

# App Android
android\gradlew.bat -p android assembleDebug
adb install -r android\app\build\outputs\apk\debug\app-debug.apk

# Monitor virtual (una vez, como administrador) y firewall para WiFi
scripts\install-virtual-display.ps1
scripts\allow-firewall.ps1

# Instalador -> dist\NexScreen-Setup-<versión>.exe
installer\build.ps1
```

Para probar el host sin la tablet: `python host\tools\fake_client.py --seconds 5` simula la tablet
y guarda el video recibido (`ffprobe capture.h264`). `nexscreen-host --list-displays` muestra las
pantallas, el monitor virtual, la escala y si la sesión está bloqueada.

## Créditos

- [farmerbb/SecondScreen](https://github.com/farmerbb/SecondScreen) (Apache 2.0): ideas de
  perfiles de resolución, pantalla siempre encendida y modo inmersivo.
- [OnCloud125252/Spacedesk-Viewer](https://github.com/OnCloud125252/Spacedesk-Viewer): concepto de
  visor liviano que se conecta solo. No se reutiliza código.
- Componentes incluidos en el instalador (FFmpeg LGPL, adb, Virtual Display Driver, nefcon):
  ver [installer/THIRD-PARTY-NOTICES.md](installer/THIRD-PARTY-NOTICES.md).

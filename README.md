# NexScreen

Usá una tablet Android (desarrollado para Samsung Galaxy Tab S9 FE) como monitor de un PC
con Windows, por **USB** o **WiFi**.

```
host/      Servidor Windows (C# / .NET 8): captura, codifica H.264 y transmite
android/   App de la tablet (Kotlin): decodifica por hardware y muestra a pantalla completa
scripts/   Utilidades: USB (adb reverse) y regla de firewall para WiFi
docs/      Protocolo de red
```

## Estado

| Fase | Contenido | Estado |
|------|-----------|--------|
| 1 | Espejo de un monitor existente → tablet (USB y WiFi) | ✅ |
| 2 | Monitor virtual (driver IddCx) para **extender** el escritorio | Pendiente |
| 3 | Input táctil y S Pen (presión) hacia Windows | Pendiente |
| 4 | App de bandeja, autoconexión, perfiles, instalador | Pendiente |

## Cómo funciona

```
ddagrab (Desktop Duplication) → Quick Sync H.264 (fallback x264) → FLV por pipe
  → nexscreen-host: FLV → Annex-B → protocolo propio sobre TCP
  → tablet: MediaCodec (low latency) → SurfaceView
```

- La captura y la codificación la hace **ffmpeg** como subproceso, detrás de la interfaz
  `IVideoSource` para poder reemplazarla por un pipeline nativo Media Foundation.
- ffmpeg emite FLV porque cada paquete trae su tamaño: no hay que esperar al siguiente
  start code para cortar un frame, así que no se agrega un frame de latencia.
- Con `--encoder auto` se usa Intel Quick Sync (`h264_qsv`); si no produce video en 5 s se pasa a x264.

## Requisitos

- Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download) y ffmpeg con `ddagrab` y `h264_qsv`
  (`winget install Gyan.FFmpeg`).
- Android Studio (o solo Android SDK + JDK) para compilar la app.
- En la tablet: Opciones de desarrollador → **Depuración USB** activada (solo para instalar y para el modo USB).

## Uso

### 1. Host

```powershell
cd host
dotnet run --project src/NexScreen.Host            # opciones: -- --help
```

Opciones útiles: `--monitor 1` (otro monitor), `--fps 30`, `--bitrate 20`, `--encoder x264`, `--verbose`.

> `nexscreen-host` debe correr en la sesión de escritorio del usuario: Desktop Duplication devuelve
> *access denied* desde servicios, sesiones sin escritorio o con la pantalla bloqueada.

### 2. App Android

```powershell
cd android
./gradlew assembleDebug
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

O abrir la carpeta `android/` en Android Studio y presionar Run.

### 3. Conectar

- **USB:** `scripts/usb-connect.ps1` y en la tablet *Conectar por USB*.
- **WiFi:** una vez, como administrador, `scripts/allow-firewall.ps1`. En la tablet ingresá una
  de las IPs que muestra `nexscreen-host` al iniciar y tocá *Conectar por WiFi*. Las redes corporativas
  suelen aislar clientes WiFi entre sí; si no conecta, probá con USB o con el hotspot del PC.

## Desarrollo

```powershell
cd host
dotnet test                                   # tests del host
python tools/fake_client.py --seconds 5       # cliente de prueba: valida el stream sin tablet
ffprobe capture.h264
```

## Créditos e ideas

- [farmerbb/SecondScreen](https://github.com/farmerbb/SecondScreen) (Apache 2.0): perfiles de
  resolución/DPI, pantalla siempre encendida y modo inmersivo.
- [OnCloud125252/Spacedesk-Viewer](https://github.com/OnCloud125252/Spacedesk-Viewer): concepto de
  visor liviano que se conecta solo. No se reutiliza código (licencia no especificada y derivado de Spacedesk).
- Fase 2: [VirtualDrivers/Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (MIT).

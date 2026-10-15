# Plan: ideas para próximas versiones

Las fases 1 a 4 están terminadas (v0.4.0). Esta es la lista de ideas pendientes, ordenada por
prioridad. Cada una trae notas técnicas para retomarla sin redescubrir el contexto.

Esfuerzo estimado: **S** (horas), **M** (un par de días), **L** (una semana o más).

## Prioridad alta

### 1. Seguridad de la conexión por WiFi — ✅ hecho

TLS + emparejamiento por PIN, protocolo v2 (ver CLAUDE.md, "Seguridad por WiFi", y
docs/protocol.md). Queda abierto: un PAKE (SPAKE2) para que ni siquiera un man-in-the-middle
activo durante el emparejamiento pueda probar los PINs fuera de línea.

<details><summary>Notas originales</summary>

### Seguridad de la conexión por WiFi — M

**Qué:** hoy cualquier dispositivo de la red puede conectarse al puerto 27190, ver la pantalla e
inyectar toques. Agregar emparejamiento y cifrado.

**Cómo:**
- Emparejamiento con un PIN de 6 dígitos que muestra la bandeja la primera vez; la tablet lo
  ingresa y ambos guardan una clave compartida (o el certificado del otro).
- TLS sobre el socket (`SslStream` en el host, `SSLSocket` en Android) con certificado
  autofirmado fijado tras el emparejamiento.
- Por USB (`adb reverse`, 127.0.0.1) se puede omitir: el cable ya implica confianza.
- Requiere protocolo v2 (`Wire.Version` = 2): actualizar los cuatro lugares (ver CLAUDE.md).

</details>

### 2. Pedido de keyframe y bitrate adaptativo — ✅ hecho

Mensaje `REQUEST_KEYFRAME` y bitrate AIMD medido por el bloqueo del socket (ver docs/protocol.md y
CLAUDE.md, "Video"). Con ffmpeg ambas cosas se hacen reemplazando la captura en caliente (~350 ms
de arranque, sin cortar la imagen); el pipeline nativo (idea 7) lo haría sin reemplazar nada.

<details><summary>Notas originales</summary>

**Qué:** hoy, si se pierde o corrompe un cuadro, la imagen queda con artefactos hasta el próximo
keyframe (cada 2 s), y el bitrate es fijo aunque el WiFi se congestione.

**Cómo:**
- Mensaje nuevo tablet → host `REQUEST_KEYFRAME` cuando el decoder reporta un error. Con ffmpeg
  como subproceso no se puede forzar un IDR: reiniciar la captura (ya existe ese camino) o, mejor,
  resolverlo con el pipeline nativo (idea 7).
- Medir la cola de envío del socket y el RTT (ya se mide con PING) para bajar o subir el bitrate.
- Ya estaba previsto en docs/protocol.md ("Cambios previstos").

</details>

### 3. Actualización automática de la app de Windows — ✅ hecho

La bandeja busca versiones nuevas en GitHub Releases y las instala con un clic (ver CLAUDE.md,
"Actualización automática"). La app de la tablet ya se actualiza sola al enchufarla.

## Prioridad media

### 4. Licencia del repo y firma del instalador — S

**Qué:** el repo no tiene licencia (nadie puede reutilizar el código legalmente) y el instalador
no está firmado, así que Windows SmartScreen muestra una advertencia al descargarlo.

**Cómo:**
- Elegir licencia (MIT es compatible con todo lo incluido: ffmpeg LGPL, driver MIT, nefcon MIT).
- Con una licencia OSI, SignPath Foundation firma gratis proyectos open source (la misma que firma
  el Virtual Display Driver). Integrarlo en `release.yml`.

### 5. 90 Hz — ✅ hecho (0.6.0)

Generalizado: la app informa la frecuencia en HELLO y el host transmite a 60, 90 o 120 fps (ver
CLAUDE.md, "Ajustes según el dispositivo y la GPU"). Falta medir si Quick Sync y el WiFi sostienen
90 fps en la tablet.

<details><summary>Notas originales</summary>

### 90 Hz — S/M

**Qué:** la Galaxy Tab S9 FE tiene pantalla de 90 Hz; hoy se transmite a 60 fps.

**Cómo:**
- La tablet informa su frecuencia en HELLO (`Display.getRefreshRate()`), campo nuevo opcional.
- El host agrega `2304x1440@90` al XML del driver (`VddSettings.EnsureResolution` ya lo soporta)
  y usa `framerate=90` en ddagrab.
- Verificar que Quick Sync y la red sostengan 90 fps; opción "Fluidez" en la bandeja.

</details>

### 6. Audio del PC en la tablet — M

**Qué:** escuchar el sonido del PC por los parlantes de la tablet (útil para videos).

**Cómo:** captura WASAPI loopback (NAudio o P/Invoke), Opus a 48 kHz, mensaje `AUDIO` con
timestamp; en Android `MediaCodec` (Opus) + `AudioTrack` en modo baja latencia. Opción en la
bandeja (desactivado por defecto). Cuidar la sincronía con el video.

### 7. Pipeline de captura nativo (sin ffmpeg) — ✅ hecho

Desktop Duplication directo → VideoProcessor (NV12 en la GPU) → NVENC directo o MFT de Media
Foundation, con ffmpeg de respaldo (ver CLAUDE.md, "Pipeline nativo"). Sostiene los fps que pide el
dispositivo (ffmpeg se quedaba en ~72) y atiende keyframe y bitrate sin reemplazar la captura.
Queda abierto: probar AMD, HDR y la sesión bloqueada; el instalador sigue incluyendo ffmpeg (~95 MB)
por el respaldo.

<details><summary>Notas originales</summary>

**Qué:** reemplazar el subproceso ffmpeg por captura y codificación dentro del host.

**Por qué:** menos latencia (sin pipe ni FLV), keyframes a pedido, bitrate dinámico, y el
instalador baja de ~95 MB a ~30 MB.

**Cómo:** Windows.Graphics.Capture o DXGI Desktop Duplication con Vortice, conversión a NV12 en
GPU (VideoProcessor de D3D11) y codificación con Media Foundation (MFT de hardware de Intel).
Implementar `IVideoSource` (la interfaz ya existe) y dejar ffmpeg como respaldo.

</details>

### 8. Varias tablets a la vez — L

**Qué:** usar dos o más tablets como monitores simultáneos.

**Cómo:**
- El driver admite varios monitores (`<count>` en el XML, `SETDISPLAYCOUNT` por el pipe).
- `StreamServer` hoy mantiene una sola sesión (un cliente nuevo reemplaza al anterior): pasar a
  una sesión por tablet, identificada por un id del dispositivo en HELLO.
- Posición y escala guardadas por tablet en `host-state.json`.
- `DisplayConfig` debe asignar cada target virtual a una sesión (hoy asume uno solo).

## Prioridad baja

### 9. HEVC por WiFi — S/M
H.265 usa ~40 % menos ancho de banda a igual calidad. Quick Sync (`hevc_qsv`) y la tablet
(`MediaCodec` "video/hevc") lo soportan. Negociar el codec en HELLO/CONFIG y usarlo por WiFi.

### 10. Modo vertical — S/M
Usar la tablet en vertical (1440×2304) para documentos o código. El driver acepta la resolución
rotada; la app debe permitir rotación y avisar al host para reconfigurar el monitor virtual.

### 11. Atajos en la tablet — M
Barra lateral opcional con atajos (deshacer, copiar/pegar, Win+Tab, teclado en pantalla) y botón
del S Pen configurable. Se envían como teclas con un mensaje `KEY` e `SendInput` en el host.

### 12. Instalar la app por WiFi (adb inalámbrico) — M
Emparejar `adb` por WiFi (Android 11+, `adb pair`) para que la instalación y actualización
automática de la app funcione sin cable.

### 13. Distribución de la app Android — S
Publicar el APK en Google Play o F-Droid para no depender de la depuración USB en la primera
instalación. Requiere política de privacidad y ficha de la app.

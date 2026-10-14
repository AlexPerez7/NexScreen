# Componentes de terceros incluidos en el instalador de NexScreen

NexScreen se distribuye junto con los siguientes programas, sin modificaciones. Cada uno conserva
su propia licencia; los textos completos están en esta misma carpeta.

## FFmpeg 9.0.2 (build LGPL de BtbN/FFmpeg-Builds)

- Uso: captura de pantalla (ddagrab) y codificación H.264 (Quick Sync / Media Foundation).
- Licencia: GNU Lesser General Public License v2.1 o posterior — `ffmpeg-LICENSE.txt`.
- Se usa como programa externo (`tools\ffmpeg.exe` y sus DLL), sin enlazarlo con NexScreen.
- Código fuente: https://ffmpeg.org/download.html y https://github.com/BtbN/FFmpeg-Builds
  (build `ffmpeg-n9.0.2-27-g7e93014c5b-win64-lgpl-shared-9.0`).

## Android Debug Bridge (adb) — Android SDK Platform-Tools r37.0.1

- Uso: conexión por USB con la tablet (`adb reverse`) y apertura/instalación de la app.
- Licencia: Apache License 2.0 y las indicadas en `adb-NOTICE.txt`.
- Código fuente: https://android.googlesource.com/platform/packages/modules/adb/

## Virtual Display Driver 25.7.23

- Uso: monitor virtual para extender el escritorio a la tablet.
- Licencia: MIT — https://github.com/VirtualDrivers/Virtual-Display-Driver/blob/master/LICENSE
- Firmado por SignPath Foundation. Se instala solo si se elige en el instalador.

## nefcon 1.14.0

- Uso: instalación y desinstalación del monitor virtual.
- Licencia: MIT — https://github.com/nefarius/nefcon/blob/master/LICENSE

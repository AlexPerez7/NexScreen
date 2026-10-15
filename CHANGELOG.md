# Cambios

Lo nuevo de cada versión de NexScreen. Cada release de GitHub muestra la sección de su versión
(ver `scripts/release-notes.ps1`): **agregar la sección antes de crear el tag**.

## [0.7.1] - sin publicar

### Mejoras

- **Privacidad y transparencia.** El instalador muestra antes de instalar qué cambia en el
  sistema (monitor virtual, firewall, inicio con Windows) y qué datos usa NexScreen. Hay una
  [política de privacidad](PRIVACY.md): la única conexión a internet es la búsqueda de
  actualizaciones en GitHub.
- Nueva opción en el menú de la bandeja, "Buscar actualizaciones automáticamente", para
  desactivar esa conexión sin editar archivos.
- El proyecto tiene licencia MIT.

## [0.7.0] - 2026-10-15

### Novedades

- **Más fluidez: los 90 y 120 fps del dispositivo, de verdad.** NexScreen captura y codifica el
  video por su cuenta, sin ffmpeg, y sostiene la frecuencia de la pantalla del dispositivo (90 fps
  estables con la Galaxy Tab S9 FE por USB; antes se quedaba en ~70 aunque la pantalla pidiera 90).
  Funciona con NVIDIA (NVENC) e Intel (Quick Sync); si algo no está disponible, sigue con ffmpeg como
  hasta ahora.

### Mejoras

- El keyframe que pide el dispositivo y los cambios de bitrate por la red se aplican al instante,
  sin reiniciar la captura.
- Con el escritorio quieto, la bandeja muestra menos fps (ya no se repiten cuadros iguales).

## [0.6.0] - 2026-10-15

### Novedades

- **Funciona con más equipos.** Además de Intel Quick Sync, NexScreen codifica el video con
  **NVIDIA (NVENC)** y **AMD (AMF)**, y elige solo el de la GPU a la que está conectada la pantalla
  (también en PCs con dos GPU). Si ninguno funciona prueba Media Foundation por GPU y, al final, por CPU.
- **Celulares además de tablets.** El video ya no queda debajo de la cámara (perforación o muesca):
  el monitor se crea con el área libre de la pantalla.
- **Hasta 120 fps.** Se transmite a la frecuencia de la pantalla del dispositivo (60, 90 o 120 Hz)
  y la app le pide a Android que la use. Con un encoder por CPU se queda en 60 fps.
- **Escala automática.** La escala de Windows se elige según la densidad de la pantalla
  (150 % en una tablet de 11", más en un celular). Es la opción nueva por defecto de "Escala";
  si ya tenías una elegida, se mantiene.
- **Bitrate adaptativo.** Si el WiFi no da abasto, NexScreen baja la calidad del video en vez de
  acumular atraso, y la vuelve a subir de a poco cuando la red se recupera. La calidad elegida en
  la bandeja ("Alta", "Normal", "Ahorro") es ahora el máximo. Mientras está reducida, la bandeja
  lo indica ("red lenta").

### Mejoras

- La calidad ("Alta", "Normal", "Ahorro") se ajusta a la resolución y los fps del dispositivo: un
  celular de 120 Hz recibe más bitrate que una tablet de 60 Hz con la misma opción.
- Si la tablet tiene un error al decodificar el video, se recupera sola en menos de un segundo
  (le pide al PC una imagen completa) en vez de cortar la conexión.
- Los reinicios de la captura (por ejemplo, al reordenar las pantallas) ya no reinician el
  decoder de la tablet cuando no cambió la configuración del video.

## [0.5.0] - 2026-10-14

### Novedades

- **Actualización automática del PC.** La bandeja busca versiones nuevas al iniciar y una vez por
  día, te avisa con una notificación y la instala con un clic (pide permiso de administrador y
  vuelve a abrirse sola). También puedes buscar a mano con "Buscar actualizaciones" en el menú.
- **Conexión por WiFi cifrada y con emparejamiento.** Todo el tráfico por WiFi va cifrado (TLS).
  La primera vez que una tablet se conecta, el PC muestra un código de 6 dígitos que hay que
  ingresar en la tablet; después se conecta sola. Por USB no cambia nada.
- Menú "Tablets emparejadas (WiFi)" en la bandeja para olvidar una tablet.

### Mejoras

- Un equipo de la red que no está emparejado ya no puede cortar la conexión de tu tablet.
- El instalador cierra el servicio de adb de NexScreen antes de actualizar (antes podía fallar
  con el archivo en uso).

### Importante

- Protocolo v2: la app de la tablet tiene que ser la 0.5.0. Se actualiza sola al enchufarla por
  USB con NexScreen abierto en el PC.
- Si vienes de la 0.4.0, esta versión se instala a mano (la 0.4.0 no tiene el actualizador).
- Al actualizar, la tablet tiene que emparejarse una vez para usar WiFi.

## [0.4.0] - 2026-10-14

Primera versión publicada.

- Usa una tablet Android como monitor extendido (o espejo) del PC, por USB o WiFi.
- Táctil y S Pen con presión hacia Windows.
- App de bandeja: modo, posición, calidad y escala de la tablet; inicia con Windows.
- Autoconexión por USB: al enchufar la tablet le instala la app, la abre y se conecta.
- Descubrimiento del PC por WiFi desde la tablet.
- Instalador para Windows con el monitor virtual y las reglas del firewall.

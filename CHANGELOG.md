# Cambios

Lo nuevo de cada versión de NexScreen. Cada release de GitHub muestra la sección de su versión
(ver `scripts/release-notes.ps1`): **agregar la sección antes de crear el tag**.

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

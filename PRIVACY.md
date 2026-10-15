# Política de privacidad de NexScreen

NexScreen no recopila, vende ni envía datos personales, y no incluye analítica, publicidad ni
informes de errores.

## Conexiones a internet

La única conexión a internet que hace NexScreen es la **búsqueda de actualizaciones**:

- **Qué hace:** la app de bandeja consulta `https://api.github.com/repos/AlexPerez7/NexScreen/releases/latest`
  al minuto de iniciar y luego cada 24 horas. Si hay una versión nueva, la descarga desde GitHub
  solo cuando tú eliges "Instalar la versión X.Y.Z".
- **Qué se envía:** una consulta HTTPS normal a GitHub (con la versión de NexScreen en el
  `User-Agent`). GitHub puede registrar tu dirección IP según su
  [política de privacidad](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).
  NexScreen no envía nada más, ni a GitHub ni a otros servidores.
- **Cómo desactivarla:** en el menú del ícono de la bandeja, desmarca "Buscar actualizaciones
  automáticamente" (o pon `"checkForUpdates": false` en `%APPDATA%\NexScreen\settings.json`). Con
  eso NexScreen no hace ninguna conexión a internet.

## Conexión con tu dispositivo

El video del escritorio y el táctil viajan solo entre tu PC y tu dispositivo Android, por el cable
USB o por tu red local. Por WiFi van cifrados (TLS) y solo con dispositivos que emparejaste con el
PIN. Nada pasa por servidores de terceros.

## Datos que guarda NexScreen en tu PC

- `%APPDATA%\NexScreen\`: preferencias, posición del monitor virtual y la lista de dispositivos
  emparejados.
- `%LOCALAPPDATA%\NexScreen\logs\`: registro de la app (no incluye el contenido de la pantalla).
- El certificado "NexScreen host" en el almacén de certificados de tu usuario, usado para cifrar
  la conexión WiFi.

Desinstalar NexScreen quita el programa, el monitor virtual y las reglas del firewall; estos
datos de usuario puedes borrarlos a mano.

# Protocolo NexScreen (v2)

TCP, puerto por defecto **27190**. El host escucha; la tablet se conecta (por WiFi directo o
por USB a través de `adb reverse`). Hay una sola sesión activa: una conexión nueva reemplaza a la
anterior, pero **solo después de completar el handshake** (TLS, Hello y, si hace falta, el
emparejamiento). Una conexión que no se autentica no corta la sesión de la tablet.

## Transporte

| Conexión | Cómo la reconoce el host | Transporte |
|----------|--------------------------|------------|
| USB (`adb reverse`) | Llega desde 127.0.0.1 | TCP sin cifrar: el cable ya implica confianza |
| WiFi | Cualquier otra dirección | **TLS** (1.2 o 1.3) con certificados de ambos lados |

Por WiFi, el host presenta un certificado autofirmado (ECDSA P-256, guardado en el almacén
`CurrentUser\My` con el nombre "NexScreen host") y exige uno de la tablet (EC P-256 en el
Android Keystore). No hay CA: cada lado guarda la **huella** del otro (SHA-256 del certificado
DER, en hexadecimal en minúsculas) al emparejarse.

## Framing

Todos los mensajes, en ambas direcciones:

```
+---------+------------------+-----------------+
| u8 tipo | u32 largo (BE)   | payload (largo) |
+---------+------------------+-----------------+
```

Enteros en big-endian. Payload máximo: 16 MiB.

## Mensajes

| Tipo | Nombre | Dirección | Payload |
|------|--------|-----------|---------|
| 0x01 | HELLO  | tablet → host | JSON `{"protocol":2,"device":"samsung SM-X510","width":2304,"height":1440,"dpi":274,"paired":true}` |
| 0x02 | CONFIG | host → tablet | JSON `{"protocol":2,"codec":"h264","maxWidth":2304,"maxHeight":1440,"fps":60}` |
| 0x03 | PAIR_REQUIRED | host → tablet | JSON `{"host":"ALEXP-PC"}` (solo WiFi) |
| 0x04 | PAIR | tablet → host | JSON `{"proof":"<hex>"}` |
| 0x05 | PAIR_RESULT | host → tablet | JSON `{"ok":false,"error":"Código incorrecto.","retry":true}` |
| 0x10 | VIDEO  | host → tablet | `[i64 ptsUs][u8 flags][access unit H.264 Annex-B]` |
| 0x20 | POINTER | tablet → host | `[u8 cantidad]` + cantidad × entrada de 12 bytes (ver abajo) |
| 0x30 | PING   | ambos | `[i64 timestamp µs del emisor]` |
| 0x31 | PONG   | ambos | eco exacto del payload del PING |

### Flags de VIDEO

| Bit | Significado |
|-----|-------------|
| 0x01 | Keyframe (IDR) |
| 0x02 | Configuración del codec (solo SPS/PPS); se pasa al decoder con `BUFFER_FLAG_CODEC_CONFIG` |

### Entrada de POINTER (12 bytes)

```
[u8 id][u8 herramienta][u8 acción][u8 botones][u16 x][u16 y][u16 presión][i8 tiltX][i8 tiltY]
```

| Campo | Valores |
|-------|---------|
| id | Id del puntero en Android (`getPointerId`) |
| herramienta | 0 dedo, 1 lápiz, 2 borrador |
| acción | 0 down, 1 move, 2 up, 3 cancel, 4 hover (lápiz cerca sin tocar), 5 hover-exit |
| botones | bit 0: botón lateral del lápiz |
| x, y | 0..65535 sobre el área del video (0 = izquierda/arriba) |
| presión | 0..65535 (los dedos envían 65535) |
| tiltX, tiltY | Inclinación del lápiz en grados, −90..90 |

Todas las entradas de dedos de un mensaje forman **un frame**: el host las inyecta juntas con
`InjectSyntheticPointerInput`, como exige Windows para el multitáctil. Las del lápiz se inyectan
una por una. Un `move` de un dedo desconocido se trata como `down`.

`paired` en HELLO: la tablet ya confía en el certificado de este host (lo tiene guardado). Por
USB se ignora.

## Emparejamiento (solo WiFi)

El host pide el PIN si la tablet no está en su lista (`%APPDATA%\NexScreen\paired-devices.json`)
**o** si la tablet informa `paired: false`. Hace falta que confíen los dos: si la tablet no
reconoce el certificado del PC, no alcanza con que el PC la conozca a ella.

1. El host genera un PIN de 6 dígitos, lo muestra en una ventana y envía PAIR_REQUIRED.
2. La tablet le pide el PIN al usuario y envía PAIR con
   `proof = hex(HMAC-SHA256(clave = PIN, mensaje = "NexScreen pairing v2\n<huella host>\n<huella tablet>"))`.
   Las huellas atan la prueba a esta conexión TLS: no se puede reusar en otra.
3. El host la compara (en tiempo constante) y responde PAIR_RESULT. Si es correcta guarda la
   huella de la tablet y sigue con CONFIG; la tablet guarda la del host.
4. Con un PIN incorrecto, `retry: true` permite otro intento. Tras **3 intentos**, a los
   **2 minutos** o si el usuario cancela en el PC, PAIR_RESULT llega con `retry: false` y el host
   cierra la conexión. Hay un solo emparejamiento a la vez.

La tablet **no acepta CONFIG ni envía input** por WiFi hasta que el host es de confianza (estaba
guardado o se acaba de emparejar). Así un equipo que se haga pasar por el PC no puede saltarse el PIN.

Ejemplo para verificar implementaciones: PIN `123456`, huella del host `aa11` y de la tablet
`bb22` dan `d0de3459577ccda1a0a23d616cc7427506893c97c5f4f8e38074832597cbe4af`.

**Límite conocido:** un atacante que intercepte activamente la red (man-in-the-middle) justo durante
el emparejamiento podría probar los 10⁶ PINs posibles fuera de línea. Después de emparejar, las
huellas fijadas lo impiden. Para cerrar ese caso haría falta un PAKE (por ejemplo, SPAKE2).

## Descubrimiento (UDP 27191)

Para conectarse por WiFi sin escribir la IP, la tablet envía por UDP un broadcast al puerto
**27191** (a `255.255.255.255` y al broadcast de su subred, tres veces en ~2 s) con el texto ASCII:

```
NEXSCREEN_DISCOVER 1
```

Cada host responde por unicast al origen con un JSON:

```json
{"protocol":2,"name":"ALEXP-ASSERTIVA","port":27190,"version":"0.5.0"}
```

La tablet descarta respuestas con otra versión de protocolo y muestra los hosts encontrados.
En Windows hacen falta reglas de firewall para TCP 27190 y UDP 27191 (`scripts/allow-firewall.ps1`),
y la red tiene que estar marcada como Privada.

## Secuencia

1. La tablet conecta (por WiFi, primero el handshake TLS) y envía **HELLO** con la resolución
   física de la pantalla. Si por WiFi no están emparejados, sigue el emparejamiento.
2. El host valida la versión y responde **CONFIG**. El video se escala para entrar en
   `maxWidth × maxHeight` conservando el aspecto, **sin agrandar**; el tamaño real lo
   informa el decoder (`INFO_OUTPUT_FORMAT_CHANGED`).
3. El host envía un VIDEO de configuración (SPS/PPS) y luego frames, empezando por un keyframe.
4. La tablet envía PING cada segundo para medir el RTT, y POINTER con cada toque o movimiento del lápiz.
5. Cualquiera de los dos puede cerrar el socket para terminar la sesión.

## Compatibilidad

Una versión de protocolo distinta entre host y tablet aborta la sesión en el handshake.
Los tipos de mensaje nuevos se agregan sin subir la versión si el otro extremo puede ignorarlos
(POINTER se agregó así: un host viejo lo descarta). La v2 agregó TLS y el emparejamiento por WiFi:
es incompatible con la v1 (la app de la tablet se actualiza sola al enchufarla por USB).
Cambios previstos: pedido de keyframe y bitrate adaptativo.

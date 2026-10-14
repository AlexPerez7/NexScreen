# Protocolo NexScreen (v1)

TCP, puerto por defecto **27190**. El host escucha; la tablet se conecta (por WiFi directo o
por USB a través de `adb reverse`). Hay una sola sesión activa: una conexión nueva reemplaza a la anterior.

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
| 0x01 | HELLO  | tablet → host | JSON `{"protocol":1,"device":"samsung SM-X510","width":2304,"height":1440,"dpi":274}` |
| 0x02 | CONFIG | host → tablet | JSON `{"protocol":1,"codec":"h264","maxWidth":2304,"maxHeight":1440,"fps":60}` |
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

## Descubrimiento (UDP 27191)

Para conectarse por WiFi sin escribir la IP, la tablet envía por UDP un broadcast al puerto
**27191** (a `255.255.255.255` y al broadcast de su subred, tres veces en ~2 s) con el texto ASCII:

```
NEXSCREEN_DISCOVER 1
```

Cada host responde por unicast al origen con un JSON:

```json
{"protocol":1,"name":"ALEXP-ASSERTIVA","port":27190,"version":"0.4.0"}
```

La tablet descarta respuestas con otra versión de protocolo y muestra los hosts encontrados.
En Windows hacen falta reglas de firewall para TCP 27190 y UDP 27191 (`scripts/allow-firewall.ps1`),
y la red tiene que estar marcada como Privada.

## Secuencia

1. La tablet conecta y envía **HELLO** con la resolución física de la pantalla.
2. El host valida la versión y responde **CONFIG**. El video se escala para entrar en
   `maxWidth × maxHeight` conservando el aspecto, **sin agrandar**; el tamaño real lo
   informa el decoder (`INFO_OUTPUT_FORMAT_CHANGED`).
3. El host envía un VIDEO de configuración (SPS/PPS) y luego frames, empezando por un keyframe.
4. La tablet envía PING cada segundo para medir el RTT, y POINTER con cada toque o movimiento del lápiz.
5. Cualquiera de los dos puede cerrar el socket para terminar la sesión.

## Compatibilidad

Una versión de protocolo distinta entre host y tablet aborta la sesión en el handshake.
Los tipos de mensaje nuevos se agregan sin subir la versión si el otro extremo puede ignorarlos
(POINTER se agregó así: un host viejo lo descarta). Cambios previstos: pedido de keyframe y
bitrate adaptativo.

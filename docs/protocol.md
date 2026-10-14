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
| 0x30 | PING   | ambos | `[i64 timestamp µs del emisor]` |
| 0x31 | PONG   | ambos | eco exacto del payload del PING |

### Flags de VIDEO

| Bit | Significado |
|-----|-------------|
| 0x01 | Keyframe (IDR) |
| 0x02 | Configuración del codec (solo SPS/PPS); se pasa al decoder con `BUFFER_FLAG_CODEC_CONFIG` |

## Secuencia

1. La tablet conecta y envía **HELLO** con la resolución física de la pantalla.
2. El host valida la versión y responde **CONFIG**. El video se escala para entrar en
   `maxWidth × maxHeight` conservando el aspecto, **sin agrandar**; el tamaño real lo
   informa el decoder (`INFO_OUTPUT_FORMAT_CHANGED`).
3. El host envía un VIDEO de configuración (SPS/PPS) y luego frames, empezando por un keyframe.
4. La tablet envía PING cada segundo para medir el RTT.
5. Cualquiera de los dos puede cerrar el socket para terminar la sesión.

## Compatibilidad

Una versión de protocolo distinta entre host y tablet aborta la sesión en el handshake.
Cambios previstos para v2: mensajes de input (táctil/S Pen), pedido de keyframe y bitrate adaptativo.

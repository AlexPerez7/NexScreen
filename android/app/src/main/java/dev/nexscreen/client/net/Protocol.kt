package dev.nexscreen.client.net

import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.Socket

/**
 * Constantes del protocolo. Ver docs/protocol.md; cualquier cambio debe reflejarse
 * también en el host (Wire.cs).
 */
object Protocol {
    const val VERSION = 1
    const val DEFAULT_PORT = 27190
    const val MAX_PAYLOAD = 16 * 1024 * 1024

    const val TYPE_HELLO = 0x01
    const val TYPE_CONFIG = 0x02
    const val TYPE_VIDEO = 0x10
    const val TYPE_POINTER = 0x20
    const val TYPE_PING = 0x30
    const val TYPE_PONG = 0x31

    /** [u64 ptsUs][u8 flags] antes del access unit Annex-B. */
    const val VIDEO_PREFIX_SIZE = 9
    const val FLAG_KEY_FRAME = 1
    const val FLAG_CONFIG = 2

    /** POINTER: [u8 cantidad] + cantidad × 12 bytes. Ver docs/protocol.md. */
    const val POINTER_ENTRY_SIZE = 12
    const val TOOL_FINGER = 0
    const val TOOL_PEN = 1
    const val TOOL_ERASER = 2
    const val ACTION_DOWN = 0
    const val ACTION_MOVE = 1
    const val ACTION_UP = 2
    const val ACTION_CANCEL = 3
    const val ACTION_HOVER = 4
    const val ACTION_HOVER_EXIT = 5
    const val BUTTON_BARREL = 1
}

class Message(val type: Int, val payload: ByteArray)

/** Framing de mensajes: [u8 tipo][u32 largo big-endian][payload]. */
class MessageChannel(socket: Socket) {
    private val input = DataInputStream(BufferedInputStream(socket.getInputStream(), 256 * 1024))
    private val output = DataOutputStream(BufferedOutputStream(socket.getOutputStream()))

    /** Lee el próximo mensaje. Lanza EOFException si el host cerró la conexión. */
    fun read(): Message {
        val type = input.readUnsignedByte()
        val length = input.readInt()
        if (length < 0 || length > Protocol.MAX_PAYLOAD) throw IOException("Mensaje inválido: $length bytes")
        val payload = ByteArray(length)
        input.readFully(payload)
        return Message(type, payload)
    }

    @Synchronized
    fun write(type: Int, payload: ByteArray) {
        output.writeByte(type)
        output.writeInt(payload.size)
        output.write(payload)
        output.flush()
    }
}

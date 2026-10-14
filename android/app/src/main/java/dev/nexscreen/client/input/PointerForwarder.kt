package dev.nexscreen.client.input

import android.os.SystemClock
import android.view.MotionEvent
import android.view.View
import dev.nexscreen.client.net.Protocol
import java.nio.ByteBuffer
import kotlin.math.atan
import kotlin.math.cos
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.tan

/**
 * Convierte los eventos de dedos y S Pen sobre el video en mensajes POINTER para el host.
 *
 * - Coordenadas normalizadas 0..1 sobre la vista (que tiene exactamente el área del video).
 * - Muestras históricas incluidas: Android agrupa los movimientos por frame y sin ellas los
 *   trazos del lápiz salen poligonales.
 * - Rechazo de palma: mientras el lápiz está cerca se ignoran los dedos, y un dedo que ya estaba
 *   apoyado se cancela.
 */
class PointerForwarder(private val send: (ByteArray) -> Unit) : View.OnTouchListener, View.OnGenericMotionListener {

    private data class Entry(
        val id: Int,
        val tool: Int,
        val action: Int,
        val barrel: Boolean,
        val x: Float,
        val y: Float,
        val pressure: Float,
        val tiltX: Int,
        val tiltY: Int,
    )

    private var penInRange = false
    private var penNearUntil = 0L
    private val activeFingers = mutableMapOf<Int, Entry>()
    private val rejectedFingers = mutableSetOf<Int>()

    override fun onTouch(view: View, event: MotionEvent): Boolean {
        if (event.actionMasked == MotionEvent.ACTION_DOWN) {
            // Entrega los eventos apenas llegan, sin esperar al próximo vsync: menos latencia.
            view.requestUnbufferedDispatch(event)
        }
        if (hasPen(event)) markPenNear(inRange = true)

        if (event.actionMasked == MotionEvent.ACTION_MOVE) {
            for (h in 0 until event.historySize) {
                sendEntries(collect(view, event, h))
            }
        }
        sendEntries(collect(view, event, HISTORY_CURRENT))

        if (hasPen(event) && event.actionMasked in setOf(MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL)) {
            markPenNear(inRange = true) // sigue cerca hasta el HOVER_EXIT
        }
        return true
    }

    override fun onGenericMotion(view: View, event: MotionEvent): Boolean {
        if (!hasPen(event)) return false

        val action = when (event.actionMasked) {
            MotionEvent.ACTION_HOVER_ENTER, MotionEvent.ACTION_HOVER_MOVE -> Protocol.ACTION_HOVER
            MotionEvent.ACTION_HOVER_EXIT -> Protocol.ACTION_HOVER_EXIT
            else -> return false
        }

        if (action == Protocol.ACTION_HOVER) markPenNear(inRange = true) else markPenNear(inRange = false)
        sendEntries(listOf(entry(view, event, 0, HISTORY_CURRENT, action)))
        return true
    }

    private fun collect(view: View, event: MotionEvent, history: Int): List<Entry> {
        val entries = mutableListOf<Entry>()
        val penNear = penInRange || SystemClock.uptimeMillis() < penNearUntil

        for (index in 0 until event.pointerCount) {
            val action = pointerAction(event, index, history)
            val entry = entry(view, event, index, history, action)

            if (entry.tool != Protocol.TOOL_FINGER) {
                entries += entry
                continue
            }

            if (entry.id in rejectedFingers) {
                if (action == Protocol.ACTION_UP || action == Protocol.ACTION_CANCEL) rejectedFingers -= entry.id
                continue
            }
            if (penNear && action == Protocol.ACTION_DOWN) {
                rejectedFingers += entry.id
                continue
            }

            entries += entry
            if (action == Protocol.ACTION_UP || action == Protocol.ACTION_CANCEL) {
                activeFingers -= entry.id
            } else {
                activeFingers[entry.id] = entry
            }
        }
        return entries
    }

    private fun pointerAction(event: MotionEvent, index: Int, history: Int): Int {
        if (history != HISTORY_CURRENT) return Protocol.ACTION_MOVE
        val isActionPointer = index == event.actionIndex
        return when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> Protocol.ACTION_DOWN
            MotionEvent.ACTION_POINTER_DOWN -> if (isActionPointer) Protocol.ACTION_DOWN else Protocol.ACTION_MOVE
            MotionEvent.ACTION_UP -> Protocol.ACTION_UP
            MotionEvent.ACTION_POINTER_UP -> if (isActionPointer) Protocol.ACTION_UP else Protocol.ACTION_MOVE
            MotionEvent.ACTION_CANCEL -> Protocol.ACTION_CANCEL
            else -> Protocol.ACTION_MOVE
        }
    }

    private fun entry(view: View, event: MotionEvent, index: Int, history: Int, action: Int): Entry {
        val tool = when (event.getToolType(index)) {
            MotionEvent.TOOL_TYPE_STYLUS -> Protocol.TOOL_PEN
            MotionEvent.TOOL_TYPE_ERASER -> Protocol.TOOL_ERASER
            else -> Protocol.TOOL_FINGER
        }
        val current = history == HISTORY_CURRENT
        val x = if (current) event.getX(index) else event.getHistoricalX(index, history)
        val y = if (current) event.getY(index) else event.getHistoricalY(index, history)
        val pressure = if (tool == Protocol.TOOL_FINGER) 1f
        else if (current) event.getPressure(index) else event.getHistoricalPressure(index, history)

        // Inclinación: AXIS_TILT es el ángulo con la vertical y AXIS_ORIENTATION hacia dónde apunta.
        val tilt = if (current) event.getAxisValue(MotionEvent.AXIS_TILT, index)
        else event.getHistoricalAxisValue(MotionEvent.AXIS_TILT, index, history)
        val orientation = if (current) event.getAxisValue(MotionEvent.AXIS_ORIENTATION, index)
        else event.getHistoricalAxisValue(MotionEvent.AXIS_ORIENTATION, index, history)
        val tiltX = Math.toDegrees(atan(tan(tilt) * sin(orientation)).toDouble()).roundToInt()
        val tiltY = Math.toDegrees(atan(tan(tilt) * -cos(orientation)).toDouble()).roundToInt()

        // El botón del S Pen se reporta como STYLUS_PRIMARY o, en algunos Samsung, como SECONDARY.
        val barrel = event.buttonState and
            (MotionEvent.BUTTON_STYLUS_PRIMARY or MotionEvent.BUTTON_SECONDARY) != 0

        return Entry(
            id = event.getPointerId(index),
            tool = tool,
            action = action,
            barrel = barrel,
            x = (x / view.width).coerceIn(0f, 1f),
            y = (y / view.height).coerceIn(0f, 1f),
            pressure = pressure.coerceIn(0f, 1f),
            tiltX = tiltX.coerceIn(-90, 90),
            tiltY = tiltY.coerceIn(-90, 90),
        )
    }

    private fun hasPen(event: MotionEvent): Boolean = (0 until event.pointerCount).any {
        val tool = event.getToolType(it)
        tool == MotionEvent.TOOL_TYPE_STYLUS || tool == MotionEvent.TOOL_TYPE_ERASER
    }

    /** Al acercarse el lápiz se cancelan los dedos apoyados (la palma suele tocar primero). */
    private fun markPenNear(inRange: Boolean) {
        if (inRange && !penInRange && activeFingers.isNotEmpty()) {
            sendEntries(activeFingers.values.map { it.copy(action = Protocol.ACTION_CANCEL) })
            rejectedFingers += activeFingers.keys
            activeFingers.clear()
        }
        penInRange = inRange
        penNearUntil = SystemClock.uptimeMillis() + PALM_REJECTION_GRACE_MS
    }

    private fun sendEntries(entries: List<Entry>) {
        if (entries.isEmpty()) return
        val buffer = ByteBuffer.allocate(1 + entries.size.coerceAtMost(MAX_ENTRIES) * Protocol.POINTER_ENTRY_SIZE)
        buffer.put(entries.size.coerceAtMost(MAX_ENTRIES).toByte())
        for (e in entries.take(MAX_ENTRIES)) {
            buffer.put(e.id.toByte())
            buffer.put(e.tool.toByte())
            buffer.put(e.action.toByte())
            buffer.put((if (e.barrel) Protocol.BUTTON_BARREL else 0).toByte())
            buffer.putShort(toUInt16(e.x))
            buffer.putShort(toUInt16(e.y))
            buffer.putShort(toUInt16(e.pressure))
            buffer.put(e.tiltX.toByte())
            buffer.put(e.tiltY.toByte())
        }
        send(buffer.array())
    }

    private fun toUInt16(value: Float): Short = (value * 65535f).roundToInt().coerceIn(0, 65535).toShort()

    private companion object {
        const val HISTORY_CURRENT = -1
        const val MAX_ENTRIES = 255
        const val PALM_REJECTION_GRACE_MS = 500L
    }
}

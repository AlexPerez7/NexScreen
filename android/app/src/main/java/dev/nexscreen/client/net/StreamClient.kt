package dev.nexscreen.client.net

import android.os.Build
import android.os.SystemClock
import android.util.Log
import android.view.Surface
import dev.nexscreen.client.video.VideoDecoder
import org.json.JSONObject
import java.io.EOFException
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.util.concurrent.atomic.AtomicInteger

data class DisplayInfo(val width: Int, val height: Int, val dpi: Int)

data class StreamStats(val fps: Float, val mbps: Float, val rttMs: Float?)

/**
 * Conexión con el host: handshake, recepción de video y ping de latencia.
 * Corre en su propio hilo; los callbacks del [Listener] llegan desde hilos de fondo.
 */
class StreamClient(
    private val host: String,
    private val port: Int,
    private val surface: Surface,
    private val display: DisplayInfo,
    private val listener: Listener,
) {
    interface Listener {
        fun onConnecting()
        fun onConnected()
        fun onVideoSize(width: Int, height: Int)
        fun onStats(stats: StreamStats)

        /** La conexión terminó sin que se llamara a [stop]. */
        fun onDisconnected(error: Throwable?)
    }

    @Volatile
    private var running = false

    @Volatile
    private var socket: Socket? = null
    private var thread: Thread? = null

    private val renderedFrames = AtomicInteger()
    private var receivedBytes = 0L
    private var lastRttMs: Float? = null

    fun start() {
        running = true
        thread = Thread(::run, "nexscreen-net").apply { start() }
    }

    fun stop() {
        running = false
        socket?.close()
        thread?.join(1000)
    }

    private fun run() {
        var decoder: VideoDecoder? = null
        var error: Throwable? = null
        try {
            listener.onConnecting()
            // connect() fuera de apply{}: adentro, `port` resolvería a Socket.getPort() (0).
            val socket = Socket().apply {
                tcpNoDelay = true
                receiveBufferSize = 1 shl 20
            }
            this.socket = socket
            socket.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
            if (!running) return

            val channel = MessageChannel(socket)
            channel.write(Protocol.TYPE_HELLO, helloPayload())

            var lastPing = 0L
            var lastStats = SystemClock.elapsedRealtime()
            while (running) {
                val message = channel.read()
                when (message.type) {
                    Protocol.TYPE_CONFIG -> {
                        val config = JSONObject(String(message.payload, Charsets.UTF_8))
                        checkConfig(config)
                        decoder?.release()
                        decoder = VideoDecoder(
                            surface,
                            config.getInt("maxWidth"),
                            config.getInt("maxHeight"),
                            decoderListener,
                        ).also { it.start() }
                        listener.onConnected()
                    }
                    Protocol.TYPE_VIDEO -> {
                        val payload = message.payload
                        if (payload.size <= Protocol.VIDEO_PREFIX_SIZE) continue
                        val prefix = ByteBuffer.wrap(payload, 0, Protocol.VIDEO_PREFIX_SIZE)
                        val ptsUs = prefix.long
                        val flags = prefix.get().toInt()
                        decoder?.queue(
                            payload,
                            Protocol.VIDEO_PREFIX_SIZE,
                            payload.size - Protocol.VIDEO_PREFIX_SIZE,
                            ptsUs,
                            flags,
                        )
                        receivedBytes += payload.size
                    }
                    Protocol.TYPE_PING -> channel.write(Protocol.TYPE_PONG, message.payload)
                    Protocol.TYPE_PONG -> {
                        val sentUs = ByteBuffer.wrap(message.payload).long
                        lastRttMs = (nowUs() - sentUs) / 1000f
                    }
                    else -> Log.d(TAG, "Mensaje ignorado: ${message.type}")
                }

                val now = SystemClock.elapsedRealtime()
                if (now - lastPing >= PING_INTERVAL_MS) {
                    channel.write(Protocol.TYPE_PING, ByteBuffer.allocate(8).putLong(nowUs()).array())
                    lastPing = now
                }
                if (now - lastStats >= STATS_INTERVAL_MS) {
                    val seconds = (now - lastStats) / 1000f
                    listener.onStats(
                        StreamStats(
                            fps = renderedFrames.getAndSet(0) / seconds,
                            mbps = receivedBytes * 8 / seconds / 1_000_000f,
                            rttMs = lastRttMs,
                        ),
                    )
                    receivedBytes = 0
                    lastStats = now
                }
            }
        } catch (t: Throwable) {
            if (running) {
                error = if (t is EOFException) null else t
                Log.w(TAG, "Conexión terminada", t)
            }
        } finally {
            decoder?.release()
            socket?.close()
            if (running) listener.onDisconnected(error)
        }
    }

    private val decoderListener = object : VideoDecoder.Listener {
        override fun onVideoSize(width: Int, height: Int) = listener.onVideoSize(width, height)
        override fun onFrameRendered() {
            renderedFrames.incrementAndGet()
        }
    }

    private fun helloPayload(): ByteArray = JSONObject()
        .put("protocol", Protocol.VERSION)
        .put("device", "${Build.MANUFACTURER} ${Build.MODEL}")
        .put("width", display.width)
        .put("height", display.height)
        .put("dpi", display.dpi)
        .toString()
        .toByteArray(Charsets.UTF_8)

    private fun checkConfig(config: JSONObject) {
        val protocol = config.getInt("protocol")
        if (protocol != Protocol.VERSION) {
            throw IllegalStateException("Versión de protocolo incompatible: host $protocol, app ${Protocol.VERSION}")
        }
        val codec = config.getString("codec")
        if (codec != "h264") throw IllegalStateException("Codec no soportado: $codec")
    }

    private fun nowUs() = SystemClock.elapsedRealtimeNanos() / 1000

    private companion object {
        const val TAG = "StreamClient"
        const val CONNECT_TIMEOUT_MS = 3000
        const val PING_INTERVAL_MS = 1000L
        const val STATS_INTERVAL_MS = 1000L
    }
}

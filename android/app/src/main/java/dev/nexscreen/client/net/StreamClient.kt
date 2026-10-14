package dev.nexscreen.client.net

import android.os.Build
import android.os.SystemClock
import android.util.Log
import android.view.Surface
import dev.nexscreen.client.video.DecoderFailedException
import dev.nexscreen.client.video.VideoDecoder
import org.json.JSONObject
import java.io.EOFException
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.atomic.AtomicInteger
import javax.net.ssl.SSLSocket

/** Pantalla del dispositivo: área útil en píxeles, densidad de Android y frecuencia máxima en Hz. */
data class DisplayInfo(val width: Int, val height: Int, val dpi: Int, val refreshRate: Int)

data class StreamStats(val fps: Float, val mbps: Float, val rttMs: Float?)

/** El emparejamiento falló sin posibilidad de reintentar (no tiene sentido reconectar sola). */
class PairingFailedException(message: String) : Exception(message)

/**
 * Conexión con el host: handshake, recepción de video y ping de latencia.
 * Corre en su propio hilo; los callbacks del [Listener] llegan desde hilos de fondo.
 *
 * Por USB (127.0.0.1, `adb reverse`) va sin cifrar. Por WiFi va con TLS y solo se acepta video
 * (y se manda input) si el PC está en [TrustedHosts] o el usuario acaba de emparejarlo con el PIN.
 */
class StreamClient(
    private val host: String,
    private val port: Int,
    private val surface: Surface,
    private val display: DisplayInfo,
    private val trustedHosts: TrustedHosts,
    private val listener: Listener,
) {
    interface Listener {
        fun onConnecting()

        /**
         * El PC pide el PIN. Responder con [submitPin] (o [stop] para cancelar).
         * [error]: por qué falló el intento anterior, o null si es el primero.
         */
        fun onPairingRequired(hostName: String, error: String?)
        fun onPaired(hostName: String)
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

    @Volatile
    private var channel: MessageChannel? = null

    /** Canal durante el emparejamiento (antes de que [channel] habilite el input). */
    @Volatile
    private var pairingChannel: MessageChannel? = null

    @Volatile
    private var hostFingerprint: String? = null
    private var thread: Thread? = null

    // Android no permite escribir en la red desde el hilo de UI, que es donde llegan los toques.
    private val inputExecutor = Executors.newSingleThreadExecutor { Thread(it, "nexscreen-input") }

    // Estado del video: solo lo usa el hilo de red.
    private var decoder: VideoDecoder? = null
    private var videoWidth = 0
    private var videoHeight = 0

    /** SPS/PPS vigentes, para configurar un decoder nuevo sin esperar al host. */
    private var codecConfig: ByteArray? = null

    /** Tras recrear el decoder se descartan cuadros hasta el próximo keyframe. */
    private var awaitingKeyframe = false
    private val decoderFailures = ArrayDeque<Long>()

    private val renderedFrames = AtomicInteger()
    private var receivedBytes = 0L
    private var lastRttMs: Float? = null

    fun start() {
        running = true
        thread = Thread(::run, "nexscreen-net").apply { start() }
    }

    fun stop() {
        running = false
        channel = null
        inputExecutor.shutdownNow()
        socket?.close()
        thread?.join(1000)
    }

    /** Envía un mensaje POINTER. Se descarta si todavía no hay conexión. */
    fun sendPointer(payload: ByteArray) {
        val channel = channel ?: return
        try {
            inputExecutor.execute {
                try {
                    channel.write(Protocol.TYPE_POINTER, payload)
                } catch (e: IOException) {
                    // El hilo de red detecta la desconexión y la informa.
                }
            }
        } catch (e: RejectedExecutionException) {
            // Cliente detenido.
        }
    }

    /** Envía el PIN que ingresó el usuario (ver [Listener.onPairingRequired]). */
    fun submitPin(pin: String) {
        val channel = pairingChannel ?: return
        val hostFingerprint = hostFingerprint ?: return
        try {
            inputExecutor.execute {
                try {
                    // El Keystore puede tardar: fuera del hilo de UI.
                    val clientFingerprint = Pairing.fingerprint(TlsIdentity.certificate.encoded)
                    val proof = Pairing.proof(pin, hostFingerprint, clientFingerprint)
                    channel.write(Protocol.TYPE_PAIR, JSONObject().put("proof", proof).toString().toByteArray(Charsets.UTF_8))
                } catch (e: IOException) {
                    // El hilo de red detecta la desconexión y la informa.
                }
            }
        } catch (e: RejectedExecutionException) {
            // Cliente detenido.
        }
    }

    private fun run() {
        var error: Throwable? = null
        try {
            listener.onConnecting()
            // connect() fuera de apply{}: adentro, `port` resolvería a Socket.getPort() (0).
            val tcp = Socket().apply {
                tcpNoDelay = true
                receiveBufferSize = 1 shl 20
            }
            this.socket = tcp
            tcp.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
            if (!running) return

            val usb = host == USB_HOST
            var trusted = usb
            var hostName = host
            val socket = if (usb) tcp else secure(tcp)
            this.socket = socket
            if (socket is SSLSocket) {
                val fingerprint = Pairing.fingerprint(socket.session.peerCertificates[0].encoded)
                hostFingerprint = fingerprint
                trusted = trustedHosts.contains(fingerprint)
            }
            if (!running) return

            val channel = MessageChannel(socket)
            channel.write(Protocol.TYPE_HELLO, helloPayload(paired = trusted && !usb))

            var lastPing = 0L
            var lastStats = SystemClock.elapsedRealtime()
            while (running) {
                val message = channel.read()
                when (message.type) {
                    Protocol.TYPE_PAIR_REQUIRED -> {
                        if (usb) throw IOException("El PC pidió emparejar por USB")
                        hostName = JSONObject(String(message.payload, Charsets.UTF_8)).optString("host", host)
                        trusted = false
                        pairingChannel = channel
                        listener.onPairingRequired(hostName, null)
                    }
                    Protocol.TYPE_PAIR_RESULT -> {
                        val result = JSONObject(String(message.payload, Charsets.UTF_8))
                        val error = result.optString("error").ifEmpty { null }
                        val fingerprint = hostFingerprint
                        when {
                            result.optBoolean("ok") && fingerprint != null && pairingChannel != null -> {
                                trustedHosts.add(fingerprint, hostName)
                                trusted = true
                                pairingChannel = null
                                listener.onPaired(hostName)
                            }
                            result.optBoolean("retry") && pairingChannel != null -> listener.onPairingRequired(hostName, error)
                            else -> throw PairingFailedException(error ?: "El PC rechazó el emparejamiento")
                        }
                    }
                    Protocol.TYPE_CONFIG -> {
                        // Sin esto, un equipo que se hiciera pasar por el PC podría saltarse el PIN.
                        if (!trusted) throw PairingFailedException("El PC no completó el emparejamiento")
                        val config = JSONObject(String(message.payload, Charsets.UTF_8))
                        checkConfig(config)
                        // Recién ahora el input puede ir al PC.
                        this.channel = channel
                        videoWidth = config.getInt("maxWidth")
                        videoHeight = config.getInt("maxHeight")
                        codecConfig = null
                        awaitingKeyframe = false
                        decoderFailures.clear()
                        restartDecoder()
                        listener.onConnected()
                    }
                    Protocol.TYPE_VIDEO -> onVideo(channel, message.payload)
                    Protocol.TYPE_PING -> channel.write(Protocol.TYPE_PONG, message.payload)
                    Protocol.TYPE_PONG -> {
                        val sentUs = ByteBuffer.wrap(message.payload).long
                        lastRttMs = (nowUs() - sentUs) / 1000f
                    }
                    else -> Log.d(TAG, "Mensaje ignorado: ${message.type}")
                }

                val now = SystemClock.elapsedRealtime()
                if (trusted && now - lastPing >= PING_INTERVAL_MS) {
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
            channel = null
            pairingChannel = null
            decoder?.release()
            decoder = null
            socket?.close()
            if (running) listener.onDisconnected(error)
        }
    }

    private fun onVideo(channel: MessageChannel, payload: ByteArray) {
        if (payload.size <= Protocol.VIDEO_PREFIX_SIZE) return
        receivedBytes += payload.size
        val prefix = ByteBuffer.wrap(payload, 0, Protocol.VIDEO_PREFIX_SIZE)
        val ptsUs = prefix.long
        val flags = prefix.get().toInt()
        if (flags and Protocol.FLAG_CONFIG != 0) {
            // SPS/PPS a mitad de sesión: el host reemplazó la captura (keyframe pedido, otro bitrate o
            // un reinicio). Si son los mismos, el decoder sigue sirviendo y el keyframe que viene lo
            // resincroniza; si cambiaron, uno limpio evita artefactos.
            val config = payload.copyOfRange(Protocol.VIDEO_PREFIX_SIZE, payload.size)
            val previous = codecConfig
            if (previous != null && previous.contentEquals(config)) return
            if (previous != null) {
                Log.i(TAG, "Nueva configuración de video, reiniciando el decoder")
                restartDecoder()
            }
            codecConfig = config
        } else if (awaitingKeyframe) {
            if (flags and Protocol.FLAG_KEY_FRAME == 0) return
            awaitingKeyframe = false
        }

        try {
            decoder?.queue(payload, Protocol.VIDEO_PREFIX_SIZE, payload.size - Protocol.VIDEO_PREFIX_SIZE, ptsUs, flags)
        } catch (e: DecoderFailedException) {
            recoverDecoder(channel, e)
        }
    }

    /**
     * El decoder falló (por ejemplo, con un cuadro dañado): en vez de cortar la conexión, crear uno
     * nuevo con los SPS/PPS guardados y pedirle un keyframe al host. Un host que no conoce el pedido
     * lo ignora y el keyframe periódico (cada 2 s) llega igual.
     */
    private fun recoverDecoder(channel: MessageChannel, error: DecoderFailedException) {
        val now = SystemClock.elapsedRealtime()
        while (decoderFailures.isNotEmpty() && now - decoderFailures.first() > DECODER_FAILURE_WINDOW_MS) {
            decoderFailures.removeFirst()
        }
        if (decoderFailures.size >= MAX_DECODER_FAILURES) throw IOException("El decoder falló varias veces seguidas", error)
        decoderFailures.addLast(now)

        Log.w(TAG, "Recuperando el decoder y pidiendo un keyframe", error)
        restartDecoder()
        codecConfig?.let { decoder?.queue(it, 0, it.size, 0, Protocol.FLAG_CONFIG) }
        awaitingKeyframe = true
        channel.write(Protocol.TYPE_REQUEST_KEYFRAME, ByteArray(0))
    }

    private fun restartDecoder() {
        decoder?.release()
        decoder = VideoDecoder(surface, videoWidth, videoHeight, decoderListener).also { it.start() }
    }

    private val decoderListener = object : VideoDecoder.Listener {
        override fun onVideoSize(width: Int, height: Int) = listener.onVideoSize(width, height)
        override fun onFrameRendered() {
            renderedFrames.incrementAndGet()
        }
    }

    /** Handshake TLS (con timeout: un puerto que no habla TLS no debe colgar la conexión). */
    private fun secure(tcp: Socket): SSLSocket {
        val ssl = TlsIdentity.socketFactory().createSocket(tcp, host, port, true) as SSLSocket
        ssl.soTimeout = TLS_TIMEOUT_MS
        ssl.startHandshake()
        ssl.soTimeout = 0
        return ssl
    }

    private fun helloPayload(paired: Boolean): ByteArray = JSONObject()
        .put("protocol", Protocol.VERSION)
        .put("paired", paired)
        .put("device", "${Build.MANUFACTURER} ${Build.MODEL}")
        .put("width", display.width)
        .put("height", display.height)
        .put("dpi", display.dpi)
        .put("refreshRate", display.refreshRate)
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
        const val USB_HOST = "127.0.0.1"
        const val CONNECT_TIMEOUT_MS = 3000
        const val TLS_TIMEOUT_MS = 5000
        const val PING_INTERVAL_MS = 1000L
        const val STATS_INTERVAL_MS = 1000L
        const val MAX_DECODER_FAILURES = 3
        const val DECODER_FAILURE_WINDOW_MS = 10_000L
    }
}

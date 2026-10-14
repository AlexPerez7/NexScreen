package dev.nexscreen.client.video

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.util.Log
import android.view.Surface
import dev.nexscreen.client.net.Protocol

/**
 * Decoder H.264 por hardware que renderiza directo al Surface, configurado para la
 * menor latencia posible: cada frame se muestra apenas sale del decoder.
 */
class VideoDecoder(
    surface: Surface,
    width: Int,
    height: Int,
    private val listener: Listener,
) {
    interface Listener {
        fun onVideoSize(width: Int, height: Int)
        fun onFrameRendered()
    }

    private val codec = MediaCodec.createDecoderByType(MIME)
    private var outputThread: Thread? = null

    @Volatile
    private var running = false

    init {
        val format = MediaFormat.createVideoFormat(MIME, width, height).apply {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            setInteger(MediaFormat.KEY_PRIORITY, 0) // tiempo real
        }
        codec.configure(format, surface, null, 0)
        Log.i(TAG, "Decoder: ${codec.name}")
    }

    fun start() {
        codec.start()
        running = true
        outputThread = Thread(::drainOutput, "nexscreen-decoder").apply { start() }
    }

    /** Encola un access unit Annex-B. Bloquea hasta que el decoder tenga un buffer libre. */
    fun queue(data: ByteArray, offset: Int, length: Int, ptsUs: Long, flags: Int) {
        var index: Int
        do {
            if (!running) return
            index = codec.dequeueInputBuffer(INPUT_TIMEOUT_US)
        } while (index < 0)

        val buffer = codec.getInputBuffer(index) ?: return
        buffer.clear()
        buffer.put(data, offset, length)

        val codecFlags = when {
            flags and Protocol.FLAG_CONFIG != 0 -> MediaCodec.BUFFER_FLAG_CODEC_CONFIG
            flags and Protocol.FLAG_KEY_FRAME != 0 -> MediaCodec.BUFFER_FLAG_KEY_FRAME
            else -> 0
        }
        codec.queueInputBuffer(index, 0, length, ptsUs, codecFlags)
    }

    private fun drainOutput() {
        val info = MediaCodec.BufferInfo()
        while (running) {
            val index = try {
                codec.dequeueOutputBuffer(info, OUTPUT_TIMEOUT_US)
            } catch (e: IllegalStateException) {
                if (running) Log.w(TAG, "Decoder detenido", e)
                break
            }

            when {
                index >= 0 -> {
                    codec.releaseOutputBuffer(index, true)
                    listener.onFrameRendered()
                }
                index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> reportSize(codec.outputFormat)
            }
        }
    }

    private fun reportSize(format: MediaFormat) {
        val hasCrop = format.containsKey(KEY_CROP_RIGHT) && format.containsKey(KEY_CROP_BOTTOM)
        val width = if (hasCrop) format.getInteger(KEY_CROP_RIGHT) - format.getInteger(KEY_CROP_LEFT) + 1
        else format.getInteger(MediaFormat.KEY_WIDTH)
        val height = if (hasCrop) format.getInteger(KEY_CROP_BOTTOM) - format.getInteger(KEY_CROP_TOP) + 1
        else format.getInteger(MediaFormat.KEY_HEIGHT)
        Log.i(TAG, "Video ${width}x$height")
        listener.onVideoSize(width, height)
    }

    fun release() {
        running = false
        outputThread?.join(500)
        try {
            codec.stop()
        } catch (e: IllegalStateException) {
            Log.w(TAG, "Error al detener el decoder", e)
        }
        codec.release()
    }

    private companion object {
        const val TAG = "VideoDecoder"
        const val MIME = MediaFormat.MIMETYPE_VIDEO_AVC
        const val INPUT_TIMEOUT_US = 10_000L
        const val OUTPUT_TIMEOUT_US = 10_000L
        const val KEY_CROP_LEFT = "crop-left"
        const val KEY_CROP_RIGHT = "crop-right"
        const val KEY_CROP_TOP = "crop-top"
        const val KEY_CROP_BOTTOM = "crop-bottom"
    }
}

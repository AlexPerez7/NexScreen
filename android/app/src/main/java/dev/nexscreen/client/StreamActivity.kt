package dev.nexscreen.client

import android.os.Build
import android.os.Bundle
import android.view.SurfaceHolder
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.core.view.isVisible
import dev.nexscreen.client.databinding.ActivityStreamBinding
import dev.nexscreen.client.input.PointerForwarder
import dev.nexscreen.client.net.DisplayInfo
import dev.nexscreen.client.net.Protocol
import dev.nexscreen.client.net.StreamClient
import dev.nexscreen.client.net.StreamStats
import java.util.Locale

/**
 * Pantalla completa que muestra el video del PC. La conexión vive mientras exista el
 * Surface: al salir de la app se corta y al volver se reconecta.
 */
class StreamActivity : AppCompatActivity(), SurfaceHolder.Callback, StreamClient.Listener {

    private lateinit var binding: ActivityStreamBinding
    private var client: StreamClient? = null
    private var videoWidth = 0
    private var videoHeight = 0

    private val host by lazy { intent.getStringExtra(EXTRA_HOST) ?: "127.0.0.1" }
    private val port by lazy { intent.getIntExtra(EXTRA_PORT, Protocol.DEFAULT_PORT) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityStreamBinding.inflate(layoutInflater)
        setContentView(binding.root)

        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            window.attributes.layoutInDisplayCutoutMode =
                WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        }
        hideSystemBars()

        binding.stats.isVisible = intent.getBooleanExtra(EXTRA_SHOW_STATS, false)
        binding.surface.holder.addCallback(this)
        binding.root.addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ -> fitSurfaceToVideo() }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
    }

    private fun hideSystemBars() {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, binding.root).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        val client = StreamClient(host, port, holder.surface, displayInfo(), this).also { it.start() }
        this.client = client
        // Los toques sobre el video se reenvían al PC como táctil y lápiz de Windows.
        val forwarder = PointerForwarder(client::sendPointer)
        binding.surface.setOnTouchListener(forwarder)
        binding.surface.setOnGenericMotionListener(forwarder)
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        binding.surface.setOnTouchListener(null)
        binding.surface.setOnGenericMotionListener(null)
        client?.stop()
        client = null
    }

    /** Resolución física de la pantalla, para que el host no mande más píxeles de los necesarios. */
    @Suppress("DEPRECATION")
    private fun displayInfo(): DisplayInfo {
        val (width, height) = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val bounds = windowManager.maximumWindowMetrics.bounds
            bounds.width() to bounds.height()
        } else {
            val metrics = android.util.DisplayMetrics()
            windowManager.defaultDisplay.getRealMetrics(metrics)
            metrics.widthPixels to metrics.heightPixels
        }
        return DisplayInfo(width, height, resources.displayMetrics.densityDpi)
    }

    /** Ajusta el SurfaceView al aspecto del video (letterbox) para no deformarlo. */
    private fun fitSurfaceToVideo() {
        val containerWidth = binding.root.width
        val containerHeight = binding.root.height
        if (videoWidth == 0 || videoHeight == 0 || containerWidth == 0 || containerHeight == 0) return

        val scale = minOf(containerWidth.toFloat() / videoWidth, containerHeight.toFloat() / videoHeight)
        val width = (videoWidth * scale).toInt()
        val height = (videoHeight * scale).toInt()
        val params = binding.surface.layoutParams as FrameLayout.LayoutParams
        if (params.width != width || params.height != height) {
            params.width = width
            params.height = height
            binding.surface.layoutParams = params
        }
    }

    override fun onConnecting() = runOnUiThread {
        binding.status.text = getString(R.string.status_connecting, host, port)
        binding.status.isVisible = true
    }

    override fun onConnected() = runOnUiThread {
        binding.status.text = getString(R.string.status_waiting_video)
    }

    override fun onVideoSize(width: Int, height: Int) = runOnUiThread {
        videoWidth = width
        videoHeight = height
        binding.status.isVisible = false
        fitSurfaceToVideo()
    }

    override fun onStats(stats: StreamStats) = runOnUiThread {
        if (!binding.stats.isVisible) return@runOnUiThread
        val rtt = stats.rttMs?.let { String.format(Locale.US, "%.1f ms", it) } ?: "—"
        binding.stats.text = String.format(
            Locale.US,
            "%dx%d  %.0f fps  %.1f Mbps  RTT %s",
            videoWidth, videoHeight, stats.fps, stats.mbps, rtt,
        )
    }

    override fun onDisconnected(error: Throwable?) = runOnUiThread {
        val reason = error?.message ?: error?.javaClass?.simpleName ?: "el PC cerró la conexión"
        Toast.makeText(this, getString(R.string.disconnected, reason), Toast.LENGTH_LONG).show()
        finish()
    }

    companion object {
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_SHOW_STATS = "show_stats"
    }
}

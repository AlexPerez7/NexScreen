package dev.nexscreen.client

import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.InputFilter
import android.text.InputType
import android.view.Gravity
import android.view.SurfaceHolder
import android.view.View
import android.view.WindowManager
import android.view.inputmethod.EditorInfo
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.widget.doAfterTextChanged
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.core.view.isVisible
import dev.nexscreen.client.databinding.ActivityStreamBinding
import dev.nexscreen.client.input.PointerForwarder
import dev.nexscreen.client.net.DisplayInfo
import dev.nexscreen.client.net.Pairing
import dev.nexscreen.client.net.PairingFailedException
import dev.nexscreen.client.net.Protocol
import dev.nexscreen.client.net.StreamClient
import dev.nexscreen.client.net.StreamStats
import dev.nexscreen.client.net.TrustedHosts
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
    private var reconnectAttempts = 0
    private val handler = Handler(Looper.getMainLooper())
    private val trustedHosts by lazy { TrustedHosts(this) }
    private var pairingDialog: AlertDialog? = null

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
        reconnectAttempts = 0
        startClient()
    }

    private fun startClient() {
        val holder = binding.surface.holder
        if (!holder.surface.isValid) return
        val client = StreamClient(host, port, holder.surface, displayInfo(), trustedHosts, this).also { it.start() }
        this.client = client
        // Los toques sobre el video se reenvían al PC como táctil y lápiz de Windows.
        val forwarder = PointerForwarder(client::sendPointer)
        binding.surface.setOnTouchListener(forwarder)
        binding.surface.setOnGenericMotionListener(forwarder)
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        handler.removeCallbacksAndMessages(null)
        dismissPairingDialog()
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
        // Mientras se reconecta queda el último cuadro de fondo y el aviso de reconexión.
        if (reconnectAttempts == 0) binding.status.text = getString(R.string.status_connecting, host, port)
        binding.status.isVisible = true
    }

    /** El PC no conoce esta tablet (o la tablet no conoce el PC): pedir el PIN que muestra el PC. */
    override fun onPairingRequired(hostName: String, error: String?) = runOnUiThread {
        if (isFinishing || isDestroyed) return@runOnUiThread
        dismissPairingDialog()
        // Mientras el usuario escribe el PIN no hay que contar esto como reconexión fallida.
        reconnectAttempts = 0
        binding.status.text = getString(R.string.pairing_status, hostName)
        binding.status.isVisible = true

        val input = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_NUMBER
            filters = arrayOf(InputFilter.LengthFilter(Pairing.PIN_LENGTH))
            gravity = Gravity.CENTER
            textSize = 32f
            letterSpacing = 0.3f
            hint = "000000"
            imeOptions = EditorInfo.IME_ACTION_DONE
        }
        val padding = (24 * resources.displayMetrics.density).toInt()
        val container = FrameLayout(this).apply {
            setPadding(padding, padding / 2, padding, 0)
            addView(input)
        }

        val message = buildString {
            append(getString(R.string.pairing_message, hostName))
            if (error != null) append("\n\n").append(error)
        }
        val dialog = MaterialAlertDialogBuilder(this)
            .setTitle(R.string.pairing_title)
            .setMessage(message)
            .setView(container)
            .setCancelable(false)
            .setPositiveButton(R.string.pairing_submit, null)
            .setNegativeButton(R.string.pairing_cancel) { _, _ -> finish() }
            .create()

        fun submit() {
            val pin = input.text?.toString().orEmpty()
            if (pin.length != Pairing.PIN_LENGTH) return
            client?.submitPin(pin)
            binding.status.text = getString(R.string.pairing_checking)
            dismissPairingDialog()
        }

        dialog.setOnShowListener {
            val ok = dialog.getButton(AlertDialog.BUTTON_POSITIVE)
            ok.isEnabled = false
            ok.setOnClickListener { submit() }
            input.doAfterTextChanged { ok.isEnabled = it?.length == Pairing.PIN_LENGTH }
            input.setOnEditorActionListener { _, actionId, _ ->
                (actionId == EditorInfo.IME_ACTION_DONE).also { if (it) submit() }
            }
            input.requestFocus()
        }
        dialog.window?.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE)
        dialog.show()
        pairingDialog = dialog
    }

    override fun onPaired(hostName: String) = runOnUiThread {
        dismissPairingDialog()
        Toast.makeText(this, getString(R.string.pairing_done, hostName), Toast.LENGTH_SHORT).show()
        binding.status.text = getString(R.string.status_waiting_video)
    }

    private fun dismissPairingDialog() {
        pairingDialog?.dismiss()
        pairingDialog = null
    }

    override fun onConnected() = runOnUiThread {
        reconnectAttempts = 0
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

    /**
     * La conexión se cortó sin que el usuario saliera: el host pudo reiniciarse (por ejemplo, al
     * cambiar una opción en la bandeja) o la red tuvo un corte. Se reintenta un rato antes de rendirse.
     */
    override fun onDisconnected(error: Throwable?) = runOnUiThread {
        client = null
        dismissPairingDialog()
        if (isFinishing || isDestroyed) return@runOnUiThread

        // Si el emparejamiento falló (PIN incorrecto, cancelado o vencido) reconectar solo
        // volvería a pedir el PIN: mejor volver a la pantalla inicial y explicar por qué.
        if (error is PairingFailedException || reconnectAttempts >= MAX_RECONNECT_ATTEMPTS) {
            val reason = error?.message ?: error?.javaClass?.simpleName ?: getString(R.string.reason_host_closed)
            val text = if (error is PairingFailedException) R.string.pairing_failed else R.string.disconnected
            Toast.makeText(this, getString(text, reason), Toast.LENGTH_LONG).show()
            finish()
            return@runOnUiThread
        }

        reconnectAttempts++
        binding.status.text = getString(R.string.status_reconnecting)
        binding.status.isVisible = true
        handler.postDelayed(::startClient, RECONNECT_DELAY_MS)
    }

    companion object {
        private const val RECONNECT_DELAY_MS = 1500L

        /** ~30 s de reintentos. */
        private const val MAX_RECONNECT_ATTEMPTS = 20

        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_SHOW_STATS = "show_stats"
    }
}

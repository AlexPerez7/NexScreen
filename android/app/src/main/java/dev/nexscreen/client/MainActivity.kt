package dev.nexscreen.client

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.ViewGroup
import android.view.inputmethod.EditorInfo
import android.widget.LinearLayout
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.edit
import androidx.core.view.isVisible
import com.google.android.material.button.MaterialButton
import dev.nexscreen.client.databinding.ActivityMainBinding
import dev.nexscreen.client.net.DiscoveredHost
import dev.nexscreen.client.net.HostDiscovery
import dev.nexscreen.client.net.Protocol

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private val prefs by lazy { getSharedPreferences("connection", Context.MODE_PRIVATE) }
    private var discoveryThread: Thread? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        binding.hostInput.setText(prefs.getString(KEY_HOST, ""))
        binding.portInput.setText(prefs.getInt(KEY_PORT, Protocol.DEFAULT_PORT).toString())
        binding.showStats.isChecked = prefs.getBoolean(KEY_SHOW_STATS, false)

        // Con USB, `adb reverse` expone el puerto del PC en localhost de la tablet.
        binding.connectUsb.setOnClickListener { connect(USB_HOST) }
        binding.connectWifi.setOnClickListener { connectWifi() }
        binding.rescan.setOnClickListener { startDiscovery() }
        binding.hostInput.setOnEditorActionListener { _, actionId, _ ->
            (actionId == EditorInfo.IME_ACTION_GO).also { if (it) connectWifi() }
        }

        if (savedInstanceState == null) handleAutoConnect(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleAutoConnect(intent)
    }

    override fun onResume() {
        super.onResume()
        startDiscovery()
    }

    override fun onPause() {
        super.onPause()
        discoveryThread?.interrupt()
        discoveryThread = null
    }

    /**
     * El host abre la app con este extra al detectar la tablet por USB
     * (`am start ... --es dev.nexscreen.client.AUTO_CONNECT usb`): conectar sin tocar nada.
     */
    private fun handleAutoConnect(intent: Intent?) {
        if (intent?.getStringExtra(EXTRA_AUTO_CONNECT) != "usb") return
        intent.removeExtra(EXTRA_AUTO_CONNECT)
        connect(USB_HOST)
    }

    /** Busca PCs con NexScreen en la red y los muestra como botones. */
    private fun startDiscovery() {
        discoveryThread?.interrupt()
        binding.discoveredHosts.removeAllViews()
        binding.discoveryStatus.text = getString(R.string.discovery_searching)
        binding.discoveryStatus.isVisible = true
        binding.rescan.isEnabled = false

        discoveryThread = HostDiscovery(this).start(
            onFound = { host -> runOnUiThread { addDiscoveredHost(host) } },
            onFinished = {
                runOnUiThread {
                    if (isFinishing || isDestroyed) return@runOnUiThread
                    binding.rescan.isEnabled = true
                    val found = binding.discoveredHosts.childCount > 0
                    binding.discoveryStatus.isVisible = !found
                    if (!found) binding.discoveryStatus.text = getString(R.string.discovery_none)
                }
            },
        )
    }

    private fun addDiscoveredHost(host: DiscoveredHost) {
        if (isFinishing || isDestroyed) return
        binding.discoveryStatus.isVisible = false
        val button = MaterialButton(this, null, com.google.android.material.R.attr.materialButtonOutlinedStyle).apply {
            text = getString(R.string.discovered_host, host.name, host.address)
            isAllCaps = false
            setOnClickListener { connect(host.address, host.port) }
        }
        binding.discoveredHosts.addView(
            button,
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT),
        )
    }

    private fun connectWifi() {
        val host = binding.hostInput.text?.toString()?.trim().orEmpty()
        if (host.isEmpty()) {
            binding.hostInput.error = getString(R.string.error_host_required)
            return
        }
        connect(host)
    }

    /** Sin [port] se usa el del campo de texto (conexión manual o USB). */
    private fun connect(host: String, port: Int? = null) {
        val targetPort = port ?: binding.portInput.text?.toString()?.toIntOrNull()
        if (targetPort == null || targetPort !in 1..65535) {
            binding.portInput.error = getString(R.string.error_port_invalid)
            return
        }

        prefs.edit {
            if (host != USB_HOST) putString(KEY_HOST, host)
            putInt(KEY_PORT, targetPort)
            putBoolean(KEY_SHOW_STATS, binding.showStats.isChecked)
        }

        startActivity(
            Intent(this, StreamActivity::class.java)
                .putExtra(StreamActivity.EXTRA_HOST, host)
                .putExtra(StreamActivity.EXTRA_PORT, targetPort)
                .putExtra(StreamActivity.EXTRA_SHOW_STATS, binding.showStats.isChecked),
        )
    }

    private companion object {
        const val EXTRA_AUTO_CONNECT = "dev.nexscreen.client.AUTO_CONNECT"
        const val USB_HOST = "127.0.0.1"
        const val KEY_HOST = "host"
        const val KEY_PORT = "port"
        const val KEY_SHOW_STATS = "show_stats"
    }
}

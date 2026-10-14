package dev.nexscreen.client

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.inputmethod.EditorInfo
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.edit
import dev.nexscreen.client.databinding.ActivityMainBinding
import dev.nexscreen.client.net.Protocol

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private val prefs by lazy { getSharedPreferences("connection", Context.MODE_PRIVATE) }

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
        binding.hostInput.setOnEditorActionListener { _, actionId, _ ->
            (actionId == EditorInfo.IME_ACTION_GO).also { if (it) connectWifi() }
        }
    }

    private fun connectWifi() {
        val host = binding.hostInput.text?.toString()?.trim().orEmpty()
        if (host.isEmpty()) {
            binding.hostInput.error = getString(R.string.error_host_required)
            return
        }
        connect(host)
    }

    private fun connect(host: String) {
        val port = binding.portInput.text?.toString()?.toIntOrNull()
        if (port == null || port !in 1..65535) {
            binding.portInput.error = getString(R.string.error_port_invalid)
            return
        }

        prefs.edit {
            if (host != USB_HOST) putString(KEY_HOST, host)
            putInt(KEY_PORT, port)
            putBoolean(KEY_SHOW_STATS, binding.showStats.isChecked)
        }

        startActivity(
            Intent(this, StreamActivity::class.java)
                .putExtra(StreamActivity.EXTRA_HOST, host)
                .putExtra(StreamActivity.EXTRA_PORT, port)
                .putExtra(StreamActivity.EXTRA_SHOW_STATS, binding.showStats.isChecked),
        )
    }

    private companion object {
        const val USB_HOST = "127.0.0.1"
        const val KEY_HOST = "host"
        const val KEY_PORT = "port"
        const val KEY_SHOW_STATS = "show_stats"
    }
}

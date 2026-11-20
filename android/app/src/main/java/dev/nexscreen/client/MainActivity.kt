package dev.nexscreen.client

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.view.ViewGroup
import android.view.inputmethod.EditorInfo
import android.widget.LinearLayout
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.FileProvider
import androidx.core.content.edit
import androidx.core.view.isVisible
import com.google.android.material.button.MaterialButton
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import dev.nexscreen.client.databinding.ActivityMainBinding
import dev.nexscreen.client.net.AppRelease
import dev.nexscreen.client.net.AppUpdate
import dev.nexscreen.client.net.DiscoveredHost
import dev.nexscreen.client.net.HostDiscovery
import dev.nexscreen.client.net.Protocol
import java.io.File
import java.util.Locale

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private val prefs by lazy { getSharedPreferences("connection", Context.MODE_PRIVATE) }
    private var discoveryThread: Thread? = null
    private var updateThread: Thread? = null

    /** APK ya descargado que espera a que el usuario permita instalar desde esta app. */
    private var pendingApk: File? = null
    private val currentVersion by lazy { packageManager.getPackageInfo(packageName, 0).versionName ?: "0.0.0" }

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
        binding.checkUpdate.text = getString(R.string.update_check, currentVersion)
        binding.checkUpdate.setOnClickListener { checkForUpdate() }
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
        // Volvió de Configuración después de permitir instalar apps desde NexScreen.
        pendingApk?.let { if (packageManager.canRequestPackageInstalls()) installApk(it) }
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

    // ---- Actualizaciones ----

    private fun showUpdateStatus(text: String) {
        binding.updateStatus.text = text
        binding.updateStatus.isVisible = true
    }

    /** Consulta GitHub (solo cuando el usuario lo pide) y ofrece instalar si hay una versión nueva. */
    private fun checkForUpdate() {
        if (updateThread?.isAlive == true) return
        binding.checkUpdate.isEnabled = false
        showUpdateStatus(getString(R.string.update_checking))
        updateThread = Thread({
            val release = runCatching { AppUpdate.fetchLatest(currentVersion) }
            runOnUiThread {
                if (isFinishing || isDestroyed) return@runOnUiThread
                binding.checkUpdate.isEnabled = true
                val latest = release.getOrNull()
                when {
                    release.isFailure -> showUpdateStatus(getString(R.string.update_error))
                    latest == null || !AppUpdate.isNewer(latest.version, currentVersion) ->
                        showUpdateStatus(getString(R.string.update_up_to_date, currentVersion))
                    else -> offerUpdate(latest)
                }
            }
        }, "update-check").apply {
            isDaemon = true
            start()
        }
    }

    private fun offerUpdate(release: AppRelease) {
        binding.updateStatus.isVisible = false
        val megabytes = String.format(Locale.US, "%.1f", release.size / 1_048_576.0)
        MaterialAlertDialogBuilder(this)
            .setTitle(getString(R.string.update_available_title, release.version))
            .setMessage(getString(R.string.update_available_message, currentVersion, release.version, megabytes))
            .setPositiveButton(R.string.update_install) { _, _ -> downloadUpdate(release) }
            .setNegativeButton(R.string.update_later, null)
            .show()
    }

    private fun downloadUpdate(release: AppRelease) {
        binding.checkUpdate.isEnabled = false
        showUpdateStatus(getString(R.string.update_downloading, release.version, 0))
        updateThread = Thread({
            val result = runCatching {
                AppUpdate.download(release, currentVersion, File(cacheDir, "updates")) { percent ->
                    runOnUiThread { showUpdateStatus(getString(R.string.update_downloading, release.version, percent)) }
                }
            }
            runOnUiThread {
                if (isFinishing || isDestroyed) return@runOnUiThread
                binding.checkUpdate.isEnabled = true
                result.onSuccess { apk ->
                    binding.updateStatus.isVisible = false
                    installApk(apk)
                }.onFailure { error ->
                    showUpdateStatus(getString(R.string.update_download_failed, error.message ?: error.javaClass.simpleName))
                }
            }
        }, "update-download").apply {
            isDaemon = true
            start()
        }
    }

    /** Entrega el APK al instalador de Android; si falta el permiso, abre su pantalla en Configuración. */
    private fun installApk(apk: File) {
        if (!packageManager.canRequestPackageInstalls()) {
            pendingApk = apk
            showUpdateStatus(getString(R.string.update_allow_install))
            startActivity(
                Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")),
            )
            return
        }

        pendingApk = null
        val uri = FileProvider.getUriForFile(this, "$packageName.updates", apk)
        startActivity(
            Intent(Intent.ACTION_VIEW)
                .setDataAndType(uri, "application/vnd.android.package-archive")
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),
        )
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

package dev.nexscreen.client.net

import android.content.Context
import android.net.ConnectivityManager
import android.os.SystemClock
import android.util.Log
import org.json.JSONException
import org.json.JSONObject
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.SocketTimeoutException

data class DiscoveredHost(val name: String, val address: String, val port: Int, val version: String)

/**
 * Busca PCs con NexScreen en la red local: envía un broadcast UDP (a 255.255.255.255 y al
 * broadcast de cada subred) y junta las respuestas. Ver "Descubrimiento" en docs/protocol.md.
 */
class HostDiscovery(private val context: Context) {

    /** Corre la búsqueda en un hilo propio. Los callbacks llegan desde ese hilo. */
    fun start(onFound: (DiscoveredHost) -> Unit, onFinished: () -> Unit): Thread =
        Thread({
            try {
                discover(onFound)
            } catch (e: Exception) {
                // Una búsqueda fallida no debe cerrar la app: queda la conexión manual.
                Log.w(TAG, "Búsqueda interrumpida", e)
            } finally {
                onFinished()
            }
        }, "nexscreen-discovery").apply { start() }

    private fun discover(onFound: (DiscoveredHost) -> Unit) {
        val request = REQUEST.toByteArray(Charsets.US_ASCII)
        val targets = broadcastAddresses()
        val seen = mutableSetOf<String>()

        DatagramSocket().use { socket ->
            socket.broadcast = true
            socket.soTimeout = RECEIVE_TIMEOUT_MS
            val deadline = SystemClock.elapsedRealtime() + DISCOVERY_DURATION_MS
            var sends = 0
            var nextSend = 0L

            while (SystemClock.elapsedRealtime() < deadline && !Thread.currentThread().isInterrupted) {
                val now = SystemClock.elapsedRealtime()
                if (sends < SEND_COUNT && now >= nextSend) {
                    // Varios envíos: el WiFi puede perder algún broadcast.
                    for (target in targets) {
                        try {
                            socket.send(DatagramPacket(request, request.size, target, Protocol.DISCOVERY_PORT))
                        } catch (e: IOException) {
                            Log.d(TAG, "No se pudo enviar a $target: ${e.message}")
                        }
                    }
                    sends++
                    nextSend = now + SEND_INTERVAL_MS
                }

                val buffer = ByteArray(1024)
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    socket.receive(packet)
                } catch (e: SocketTimeoutException) {
                    continue
                }

                val host = parse(packet) ?: continue
                if (seen.add(host.address)) onFound(host)
            }
        }
    }

    private fun parse(packet: DatagramPacket): DiscoveredHost? {
        val address = packet.address.hostAddress ?: return null
        return try {
            val json = JSONObject(String(packet.data, 0, packet.length, Charsets.UTF_8))
            if (json.getInt("protocol") != Protocol.VERSION) return null
            DiscoveredHost(
                name = json.getString("name"),
                address = address,
                port = json.getInt("port"),
                version = json.optString("version"),
            )
        } catch (e: JSONException) {
            null
        }
    }

    /** 255.255.255.255 más el broadcast de cada red IPv4 (algunos routers solo pasan este último). */
    @Suppress("DEPRECATION")
    private fun broadcastAddresses(): List<InetAddress> {
        val result = mutableListOf<InetAddress>(InetAddress.getByName("255.255.255.255"))
        val connectivity = context.getSystemService(ConnectivityManager::class.java) ?: return result
        val networks = try {
            connectivity.allNetworks
        } catch (e: SecurityException) {
            Log.w(TAG, "Sin acceso al estado de la red; solo se usa 255.255.255.255", e)
            return result
        }
        for (network in networks) {
            val links = connectivity.getLinkProperties(network)?.linkAddresses ?: continue
            for (link in links) {
                val address = link.address as? Inet4Address ?: continue
                if (address.isLoopbackAddress || link.prefixLength !in 1..30) continue
                val ip = address.address.fold(0) { acc, b -> (acc shl 8) or (b.toInt() and 0xFF) }
                val broadcast = ip or (-1 ushr link.prefixLength)
                result += InetAddress.getByAddress(
                    byteArrayOf((broadcast ushr 24).toByte(), (broadcast ushr 16).toByte(), (broadcast ushr 8).toByte(), broadcast.toByte()),
                )
            }
        }
        return result.distinct()
    }

    private companion object {
        const val TAG = "HostDiscovery"
        const val REQUEST = "NEXSCREEN_DISCOVER 1"
        const val DISCOVERY_DURATION_MS = 2500L
        const val SEND_COUNT = 3
        const val SEND_INTERVAL_MS = 700L
        const val RECEIVE_TIMEOUT_MS = 250
    }
}

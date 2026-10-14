package dev.nexscreen.client.net

import android.content.Context
import androidx.core.content.edit
import java.security.MessageDigest
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * Emparejamiento por PIN. Ver docs/protocol.md y Pairing.cs en el host: la prueba es
 * HMAC-SHA256(PIN, "NexScreen pairing v2\n<huella del PC>\n<huella de la tablet>").
 */
object Pairing {
    const val PIN_LENGTH = 6
    private const val CONTEXT = "NexScreen pairing v2"

    fun proof(pin: String, hostFingerprint: String, clientFingerprint: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(pin.toByteArray(Charsets.UTF_8), "HmacSHA256"))
        return mac.doFinal("$CONTEXT\n$hostFingerprint\n$clientFingerprint".toByteArray(Charsets.UTF_8)).toHex()
    }

    /** SHA-256 del certificado (DER) en hexadecimal en minúsculas, igual que en el host. */
    fun fingerprint(der: ByteArray): String = MessageDigest.getInstance("SHA-256").digest(der).toHex()

    private fun ByteArray.toHex() = joinToString("") { "%02x".format(it) }
}

/** PCs en los que la tablet confía: huella de su certificado TLS → nombre del PC. */
class TrustedHosts(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences("trusted_hosts", Context.MODE_PRIVATE)

    fun contains(fingerprint: String) = prefs.contains(fingerprint)

    fun add(fingerprint: String, name: String) = prefs.edit { putString(fingerprint, name) }

    fun clear() = prefs.edit { clear() }

    val count: Int get() = prefs.all.size
}

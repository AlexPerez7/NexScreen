package dev.nexscreen.client.net

import android.annotation.SuppressLint
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.math.BigInteger
import java.net.Socket
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.Principal
import java.security.PrivateKey
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.util.Date
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngine
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509ExtendedKeyManager
import javax.net.ssl.X509TrustManager
import javax.security.auth.x500.X500Principal

/**
 * Identidad TLS de la tablet para las conexiones por WiFi: una clave EC P-256 en el Android
 * Keystore (no sale del dispositivo) con un certificado autofirmado. El PC fija su huella al
 * emparejar. Se crea la primera vez que se usa.
 */
object TlsIdentity {
    private const val ALIAS = "nexscreen-client"
    private const val KEYSTORE = "AndroidKeyStore"

    @Volatile
    private var cached: Pair<PrivateKey, X509Certificate>? = null

    val certificate: X509Certificate get() = load().second

    /** Socket TLS con el certificado de la tablet que acepta cualquier certificado del PC (ver [AcceptAnyServer]). */
    fun socketFactory(): SSLSocketFactory {
        val (key, cert) = load()
        val context = SSLContext.getInstance("TLS")
        context.init(arrayOf(ClientKeyManager(key, cert)), arrayOf(AcceptAnyServer), null)
        return context.socketFactory
    }

    @Synchronized
    private fun load(): Pair<PrivateKey, X509Certificate> {
        cached?.let { return it }
        val store = KeyStore.getInstance(KEYSTORE).apply { load(null) }
        if (!store.containsAlias(ALIAS)) generate()
        val entry = store.getEntry(ALIAS, null) as KeyStore.PrivateKeyEntry
        return (entry.privateKey to entry.certificate as X509Certificate).also { cached = it }
    }

    private fun generate() {
        val now = System.currentTimeMillis()
        val day = 24L * 60 * 60 * 1000
        val spec = KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            // NONE: TLS 1.2 firma el hash ya calculado.
            .setDigests(
                KeyProperties.DIGEST_NONE,
                KeyProperties.DIGEST_SHA256,
                KeyProperties.DIGEST_SHA384,
                KeyProperties.DIGEST_SHA512,
            )
            .setCertificateSubject(X500Principal("CN=NexScreen tablet"))
            .setCertificateSerialNumber(BigInteger.valueOf(now))
            .setCertificateNotBefore(Date(now - day))
            .setCertificateNotAfter(Date(now + 50 * 365 * day))
            .build()
        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, KEYSTORE).apply { initialize(spec) }.generateKeyPair()
    }

    private class ClientKeyManager(private val key: PrivateKey, private val cert: X509Certificate) : X509ExtendedKeyManager() {
        override fun chooseClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?) = ALIAS
        override fun chooseEngineClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, engine: SSLEngine?) = ALIAS
        override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?) = arrayOf(ALIAS)
        override fun getCertificateChain(alias: String?) = arrayOf(cert)
        override fun getPrivateKey(alias: String?) = key
        override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?): Array<String>? = null
        override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?): String? = null
    }

    /**
     * El certificado del PC es autofirmado: no hay CA que lo valide. La confianza sale del
     * emparejamiento: StreamClient compara la huella con las de [TrustedHosts] y, si no está, no
     * acepta video ni manda input hasta que el usuario ingresa el PIN.
     */
    @SuppressLint("CustomX509TrustManager", "TrustAllX509TrustManager")
    private object AcceptAnyServer : X509TrustManager {
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
            throw UnsupportedOperationException()

        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            if (chain.isNullOrEmpty()) throw java.security.cert.CertificateException("El PC no presentó certificado")
        }

        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    }
}

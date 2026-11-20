package dev.nexscreen.client.net

import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/** Una versión publicada en GitHub Releases con su APK. */
data class AppRelease(
    val version: String,
    val apkUrl: String,
    val size: Long,
    /** SHA-256 en hexadecimal que publica GitHub (`digest`), o null si no lo informa. */
    val sha256: String?,
)

/**
 * Búsqueda e instalación de actualizaciones de la app desde GitHub Releases. Solo se usa cuando el
 * usuario toca "Buscar actualizaciones": la app no hace ninguna conexión a internet por su cuenta
 * (ver PRIVACY.md). Android además exige que el APK nuevo esté firmado con la misma clave.
 */
object AppUpdate {
    private const val LATEST_URL = "https://api.github.com/repos/AlexPerez7/NexScreen/releases/latest"
    private const val TIMEOUT_MS = 15_000
    private val APK_ASSET = Regex("^NexScreen-\\d+\\.\\d+\\.\\d+\\.apk$", RegexOption.IGNORE_CASE)

    /** "v0.8.0" o "0.8.0" → [0, 8, 0]. Null si no es una versión de tres números. */
    fun parseVersion(text: String?): List<Int>? {
        val parts = text?.trim()?.removePrefix("v")?.removePrefix("V")?.split('.') ?: return null
        if (parts.size != 3) return null
        val numbers = parts.map { it.toIntOrNull()?.takeIf { n -> n >= 0 } ?: return null }
        return numbers
    }

    /** True si [latest] es una versión mayor que [current]. Si alguna no se entiende, false. */
    fun isNewer(latest: String, current: String): Boolean {
        val a = parseVersion(latest) ?: return false
        val b = parseVersion(current) ?: return false
        for (i in 0 until 3) {
            if (a[i] != b[i]) return a[i] > b[i]
        }
        return false
    }

    /**
     * Lee la respuesta de `/releases/latest`. Devuelve null si es un borrador o pre-release, el tag no
     * es una versión, o el release no trae el APK (`NexScreen-X.Y.Z.apk`).
     */
    fun parseRelease(json: String): AppRelease? {
        val root = JSONObject(json)
        if (root.optBoolean("draft") || root.optBoolean("prerelease")) return null
        val tag = root.optString("tag_name")
        val version = parseVersion(tag)?.joinToString(".") ?: return null
        val assets = root.optJSONArray("assets") ?: return null
        for (i in 0 until assets.length()) {
            val asset = assets.optJSONObject(i) ?: continue
            if (!APK_ASSET.matches(asset.optString("name"))) continue
            val url = asset.optString("browser_download_url")
            if (!url.startsWith("https://")) continue
            return AppRelease(version, url, asset.optLong("size"), parseDigest(asset.optString("digest")))
        }
        return null
    }

    /** GitHub informa el hash como "sha256:<hex>". Otros algoritmos se ignoran. */
    internal fun parseDigest(digest: String?): String? {
        val prefix = "sha256:"
        if (digest == null || !digest.startsWith(prefix, ignoreCase = true)) return null
        val hex = digest.substring(prefix.length)
        return if (hex.length == 64 && hex.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }) hex.lowercase() else null
    }

    /** Consulta el último release. Bloquea: llamar desde un hilo de fondo. */
    @Throws(IOException::class)
    fun fetchLatest(currentVersion: String): AppRelease? {
        val connection = open(LATEST_URL, currentVersion).apply { setRequestProperty("Accept", "application/vnd.github+json") }
        try {
            if (connection.responseCode != HttpURLConnection.HTTP_OK) {
                throw IOException("GitHub respondió ${connection.responseCode}")
            }
            return parseRelease(connection.inputStream.bufferedReader().use { it.readText() })
        } finally {
            connection.disconnect()
        }
    }

    /**
     * Descarga el APK en [dir] (vacía antes lo que haya) y verifica tamaño y SHA-256.
     * [onProgress] recibe el porcentaje (0-100). Bloquea: llamar desde un hilo de fondo.
     */
    @Throws(IOException::class)
    fun download(release: AppRelease, currentVersion: String, dir: File, onProgress: (Int) -> Unit): File {
        dir.mkdirs()
        dir.listFiles()?.forEach { it.delete() }
        val target = File(dir, "NexScreen-${release.version}.apk")
        val digest = MessageDigest.getInstance("SHA-256")
        var total = 0L

        val connection = open(release.apkUrl, currentVersion)
        try {
            if (connection.responseCode != HttpURLConnection.HTTP_OK) {
                throw IOException("La descarga falló (${connection.responseCode})")
            }
            connection.inputStream.use { input ->
                target.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var lastPercent = -1
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        output.write(buffer, 0, read)
                        digest.update(buffer, 0, read)
                        total += read
                        if (release.size > 0) {
                            val percent = (total * 100 / release.size).toInt().coerceIn(0, 100)
                            if (percent != lastPercent) {
                                lastPercent = percent
                                onProgress(percent)
                            }
                        }
                    }
                }
            }
        } catch (e: IOException) {
            target.delete()
            throw e
        } finally {
            connection.disconnect()
        }

        val actual = digest.digest().joinToString("") { "%02x".format(it) }
        if ((release.size > 0 && total != release.size) || (release.sha256 != null && actual != release.sha256)) {
            target.delete()
            throw IOException("El archivo descargado está dañado")
        }
        return target
    }

    private fun open(url: String, currentVersion: String): HttpURLConnection =
        (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = TIMEOUT_MS
            readTimeout = TIMEOUT_MS
            setRequestProperty("User-Agent", "NexScreen-Android/$currentVersion")
        }
}

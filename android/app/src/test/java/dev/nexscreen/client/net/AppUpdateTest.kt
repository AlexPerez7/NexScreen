package dev.nexscreen.client.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class AppUpdateTest {
    private val sha = "a".repeat(64)

    private fun release(
        tag: String = "v0.8.0",
        draft: Boolean = false,
        prerelease: Boolean = false,
        assetName: String = "NexScreen-0.8.0.apk",
        url: String = "https://github.com/x/y/releases/download/v0.8.0/NexScreen-0.8.0.apk",
        digest: String = "sha256:$sha",
    ) = """
        {"tag_name":"$tag","draft":$draft,"prerelease":$prerelease,"assets":[
          {"name":"NexScreen-Setup-0.8.0.exe","browser_download_url":"https://github.com/x/setup.exe","size":1,"digest":"sha256:$sha"},
          {"name":"$assetName","browser_download_url":"$url","size":1234,"digest":"$digest"}
        ]}
    """.trimIndent()

    @Test
    fun parsesVersions() {
        assertEquals(listOf(0, 8, 0), AppUpdate.parseVersion("v0.8.0"))
        assertEquals(listOf(1, 2, 3), AppUpdate.parseVersion("1.2.3"))
        assertNull(AppUpdate.parseVersion("1.2"))
        assertNull(AppUpdate.parseVersion("1.2.x"))
        assertNull(AppUpdate.parseVersion("build-deps"))
        assertNull(AppUpdate.parseVersion(null))
    }

    @Test
    fun comparesVersionsNumerically() {
        assertTrue(AppUpdate.isNewer("0.8.0", "0.7.0"))
        assertTrue(AppUpdate.isNewer("v0.10.0", "0.9.0")) // 10 > 9, no es orden de texto
        assertTrue(AppUpdate.isNewer("1.0.0", "0.99.99"))
        assertFalse(AppUpdate.isNewer("0.7.0", "0.7.0"))
        assertFalse(AppUpdate.isNewer("0.6.0", "0.7.0"))
        assertFalse(AppUpdate.isNewer("basura", "0.7.0"))
    }

    @Test
    fun parsesTheApkAssetAndIgnoresTheInstaller() {
        val parsed = AppUpdate.parseRelease(release())!!

        assertEquals("0.8.0", parsed.version)
        assertEquals("https://github.com/x/y/releases/download/v0.8.0/NexScreen-0.8.0.apk", parsed.apkUrl)
        assertEquals(1234L, parsed.size)
        assertEquals(sha, parsed.sha256)
    }

    @Test
    fun ignoresDraftsPrereleasesAndOtherTags() {
        assertNull(AppUpdate.parseRelease(release(draft = true)))
        assertNull(AppUpdate.parseRelease(release(prerelease = true)))
        assertNull(AppUpdate.parseRelease(release(tag = "build-deps")))
    }

    @Test
    fun rejectsReleasesWithoutAValidApk() {
        assertNull(AppUpdate.parseRelease(release(assetName = "otra-cosa.apk")))
        assertNull(AppUpdate.parseRelease(release(url = "http://inseguro/NexScreen-0.8.0.apk")))
    }

    @Test
    fun ignoresDigestsThatAreNotSha256() {
        assertNull(AppUpdate.parseRelease(release(digest = "md5:abc"))!!.sha256)
        assertNull(AppUpdate.parseDigest("sha256:corto"))
        assertEquals(sha, AppUpdate.parseDigest("SHA256:${sha.uppercase()}"))
    }
}

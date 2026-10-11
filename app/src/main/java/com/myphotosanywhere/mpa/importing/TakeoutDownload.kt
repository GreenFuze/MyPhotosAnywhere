package com.myphotosanywhere.mpa.importing

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.OkHttpClient
import okhttp3.Request
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import java.io.File
import java.io.RandomAccessFile
import java.security.KeyStore
import java.util.concurrent.TimeUnit
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** Download links can contain bearer credentials. Only Keystore-encrypted text is stored in Room. */
class TakeoutLinkVault {
    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        return (store.getKey(ALIAS, null) as? SecretKey) ?: KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply {
            init(KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build())
        }.generateKey()
    }
    fun seal(link: String): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }
        return Base64.encodeToString(cipher.iv + cipher.doFinal(link.toByteArray(Charsets.UTF_8)), Base64.NO_WRAP)
    }
    fun open(value: String): String {
        val bytes = Base64.decode(value, Base64.NO_WRAP)
        require(bytes.size > 28) { "Download link unavailable. Paste a fresh link." }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes.copyOfRange(0, 12))) }
        return cipher.doFinal(bytes.copyOfRange(12, bytes.size)).toString(Charsets.UTF_8)
    }
    companion object { private const val ALIAS = "mpa-takeout-links-v1" }
}

/** Streaming HTTP resume requires the same strong ETag, exact range, and total length. */
class TakeoutZipDownloader(
    private val client: OkHttpClient = OkHttpClient.Builder().followRedirects(false).followSslRedirects(false)
        .connectTimeout(30, TimeUnit.SECONDS).readTimeout(30, TimeUnit.SECONDS).build(),
    private val validateUrl: (String) -> Unit = ::validateLink,
    private val availableBytes: (File) -> Long = { requireNotNull(it.parentFile).usableSpace },
) {
    suspend fun download(link: String, file: File, saved: TakeoutDownload,
        checkpoint: suspend (TakeoutDownload) -> Unit, keepGoing: suspend () -> Unit): TakeoutDownload {
        validateUrl(link)
        requireNotNull(file.parentFile).mkdirs()
        var progress = saved
        // Bytes beyond the fsynced checkpoint are never trusted after process death.
        var offset = if (file.isFile && file.length() >= saved.bytes && strong(saved.etag)) saved.bytes else 0L
        RandomAccessFile(file, "rw").use { it.setLength(offset) }
        if(offset > 0 && offset == saved.total) {
            requireZip(file)
            return saved.copy(state="READY",error=null).also { checkpoint(it) }
        }
        var url = link
        var redirects = 0
        while (true) {
            currentCoroutineContext().ensureActive(); keepGoing()
            validateUrl(url)
            val request = Request.Builder().url(url).header("Accept-Encoding", "identity")
            if (offset > 0) request.header("Range", "bytes=$offset-").header("If-Range", requireNotNull(saved.etag))
            client.newCall(request.build()).execute().use { response ->
                if (response.code in listOf(301, 302, 303, 307, 308)) {
                    require(++redirects <= 5) { "Too many download redirects. Use a direct ZIP link." }
                    url = response.header("Location")?.let { response.request.url.resolve(it)?.toString() }
                        ?: error("Download redirect has no valid destination.")
                } else {
                    require(response.code in listOf(200, 206)) {
                        if (response.code in listOf(401, 403, 404, 410)) "Link expired or needs browser sign-in. Paste a fresh direct ZIP link, or download in your browser and select the local ZIP."
                        else "ZIP download failed (HTTP ${response.code}). Resume to retry."
                    }
                    require(response.header("Content-Encoding")?.lowercase().let { it == null || it == "identity" }) { "Server returned encoded bytes; use a direct ZIP link." }
                    val body = response.body ?: error("Empty ZIP response.")
                    val type = response.header("Content-Type").orEmpty().substringBefore(';').lowercase()
                    require(type !in listOf("text/html", "application/xhtml+xml", "application/json", "text/plain")) {
                        "This link opens a page, not a ZIP. Sign in using your browser and select the downloaded ZIP, or paste a direct ZIP download link."
                    }
                    val etag = response.header("ETag")?.takeIf { strong(it) }
                    val total: Long
                    if (response.code == 206) {
                        require(offset > 0 && etag == saved.etag) { "Archive revision changed. Paste its link again to restart the download." }
                        val range = Regex("bytes (\\d+)-(\\d+)/(\\d+)").matchEntire(response.header("Content-Range").orEmpty())
                            ?: error("Invalid download range. Source preserved.")
                        val (start, end, length) = range.destructured
                        total = length.toLong()
                        require(start.toLong() == offset && end.toLong() == total - 1 && (saved.total < 0 || total == saved.total) &&
                            (body.contentLength() < 0 || body.contentLength() == total - offset)) { "Download range or size changed. Source preserved." }
                    } else {
                        offset = 0 // Server ignored Range/If-Range: truncate, never append another revision.
                        total = body.contentLength()
                    }
                    require(total == -1L || total in 4..MAX_ARCHIVE) { "ZIP exceeds 50 GiB or is empty. Choose smaller Takeout ZIP parts." }
                    require(availableBytes(file) > maxOf(0L, total - offset) + RESERVE) { "Not enough phone storage for the ZIP plus one media file. Free space and resume." }
                    progress = progress.copy(bytes = offset, total = total, etag = etag, state = "DOWNLOADING", error = null)
                    RandomAccessFile(file, "rw").use { output ->
                        output.setLength(offset); output.seek(offset); output.fd.sync(); checkpoint(progress)
                        body.byteStream().use { input ->
                            val buffer = ByteArray(65536)
                            var persisted = offset
                            while (true) {
                                currentCoroutineContext().ensureActive(); keepGoing()
                                val n = input.read(buffer); if (n < 0) break
                                require(progress.bytes + n <= MAX_ARCHIVE && (total < 0 || progress.bytes + n <= total)) { "ZIP exceeds its declared size or the 50 GiB limit." }
                                require(availableBytes(file) > n + RESERVE) { "Phone storage is full. Free space and resume." }
                                output.write(buffer, 0, n)
                                progress = progress.copy(bytes = progress.bytes + n)
                                if (progress.bytes - persisted >= 1024 * 1024) {
                                    output.fd.sync(); checkpoint(progress); persisted = progress.bytes
                                }
                            }
                        }
                        output.fd.sync(); checkpoint(progress)
                    }
                    require(progress.bytes >= 4 && (total < 0 || progress.bytes == total)) { "ZIP download was interrupted. Resume to continue." }
                    requireZip(file)
                    return progress.copy(total = progress.bytes, state = "READY", error = null).also { checkpoint(it) }
                }
            }
        }
    }
    companion object {
        const val MAX_ARCHIVE = 50L * 1024 * 1024 * 1024
        const val RESERVE = TakeoutDocuments.MAX_MEDIA + 64L * 1024 * 1024
        private fun strong(etag: String?) = etag != null && etag.startsWith('"') && etag.endsWith('"') && etag.length > 2
        private fun requireZip(file: File) = RandomAccessFile(file,"r").use {
            require(it.readInt() in listOf(0x504b0304,0x504b0506)) { "Downloaded file is not a ZIP. Paste a direct ZIP link." }
        }
        fun validateLink(value: String) {
            val url = value.toHttpUrlOrNull() ?: error("Enter a direct HTTPS ZIP download URL.")
            require(url.isHttps && url.username.isEmpty() && url.password.isEmpty() && url.fragment == null) { "Use an HTTPS download URL without a password or fragment." }
        }
    }
}

/** Only app-owned archive paths are deletable. Source documents never enter this directory. */
class TakeoutArchiveStore(context: Context) {
    private val root = File(context.filesDir, "takeout-downloads")
    fun file(job: String): File {
        require(job.matches(Regex("[a-zA-Z0-9-]+")))
        return File(root, "$job.zip")
    }
    fun delete(job: String) {
        val file = file(job)
        check(!file.exists() || file.delete()) { "Could not remove the downloaded ZIP. Retry cleanup." }
    }
    companion object {
        fun canClean(items: List<ImportItem>): Boolean = items.isNotEmpty() && items.all {
            it.state == "COMPLETE" || (it.mime == "application/json" && it.metadata != null)
        }
    }
}

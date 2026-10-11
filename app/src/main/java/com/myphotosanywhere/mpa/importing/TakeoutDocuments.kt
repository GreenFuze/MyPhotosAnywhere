package com.myphotosanywhere.mpa.importing

import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract
import android.system.Os
import android.system.OsConstants
import java.io.File
import java.io.InputStream
import java.util.zip.ZipFile
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

/** Seekable archives stay in place. A non-seekable provider must be copied by the user to local/USB storage. */
class TakeoutDocuments(private val context: Context) {
    private val resolver get() = context.contentResolver
    private val codec = TakeoutCodec()

    suspend fun manifest(job: String, uri: Uri, tree: Boolean): List<ImportItem> {
        val result = mutableListOf<ImportItem>()
        var expanded = 0L
        fun add(source: String, path: String, bytes: Long, crc: Long, metadata: String? = null) {
            TakeoutCodec.safePath(path)
            expanded = Math.addExact(expanded, maxOf(0, bytes))
            require(expanded <= 1024L * 1024 * 1024 * 1024) { "Archive expanded size exceeds 1 TiB." }
            require(result.size < 10000) { "Use batches of fewer than 10,000 entries; MPA's XMP catalogue is bounded." }
            val mime = if (path.endsWith(".json", true)) "application/json" else TakeoutCodec.mime(path)
            val supported = mime != null && (mime == "application/json" || bytes in 1..MAX_MEDIA)
            val id = stableId("$job:$source:$path:$crc")
            result.add(ImportItem(id, job, source, path, path.substringAfterLast('/'), mime ?: "application/octet-stream", bytes, crc,
                metadata = metadata, album = path.substringBeforeLast('/', "").substringAfterLast('/').take(100),
                selected = supported && mime != "application/json", state = if (supported) "PENDING" else "UNSUPPORTED",
                error = if (supported) null else "Unsupported format or exceeds 250 MiB; source is preserved."))
        }
        if (tree) {
            val queue = ArrayDeque<Pair<String,String>>()
            queue.add(DocumentsContract.getTreeDocumentId(uri) to "")
            val visited = mutableSetOf<String>()
            while (queue.isNotEmpty()) {
                currentCoroutineContext().ensureActive()
                val (parent, prefix) = queue.removeFirst()
                require(visited.add(parent) && visited.size <= 10000) { "Repeated or excessive document folders." }
                val children = DocumentsContract.buildChildDocumentsUriUsingTree(uri, parent)
                resolver.query(children, arrayOf("document_id", "_display_name", "mime_type", "_size"), null, null, null)?.use { cursor ->
                    while (cursor.moveToNext()) {
                        val id = cursor.getString(0); val name = cursor.getString(1)
                        val path = prefix + name
                        TakeoutCodec.safePath(path)
                        if (cursor.getString(2) == DocumentsContract.Document.MIME_TYPE_DIR) queue.add(id to "$path/")
                        else {
                            val document = DocumentsContract.buildDocumentUriUsingTree(uri, id)
                            val raw = if (name.endsWith(".json", true)) resolver.openInputStream(document)?.use { readJson(it) } else null
                            add(document.toString(), path, cursor.getLong(3), -1, raw)
                        }
                    }
                } ?: error("Cannot enumerate the selected folder.")
            }
        } else archive(uri) { zip ->
            val paths = mutableSetOf<String>()
            val entries = zip.entries()
            var count = 0
            while (entries.hasMoreElements()) {
                val entry = entries.nextElement()
                require(++count <= 10000) { "Archive exceeds 10,000 entries. Use smaller batches." }
                TakeoutCodec.safePath(entry.name.removeSuffix("/"))
                require(paths.add(entry.name)) { "Archive contains duplicate paths." }
                if (entry.isDirectory) continue
                require(entry.size >= 0 && (entry.compressedSize <= 0 || entry.size / maxOf(1, entry.compressedSize) <= 200)) {
                    "Archive has an unsafe compression ratio. Source is preserved."
                }
                val raw = if (entry.name.endsWith(".json", true)) zip.getInputStream(entry).use { readJson(it) } else null
                add(uri.toString(), entry.name, entry.size, entry.crc, raw)
            }
        }
        // Match within the complete export-relative directory. Multiple parts may supply metadata later.
        return match(result)
    }

    fun match(items: List<ImportItem>): List<ImportItem> {
        val updates = items.associateBy { it.id }.toMutableMap()
        val media = items.filter { it.mime.startsWith("image/") || it.mime.startsWith("video/") }
        for (sidecar in items.filter { it.mime == "application/json" && it.metadata != null }) {
            val parsed = try { codec.parse(requireNotNull(sidecar.metadata)) } catch (_: Exception) {
                updates[sidecar.id] = sidecar.copy(state = "UNSUPPORTED", error = "Album or unsupported JSON; retained in source.")
                continue
            }
            val paths = codec.candidates(sidecar.path, parsed, media.map { it.path })
            val matches = media.filter { it.path in paths }
            if (matches.size == 1) {
                val target = requireNotNull(updates[matches.single().id])
                if (target.metadata == null || target.metadata == sidecar.metadata) {
                    updates[target.id] = target.copy(metadata = sidecar.metadata)
                    updates[sidecar.id] = sidecar.copy(state = "MATCHED", error = null)
                } else updates[sidecar.id] = sidecar.copy(state = "UNRESOLVED", error = "Conflicting metadata; no automatic overwrite.")
            } else updates[sidecar.id] = sidecar.copy(state = "UNRESOLVED", error = "Missing or ambiguous matching media; add remaining parts or review source.")
        }
        return items.map { requireNotNull(updates[it.id]) }
    }

    suspend fun stage(item: ImportItem, target: File, keepGoing: suspend () -> Unit) {
        val uri = Uri.parse(item.uri)
        if (item.crc >= 0) archive(uri) { zip ->
            val entry = zip.getEntry(item.path) ?: error("Archive entry missing. Reselect the unchanged export.")
            require(entry.crc == item.crc && entry.size == item.size) { "Archive changed since preview." }
            zip.getInputStream(entry).use { copyBounded(it, target, keepGoing) }
        } else resolver.openInputStream(uri)?.use { copyBounded(it, target, keepGoing) }
            ?: error("Document access was lost. Reselect the source.")
        require(item.size <= 0 || target.length() == item.size) { "Document changed since preview." }
        if(item.crc>=0) {
            val crc=java.util.zip.CRC32()
            target.inputStream().use { input -> val buffer=ByteArray(65536)
                while(true) { val n=input.read(buffer); if(n<0) break; crc.update(buffer,0,n) }
            }
            require(crc.value==item.crc) { "Archive entry checksum failed. Source preserved." }
        }
    }

    private suspend fun <T> archive(uri: Uri, block: suspend (ZipFile) -> T): T {
        if (uri.scheme == "file") {
            val file = File(requireNotNull(uri.path)).canonicalFile
            val root = File(context.filesDir, "takeout-downloads").canonicalFile
            if(file.parentFile == root && file.name.matches(Regex("[a-zA-Z0-9-]+\\.zip")))
                return ZipFile(file).use { block(it) }
        }
        val descriptor = resolver.openFileDescriptor(uri, "r") ?: error("Cannot open Takeout ZIP.")
        return descriptor.use {
            try { Os.lseek(it.fileDescriptor, 0, OsConstants.SEEK_CUR) } catch (_: Exception) {
                error("This provider cannot seek the ZIP. Save it to local or USB storage, then select it again.")
            }
            ZipFile("/proc/self/fd/${it.fd}").use { zip -> block(zip) }
        }
    }

    private fun readJson(input: InputStream): String {
        val output = java.io.ByteArrayOutputStream()
        val buffer = ByteArray(8192)
        while (true) {
            val n = input.read(buffer); if (n < 0) break
            require(output.size() + n <= TakeoutCodec.MAX_JSON_BYTES) { "JSON exceeds 128 KiB; source preserved." }
            output.write(buffer, 0, n)
        }
        val bytes = output.toByteArray()
        require(bytes.size <= TakeoutCodec.MAX_JSON_BYTES) { "JSON exceeds 128 KiB; source preserved." }
        return bytes.toString(Charsets.UTF_8)
    }

    companion object {
        const val MAX_MEDIA = 250L * 1024 * 1024
        fun stableId(value: String) = java.util.UUID.nameUUIDFromBytes(value.toByteArray(Charsets.UTF_8)).toString()
        suspend fun copyBounded(input: InputStream, target: File, keepGoing: suspend () -> Unit = {}) {
            target.parentFile?.mkdirs()
            var bytes = 0L
            target.outputStream().use { output ->
                val buffer = ByteArray(64 * 1024)
                while (true) {
                    currentCoroutineContext().ensureActive(); keepGoing()
                    val n = input.read(buffer); if (n < 0) break
                    bytes += n
                    require(bytes <= MAX_MEDIA) { "Media exceeds the 250 MiB limit. Source preserved." }
                    require(target.parentFile!!.usableSpace > 16L * 1024 * 1024) { "Not enough free space. Free storage and resume." }
                    output.write(buffer, 0, n)
                }
                require(bytes > 0) { "Empty media file." }; output.fd.sync()
            }
        }
    }
}

package com.myphotosanywhere.mpa.importing

import android.net.Uri
import com.myphotosanywhere.mpa.drive.*
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.runBlocking
import org.apache.commons.compress.archivers.zip.ZipFile

/** Source identity is an account/folder binding plus immutable checked archive revision, never a token. */
class DriveTakeoutArchive(private val api: DriveGateway, private val binding: String, private val root: String) {
    suspend fun capture(id: String): Uri {
        require(id.matches(Regex("[A-Za-z0-9_-]{1,200}"))) { "Invalid Drive ZIP identity." }
        val file=api.file(id)
        validate(file)
        val tag=api.readRange(id,0,1,file.bytes,null).second
        check(api.file(id)==file) { "Drive ZIP changed while opening. Select the unchanged archive again." }
        return Uri.Builder().scheme(SCHEME).authority(binding).appendPath(id)
            .appendQueryParameter("version",requireNotNull(file.version))
            .appendQueryParameter("bytes",file.bytes.toString()).appendQueryParameter("etag",tag).build()
    }
    suspend fun open(uri: Uri, budget: Long): ZipFile {
        require(uri.scheme==SCHEME && uri.authority==binding && uri.pathSegments.size==1) { "Drive ZIP source mismatch." }
        val file=api.file(uri.lastPathSegment ?: error("Drive ZIP missing."))
        validate(file)
        require(file.version==uri.getQueryParameter("version") && file.bytes==uri.getQueryParameter("bytes")?.toLongOrNull()) {
            "Drive ZIP changed since preview. Source and transfer checkpoints are preserved; select a new import."
        }
        val tag=requireNotNull(uri.getQueryParameter("etag"))
        require(tag.isNotBlank() && tag.length<=256)
        val coroutine=currentCoroutineContext()
        val channel=DriveRangeChannel(file.bytes,budget) { offset,count ->
            coroutine.ensureActive()
            // Commons Compress has a synchronous SeekableByteChannel contract. Calls stay on IO,
            // bounded by the parent coroutine, range cache and HTTP timeout.
            runBlocking(coroutine) { api.readRange(file.id,offset,count,file.bytes,tag).first }
        }
        return try { ZipFile.builder().setSeekableByteChannel(channel).get() }
        catch(e: Exception) {
            channel.close()
            // ZIP libraries may wrap transport failures; retain their bounded connection recovery.
            var cause: Throwable?=e
            repeat(8) {
                val current=cause
                if(current is DriveConnectionException) throw current
                cause=current?.cause
            }
            throw e
        }
    }
    private suspend fun validate(file: DriveFile) {
        require(!file.trashed && file.name.endsWith(".zip",true) && file.version!=null && file.bytes>0) {
            "Select an ordinary Takeout ZIP stored in the connected Drive folder."
        }
        val pending=ArrayDeque(file.parents); val seen=mutableSetOf<String>(); var inside=false
        while(pending.isNotEmpty()) {
            currentCoroutineContext().ensureActive()
            val id=pending.removeFirst()
            if(id==root) { inside=true; break }
            if(!seen.add(id)) continue
            require(seen.size<=64) { "Drive ZIP folder ancestry is too deep." }
            val folder=api.file(id)
            if(!folder.trashed && folder.mimeType==DriveGateway.FOLDER_MIME) pending.addAll(folder.parents)
        }
        require(inside) { "Drive ZIP moved outside the connected source folder. Source is preserved." }
    }
    companion object { const val SCHEME="mpa-drive-zip" }
}

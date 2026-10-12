package com.myphotosanywhere.mpa.selection

import android.content.ClipData
import android.content.Intent
import androidx.core.content.FileProvider
import androidx.room.withTransaction
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.data.AlbumPhoto
import com.myphotosanywhere.mpa.data.MediaCache
import com.myphotosanywhere.mpa.data.StorageBudget
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.withLock
import java.io.File
import java.io.IOException
import java.util.UUID

/** Bulk metadata stays local and serialized with cache ownership; originals are never deleted. */
internal class SelectionRepository(private val app: MpaApplication) {
    private val db=app.database
    internal class Unavailable(message: String): IOException(message)
    private suspend fun resolve(target: SelectionTarget): Photo {
        val id=target.photoId ?: target.cloudKey?.let { key ->
            val cloud=db.cloud().photo(key) ?: throw Unavailable("This source item is no longer indexed.")
            if(!target.accepts(cloud)) throw Unavailable("The source item changed. Select it again after refreshing.")
            cloud.photoId
        } ?: throw Unavailable("Download the original first to organize this photo.")
        val photo=db.photos().find(id) ?: throw Unavailable("This photo is no longer in the library.")
        if(photo.bytes!=target.bytes || !target.sha256.isNullOrBlank() && photo.sha256!=target.sha256)
            throw Unavailable("The selected content changed. Select it again.")
        return photo
    }

    suspend fun apply(target: SelectionTarget,action: BulkAction)=withContext(Dispatchers.IO) {
        if(action.kind==BulkKind.OFFLINE) {
            val photo=try { resolve(target) } catch(_: Unavailable) { null }
            if(photo!=null && photo.originalPath.isNotBlank() && File(photo.originalPath).isFile) {
                app.cache.lock.withLock { db.photos().find(photo.id)?.let { db.photos().update(it.copy(pinned=true)) }
                    ?: throw Unavailable("This photo is no longer in the library.") }
            } else {
                val key=target.cloudKey ?: photo?.let { it.sourceBindingKey?.let { binding -> "$binding:${it.sourceRemoteId}" } }
                    ?: throw Unavailable("No connected original is available to download.")
                val cloud=db.cloud().photo(key)?.takeIf { target.accepts(it) }
                    ?: throw Unavailable("This source item changed or is unavailable.")
                val source=db.drive().source(cloud.bindingKey)
                if(source==null || !source.enabled) throw Unavailable("Reconnect this photo's Drive source.")
                app.downloads.enqueue(cloud,pin=true)
            }
        } else app.cache.lock.withLock { db.withTransaction {
            val photo=resolve(target)
            when(action.kind) {
                BulkKind.FAVORITE -> db.photos().update(photo.copy(favorite=true))
                BulkKind.UNFAVORITE -> db.photos().update(photo.copy(favorite=false))
                BulkKind.ARCHIVE -> db.photos().update(photo.copy(archived=true))
                BulkKind.RESTORE -> db.photos().update(photo.copy(archived=false))
                BulkKind.ALBUM,BulkKind.REMOVE_ALBUM -> {
                    val album=requireNotNull(action.albumId)
                    val exists=db.openHelper.readableDatabase.query("SELECT id FROM albums WHERE id=?",arrayOf(album)).use { it.moveToFirst() }
                    if(!exists) throw Unavailable("The album no longer exists. Choose another album.")
                    if(action.kind==BulkKind.ALBUM) db.library().addToAlbum(AlbumPhoto(album,photo.id))
                    else db.library().removeFromAlbum(album,photo.id)
                }
                else -> error("Original-dependent action entered metadata transaction.")
            }
        } }
    }

    /** Prepare exact verified local bytes under leases. A failed batch never silently shares a subset. */
    suspend fun share(targets: List<SelectionTarget>,progress: (Int)->Unit): Intent {
        var staging: File?=null
        try { return withContext(Dispatchers.IO) {
        require(targets.isNotEmpty())
        val leases=ArrayList<MediaCache.OriginalRead>()
        val directory=File(app.cacheDir,"handoff/bulk-${UUID.randomUUID()}").also { staging=it }
        var complete=false
        try {
            for(target in targets) {
                currentCoroutineContext().ensureActive()
                val photo=resolve(target)
                val lease=app.cache.openOriginal(photo.id) ?: throw Unavailable("${target.name}: download the original before sharing.")
                leases.add(lease)
            }
            if(!directory.mkdirs()) throw IOException("Cannot prepare share files.")
            var total=64L*1024*1024
            for(lease in leases) {
                total=Math.addExact(total,lease.file.length())
                // Match the recorded original before exporting bytes, retaining its cache lease.
                if(lease.file.length()!=lease.photo.bytes) throw Unavailable("${lease.photo.displayName}: original size changed. Download it again.")
            }
            try { StorageBudget(app).requireBytes(directory,total,"Not enough storage to prepare these originals. Select fewer photos.") }
            catch(error: IllegalArgumentException) { throw Unavailable(error.message ?: "Not enough storage to share originals.") }
            val uris=ArrayList<android.net.Uri>()
            for((index,lease) in leases.withIndex()) {
                currentCoroutineContext().ensureActive()
                val suffix=lease.photo.displayName.substringAfterLast('.',"").takeIf { it.matches(Regex("[A-Za-z0-9]{1,10}")) } ?: "bin"
                val file=File(directory,"${index+1}.$suffix")
                lease.file.inputStream().use { input -> file.outputStream().use { output ->
                    val buffer=ByteArray(64*1024)
                    while(true) { currentCoroutineContext().ensureActive();val read=input.read(buffer);if(read<0) break;output.write(buffer,0,read) }
                } }
                if(com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(file,"SHA-256")!=lease.photo.sha256)
                    throw Unavailable("${lease.photo.displayName}: original verification failed. Nothing was shared.")
                uris.add(FileProvider.getUriForFile(app,"${app.packageName}.handoff",file));progress(index+1)
            }
            val mime=leases.map { it.photo.mimeType }.distinct().let { types ->
                if(types.size==1) types.single() else if(types.all { it.startsWith("image/") }) "image/*"
                else if(types.all { it.startsWith("video/") }) "video/*" else "*/*"
            }
            val send=Intent(Intent.ACTION_SEND_MULTIPLE).apply {
                type=mime;putParcelableArrayListExtra(Intent.EXTRA_STREAM,uris)
                clipData=ClipData.newUri(app.contentResolver,"MPA originals",uris.first()).apply { uris.drop(1).forEach { addItem(ClipData.Item(it)) } }
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            complete=true
            Intent.createChooser(send,"Share originals").apply { addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION) }
        } finally { leases.forEach { it.close() };if(!complete) directory.deleteRecursively() }
        } } catch(cancelled: CancellationException) {
            // Dispatch back can discard a successfully prepared result on cancellation.
            withContext(NonCancellable+Dispatchers.IO) { staging?.deleteRecursively() }
            throw cancelled
        }
    }
}

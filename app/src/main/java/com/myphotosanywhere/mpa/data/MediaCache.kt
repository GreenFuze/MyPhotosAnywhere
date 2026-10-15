package com.myphotosanywhere.mpa.data

import android.content.Context
import com.myphotosanywhere.mpa.core.*
import com.myphotosanywhere.mpa.drive.binding
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.File

/** All readers of disposable originals and all eviction share this lock. */
class MediaCache(private val context: Context, private val database: PhotoDatabase, private val settings: LibrarySettings,
    private val remoteVerifier: (suspend (Photo, File) -> Boolean)? = null) {
    val lock=Mutex()
    private val readers=mutableMapOf<String,Int>()
    class OriginalRead(val photo: Photo,val file: File,private val lease: AutoCloseable): AutoCloseable {
        override fun close()=lease.close()
    }
    /** Validate the latest row and protect its file under the same lock as eviction. */
    suspend fun openOriginal(id: String): OriginalRead? {
        var opened: OriginalRead?=null
        try { return withContext(Dispatchers.IO) { lock.withLock {
            val photo=database.photos().find(id) ?: return@withLock null
            val file=File(photo.originalPath)
            if(photo.originalPath.isBlank() || !file.isFile) return@withLock null
            database.photos().update(photo.copy(lastAccess=System.currentTimeMillis()))
            OriginalRead(photo,file,reader(id)).also { opened=it }
        } } } catch(e: kotlinx.coroutines.CancellationException) {
            // withContext may discard a result when the UI closes during dispatch back.
            opened?.close();throw e
        }
    }
    suspend fun acquireReader(id: String): AutoCloseable = lock.withLock { reader(id) }
    private fun reader(id: String): AutoCloseable {
        synchronized(readers) { readers[id]=(readers[id] ?: 0)+1 }
        val released=java.util.concurrent.atomic.AtomicBoolean(false)
        return AutoCloseable { if(released.compareAndSet(false,true)) synchronized(readers) {
            val remaining=(readers[id] ?: 1)-1
            if(remaining<=0) readers.remove(id) else readers[id]=remaining
        } }
    }
    suspend fun <T> withOriginal(id: String, action: suspend (Photo,File)->T): T = withContext(Dispatchers.IO) {
        lock.withLock {
            val photo=database.photos().find(id) ?: error("This photo is no longer in the library.")
            val file=File(photo.originalPath)
            check(photo.originalPath.isNotBlank() && file.isFile) { "Download this photo before opening or editing it." }
            database.photos().update(photo.copy(lastAccess=System.currentTimeMillis()))
            action(photo,file)
        }
    }
    suspend fun enforce(clear: Boolean = false): String = withContext(Dispatchers.IO) {
        lock.withLock {
            val candidates=database.photos().all().filter { it.sourceBindingKey!=null && it.originalPath.isNotBlank() }
            val items=candidates.map { photo ->
                val cloud=database.cloud().photo("${photo.sourceBindingKey}:${photo.sourceRemoteId}")
                val drafts=database.library().draft(photo.id)
                val handoffs=database.photos().findHandoffsForPhoto(photo.id)
                val options=settings.state.value; val scan=database.library().scan(photo.id)
                val awaitingIndex=photo.mimeType.startsWith("image/") && !options.indexingPaused && scan?.error==null &&
                    (options.faceIndexing && (scan==null || scan.sha256!=photo.sha256 || scan.modelVersion!=com.myphotosanywhere.mpa.people.PeopleRepository.SCAN_VERSION) ||
                        options.objectIndexing && photo.labels.isBlank())
                CachedFile(photo.id,File(photo.originalPath).length(),photo.lastAccess,
                    cloud!=null && !cloud.removed && cloud.version==photo.cachedVersion && cloud.photoId==photo.id,
                    photo.pinned || database.library().pinnedAlbumCount(photo.id)>0,drafts!=null || handoffs.isNotEmpty() || awaitingIndex,
                    synchronized(readers) { readers[photo.id] ?: 0 })
            }
            val plan=CachePolicy.plan(items,if(clear) 0 else settings.state.value.originalBudget)
            var preserved=0
            for(id in plan.ids) {
                val photo=database.photos().find(id) ?: continue
                val file=File(photo.originalPath)
                val active=photo.sourceBindingKey?.let { database.drive().source(it)?.connection() }
                val verified=try {
                    if(remoteVerifier!=null) remoteVerifier.invoke(photo,file)
                    else if(active?.binding()?.key!=photo.sourceBindingKey) false
                    else {
                        val api=(context.applicationContext as com.myphotosanywhere.mpa.MpaApplication).sync.api(requireNotNull(active))
                        val remote=api.file(requireNotNull(photo.sourceRemoteId))
                        !remote.trashed && remote.version==photo.cachedVersion && remote.bytes==photo.bytes &&
                            remote.md5==com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(file,"MD5") &&
                            (context.applicationContext as com.myphotosanywhere.mpa.MpaApplication).sync.inside(api,remote,requireNotNull(active.folderId))
                    }
                } catch(cancelled: kotlinx.coroutines.CancellationException) { throw cancelled }
                catch(_: Exception) { false }
                if(!verified) { preserved++; continue }
                // Metadata first: a crash leaves an orphan, never a row advertising a missing download.
                database.photos().update(photo.copy(originalPath=""))
                if(file.exists() && !file.delete()) database.photos().update(photo)
            }
            cleanThumbnails()
            if(plan.budgetSatisfied && preserved==0) "Disposable downloads cleared within budget. Originals and pinned items are protected."
            else "Pinned or unverified downloads exceed the budget. Protected files were kept."
        }
    }
    private suspend fun cleanThumbnails() {
        val all=database.photos().all()
        val cloud=database.cloud().observePhotos().first()
        val paths=(all.map { it.thumbnailPath } + cloud.map { it.thumbnailPath }).filter { it.isNotBlank() }.distinct()
        var total=paths.sumOf { File(it).length() }
        for(path in paths.sortedBy { File(it).lastModified() }) {
            if(total<=settings.state.value.thumbnailBudget) break
            val file=File(path); val size=file.length()
            if(file.delete()) total-=size
        }
    }
    suspend fun thumbnail(photo: Photo): File? = withContext(Dispatchers.IO) {
        val target=File(photo.thumbnailPath)
        if(target.isFile) { target.setLastModified(System.currentTimeMillis()); return@withContext target }
        // Missing bytes are an operational availability state. Recheck the current row
        // under the same lock as eviction, retaining the existing serial/bounded decoder.
        lock.withLock {
            val latest=database.photos().find(photo.id) ?: return@withLock null
            if(latest.sha256!=photo.sha256 || latest.bytes!=photo.bytes || latest.thumbnailPath!=photo.thumbnailPath) return@withLock null
            val file=File(latest.originalPath)
            if(latest.originalPath.isBlank() || !file.isFile || latest.thumbnailPath.isBlank()) return@withLock cloudThumbnail(latest)
            database.photos().update(latest.copy(lastAccess=System.currentTimeMillis()))
            target.parentFile?.mkdirs()
            val bitmap=PhotoRepository.mediaPreview(file,latest.mimeType,480)
            try { target.outputStream().use { check(bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG,85,it)) } }
            finally { bitmap.recycle() }
            target
        }
    }
    private suspend fun cloudThumbnail(photo: Photo): File? {
        val binding=photo.sourceBindingKey ?: return null
        val remote=photo.sourceRemoteId ?: return null
        val row=database.cloud().photo("$binding:$remote") ?: return null
        if(row.photoId!=photo.id || row.bytes!=photo.bytes ||
            (row.sha256?.let { it!=photo.sha256 } ?: (row.version!=photo.cachedVersion))) return null
        return row.thumbnailPath.takeIf(String::isNotBlank)?.let(::File)?.takeIf { it.isFile }
    }
}

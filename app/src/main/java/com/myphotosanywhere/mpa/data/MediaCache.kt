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
    suspend fun acquireReader(id: String): AutoCloseable = lock.withLock {
        synchronized(readers) { readers[id]=(readers[id] ?: 0)+1 }
        AutoCloseable { synchronized(readers) {
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
                CachedFile(photo.id,File(photo.originalPath).length(),photo.lastAccess,
                    cloud!=null && !cloud.removed && cloud.version==photo.cachedVersion && cloud.photoId==photo.id,
                    photo.pinned || database.library().pinnedAlbumCount(photo.id)>0,drafts!=null || handoffs.isNotEmpty(),
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
        if(photo.originalPath.isBlank()) {
            val row=database.cloud().photo("${photo.sourceBindingKey}:${photo.sourceRemoteId}")
            return@withContext row?.thumbnailPath?.let { File(it) }?.takeIf { it.isFile }
        }
        withOriginal(photo.id) { _,file ->
            target.parentFile?.mkdirs()
            val bitmap=PhotoRepository.mediaPreview(file,photo.mimeType,480)
            try { target.outputStream().use { check(bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG,85,it)) } }
            finally { bitmap.recycle() }
            target
        }
    }
}

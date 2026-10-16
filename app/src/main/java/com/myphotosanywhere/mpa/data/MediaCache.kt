package com.myphotosanywhere.mpa.data

import android.content.Context
import com.myphotosanywhere.mpa.core.*
import com.myphotosanywhere.mpa.drive.binding
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import androidx.room.withTransaction
import kotlinx.coroutines.NonCancellable
import java.io.File
import java.io.IOException

/** All readers of disposable originals and all eviction share this lock. */
class MediaCache(private val context: Context, private val database: PhotoDatabase, private val settings: LibrarySettings,
    private val remoteVerifier: (suspend (Photo, File) -> Boolean)? = null) {
    val lock=Mutex()
    private val readers=mutableMapOf<String,Int>()
    class OriginalRead(val photo: Photo,val file: File,private val lease: AutoCloseable): AutoCloseable {
        override fun close()=lease.close()
    }
    /** Validate the latest row and protect its file under the same lock as eviction. */
    suspend fun openOriginal(id: String): OriginalRead?=readOriginal(id,trackAccess=true)
    /** Grid derivation protects bytes without invalidating the Photos query on every tile. */
    internal suspend fun openThumbnailOriginal(id: String): OriginalRead?=readOriginal(id,trackAccess=false)
    private suspend fun readOriginal(id: String,trackAccess: Boolean): OriginalRead? {
        var opened: OriginalRead?=null
        try { return withContext(Dispatchers.IO) { lock.withLock {
            val photo=database.photos().find(id) ?: return@withLock null
            val file=File(photo.originalPath)
            if(photo.originalPath.isBlank() || !file.isFile) return@withLock null
            if(trackAccess) database.photos().update(photo.copy(lastAccess=System.currentTimeMillis()))
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

    /** User-directed removal is separate from Drive Trash and requires fresh exact-copy proof. */
    internal suspend fun removeDownloadedCopy(identity: DeviceCopyIdentity): String=withContext(Dispatchers.IO) {
        val id=identity.id
        val unused=kotlinx.coroutines.withTimeoutOrNull(1500) {
            while(synchronized(readers) { readers[id] ?: 0 }>0) kotlinx.coroutines.delay(16)
            true
        }
        if(unused!=true) throw IOException("This original is still in use. Close its viewer or editor and retry.")
        var protection: AutoCloseable?=null
        try {
            val snapshot=lock.withLock {
                val photo=database.photos().find(id) ?: throw IOException("This photo is no longer available.")
                if(!identity.matches(photo)) throw IOException("This photo or its source changed. Open its details again.")
                requireDisposable(photo,0)
                val source=database.drive().source(requireNotNull(photo.sourceBindingKey))
                    ?: throw IOException("The connected source changed. The device copy was kept.")
                val file=File(photo.originalPath)
                protection=reader(id)
                RemovalSnapshot(photo,source,file,file.lastModified())
            }
            val photo=snapshot.photo;val file=snapshot.file
            if(file.length()!=photo.bytes || com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(file,"SHA-256")!=photo.sha256)
                throw IOException("The device file no longer matches its verified identity. It was kept.")
            val verified=if(remoteVerifier!=null) remoteVerifier.invoke(photo,file) else {
                val app=context.applicationContext as com.myphotosanywhere.mpa.MpaApplication
                val api=app.sync.api(snapshot.source.connection())
                val remote=api.file(requireNotNull(photo.sourceRemoteId))
                remote.id==photo.sourceRemoteId && !remote.trashed && remote.version==photo.cachedVersion && remote.bytes==photo.bytes &&
                    remote.md5!=null && remote.md5==com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(file,"MD5") &&
                    app.sync.inside(api,remote,snapshot.source.folderId)
            }
            if(!verified) throw IOException("The Drive original could not be verified. The device copy was kept. Reconnect or refresh its source and retry.")
            lock.withLock {
                val latest=database.photos().find(id) ?: throw IOException("This photo is no longer available.")
                if(!snapshot.sameOriginal(latest) || !file.isFile || file.length()!=latest.bytes || file.lastModified()!=snapshot.modified)
                    throw IOException("This photo changed while Drive was checked. The device copy was kept.")
                requireDisposable(latest,1)
                val source=database.drive().source(requireNotNull(latest.sourceBindingKey))
                if(source==null || source.accountId!=snapshot.source.accountId || source.folderId!=snapshot.source.folderId || source.mode!=snapshot.source.mode)
                    throw IOException("The connected source changed. The device copy was kept.")
                val preview=removalPreview(latest,file)
                // Commit availability first. Process death can leave extra owned bytes, never
                // delete the only original or roll unrelated metadata back to an old snapshot.
                withContext(NonCancellable) {
                    database.withTransaction {
                        val current=database.photos().find(id) ?: throw IOException("This photo is no longer available.")
                        if(!snapshot.sameOriginal(current)) throw IOException("This photo changed. The device copy was kept.")
                        requireDisposable(current,1)
                        check(database.photos().clearOriginal(id,current.originalPath,preview.path)==1)
                    }
                    if(!file.delete()) {
                        check(database.photos().restoreOriginalPath(id,photo.originalPath,photo.sha256,photo.bytes)==1) { "Cannot restore the retained original path." }
                        throw IOException("Android could not remove this download. The original file was kept.")
                    }
                }
            }
            "Device download removed. The Drive original and photo information are preserved."
        } finally { protection?.close() }
    }

    private data class RemovalSnapshot(val photo: Photo,val source: com.myphotosanywhere.mpa.drive.DriveSource,val file: File,val modified: Long) {
        fun sameOriginal(other: Photo)=other.id==photo.id && other.sha256==photo.sha256 && other.bytes==photo.bytes &&
            other.originalPath==photo.originalPath && other.cachedVersion==photo.cachedVersion &&
            other.sourceBindingKey==photo.sourceBindingKey && other.sourceRemoteId==photo.sourceRemoteId
    }
    private suspend fun requireDisposable(photo: Photo,ownedReaders: Int) {
        val source=photo.sourceBindingKey?.let { database.drive().source(it) }
        val remote=photo.sourceRemoteId
        if(source==null || remote.isNullOrBlank() || photo.cachedVersion==null)
            throw IOException("This original has no connected, verified Drive copy. It cannot be removed from this device.")
        val row=database.cloud().photo("${source.key}:$remote")
        if(row==null || row.removed || row.photoId!=photo.id || row.bytes!=photo.bytes || row.version!=photo.cachedVersion ||
            row.sha256?.let { it!=photo.sha256 }==true)
            throw IOException("Refresh this source before removing its device download. The original was kept.")
        val file=File(photo.originalPath);val originals=File(context.filesDir,"originals").canonicalFile
        if(photo.originalPath.isBlank() || !file.isFile || !file.canonicalPath.startsWith(originals.path+File.separator) ||
            database.photos().sharesOriginal(photo.id,listOf(file.absolutePath,file.canonicalPath)))
            throw IOException("This file is missing, shared, or outside MPA’s disposable downloads. It was kept.")
        if(photo.pinned || database.library().pinnedAlbumCount(photo.id)>0)
            throw IOException("This photo is kept offline. Remove its photo or album offline pin first.")
        val options=settings.state.value;val scan=database.library().scan(photo.id)
        val awaitingIndex=photo.mimeType.startsWith("image/") && !options.indexingPaused && scan?.error==null &&
            (options.faceIndexing && (scan==null || scan.sha256!=photo.sha256 || scan.modelVersion!=com.myphotosanywhere.mpa.people.PeopleRepository.SCAN_VERSION) ||
                options.objectIndexing && photo.labels.isBlank())
        if(database.library().draft(photo.id)!=null || database.photos().findHandoffsForPhoto(photo.id).isNotEmpty() || awaitingIndex)
            throw IOException("An edit, AI handoff, or enabled recognition scan still needs this original. It was kept.")
        if(database.photos().unfinishedImport(photo.id) || database.trash().unfinishedTransfer(source.accountId,remote) ||
            database.drive().backupsForPhoto(photo.id).any { it.state!="COMPLETE" } ||
            database.cloud().download(row.key)?.let { it.state!="COMPLETE" }==true ||
            database.trash().records(source.accountId,remote).any { it.confirmed || it.desired!=null })
            throw IOException("A transfer or Drive Trash operation still needs this original. It was kept.")
        if(synchronized(readers) { readers[photo.id] ?: 0 }>ownedReaders)
            throw IOException("This original is still in use. Close its viewer or editor and retry.")
    }
    private fun removalPreview(photo: Photo,original: File): File {
        val directory=File(context.filesDir,"thumbnails")
        val cached=File(photo.thumbnailPath)
        if(photo.thumbnailPath.isNotBlank() && cached.isFile && cached.length()<=2L*1024*1024 &&
            cached.canonicalPath.startsWith(directory.canonicalPath+File.separator)) {
            val header=android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds=true }
            android.graphics.BitmapFactory.decodeFile(cached.path,header)
            if(header.outWidth>0 && header.outHeight>0) return cached
        }
        if(!directory.isDirectory && !directory.mkdirs()) throw IOException("Cannot keep a preview. The original was kept.")
        val budget=StorageBudget(context)
        if(budget.available(directory)<32L*1024*1024) throw IOException("More free space is needed to keep a preview. The original was kept.")
        val key=java.security.MessageDigest.getInstance("SHA-256").digest("${photo.id}:${photo.sha256}".toByteArray()).joinToString("") { "%02x".format(it) }
        val target=File(directory,"removed-$key.jpg")
        val temporary=File.createTempFile("removed-preview-",".part",directory)
        try {
            val bitmap=try { PhotoRepository.mediaPreview(original,photo.mimeType,480) }
            catch(error: IllegalStateException) { throw IOException("Cannot keep a usable preview. The original was kept.",error) }
            catch(error: IllegalArgumentException) { throw IOException("This media cannot provide a usable preview. The original was kept.",error) }
            try { temporary.outputStream().use { if(!bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG,90,it)) throw IOException("Cannot save the preview. The original was kept.") } }
            finally { bitmap.recycle() }
            if(!temporary.renameTo(target)) throw IOException("Cannot retain the preview. The original was kept.")
            return target
        } finally { if(temporary.exists()) check(temporary.delete()) { "Cannot remove an owned preview staging file." } }
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
        // An imported/restored row may reuse an original path as its preview. Such bytes
        // remain originals and must never enter the disposable-thumbnail eviction set.
        val originalPaths=all.mapNotNull { it.originalPath.takeIf(String::isNotBlank)?.let { path -> File(path).canonicalPath } }.toSet()
        val paths=(all.map { it.thumbnailPath } + cloud.map { it.thumbnailPath }).filter { it.isNotBlank() }
            .distinct().filter { File(it).canonicalPath !in originalPaths }
        var total=paths.sumOf { File(it).length() }
        val thumbnailBudget=settings.state.value.thumbnailBudget
        val baseBudget=thumbnailBudget-com.myphotosanywhere.mpa.thumbnails.ThumbnailPolicy().diskBudget(thumbnailBudget)
        for(path in paths.sortedBy { File(it).lastModified() }) {
            if(total<=baseBudget) break
            val file=File(path); val size=file.length()
            if(file.delete()) total-=size
        }
    }
    suspend fun thumbnail(photo: Photo): File? = withContext(Dispatchers.IO) {
        val target=File(photo.thumbnailPath)
        if(target.isFile) {
            if(photo.originalPath.isBlank() || target.canonicalPath!=File(photo.originalPath).canonicalPath) target.setLastModified(System.currentTimeMillis())
            return@withContext target
        }
        // Missing bytes are an operational availability state. Recheck the current row
        // under the same lock as eviction, retaining the existing serial/bounded decoder.
        lock.withLock {
            val latest=database.photos().find(photo.id) ?: return@withLock null
            if(latest.sha256!=photo.sha256 || latest.bytes!=photo.bytes || latest.thumbnailPath!=photo.thumbnailPath) return@withLock null
            val file=File(latest.originalPath)
            if(latest.originalPath.isBlank() || !file.isFile || latest.thumbnailPath.isBlank()) return@withLock cloudThumbnail(latest)
            // Preview-only reads must not invalidate every retained paging row.
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

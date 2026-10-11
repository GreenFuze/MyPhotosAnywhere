package com.myphotosanywhere.mpa.importing

import android.content.Context
import android.net.Uri
import androidx.room.withTransaction
import androidx.work.*
import com.google.gson.JsonObject
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.metadata.XmpCodec
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.File
import java.util.UUID

class ImportRepository(private val context: Context) {
    private val app get()=context.applicationContext as MpaApplication
    private val db get()=app.database
    private val dao get()=db.imports()
    private val lock=Mutex() // One staged media file across jobs, including retries.
    private val documents=TakeoutDocuments(context)
    private val codec=TakeoutCodec()
    private val archives=TakeoutArchiveStore(context)
    private val links=TakeoutLinkVault()

    suspend fun addTakeoutUrl(link: String, autoStart: Boolean=true): String = withContext(Dispatchers.IO) { lock.withLock {
        val url=link.trim(); TakeoutZipDownloader.validateLink(url)
        val job=ImportJob(UUID.randomUUID().toString(),destination().key,"TAKEOUT",state="DOWNLOADING")
        val sealed=links.seal(url)
        db.withTransaction {
            dao.saveJob(job); dao.saveDownload(TakeoutDownload(job.id,sealed,autoStart=autoStart))
        }
        scheduleDownload(job.id); job.id
    } }

    suspend fun replaceDownloadLink(id: String, link: String) = withContext(Dispatchers.IO) { lock.withLock {
        val job=dao.job(id) ?: error("Import missing.")
        val download=dao.download(id) ?: error("Download missing.")
        require(job.state !in listOf("RUNNING","DOWNLOADING","CANCELLED") && download.state!="CLEANED" && dao.items(id).isEmpty()) {
            "Pause the download before replacing its link. An imported archive cannot be replaced."
        }
        val url=link.trim(); TakeoutZipDownloader.validateLink(url)
        // A fresh URL may serve different bytes: restart this archive before manifest creation.
        val sealed=links.seal(url)
        archives.delete(id)
        db.withTransaction {
            dao.saveDownload(TakeoutDownload(id,sealed,autoStart=download.autoStart))
            dao.saveJob(job.copy(state="DOWNLOADING",error=null))
        }
        scheduleDownload(id)
    } }

    private suspend fun scheduleDownload(id: String) {
        val job=dao.job(id) ?: return
        val source=db.drive().source(job.binding) ?: return
        WorkManager.getInstance(context).enqueueUniqueWork("mpa-takeout-download:$id",ExistingWorkPolicy.KEEP,
            OneTimeWorkRequestBuilder<TakeoutDownloadWorker>().setInputData(workDataOf("job" to id))
                .setConstraints(Constraints.Builder().setRequiredNetworkType(if(source.unmeteredOnly) NetworkType.UNMETERED else NetworkType.CONNECTED).build())
                .addTag("mpa-import:${job.binding}").build())
    }

    suspend fun runDownload(id: String, notify: suspend (TakeoutDownload) -> Unit) = withContext(Dispatchers.IO) { lock.withLock {
        val job=dao.job(id) ?: return@withLock
        if(job.state!="DOWNLOADING") return@withLock
        try {
            suspend fun keepGoing() {
                currentCoroutineContext().ensureActive()
                require(dao.job(id)?.state=="DOWNLOADING" && db.drive().source(job.binding)!=null) { "Download paused or original Drive source disconnected." }
            }
            keepGoing()
            var download=dao.download(id) ?: error("Download record missing.")
            val file=archives.file(id)
            if(download.state!="READY") download=TakeoutZipDownloader().download(links.open(download.sealedLink),file,download,
                { dao.saveDownload(it); notify(it) },::keepGoing)
            keepGoing()
            val manifest=documents.manifest(id,Uri.fromFile(file),false)
            require(manifest.isNotEmpty()) { "ZIP contains no importable entries." }
            db.withTransaction {
                keepGoing()
                manifest.forEach { dao.insertItem(it) }
                documents.match(dao.items(id)).forEach { dao.updateItem(it) }
                dao.saveJob(job.copy(state=if(download.autoStart) "RUNNING" else "PREVIEW",error=null))
            }
            if(download.autoStart) schedule(id)
        } catch(e: CancellationException) { throw e }
        catch(e: Exception) {
            // Network/crypto exceptions can contain signed URLs. Never persist arbitrary exception messages.
            val message=if(e is IllegalArgumentException && !e.message.orEmpty().contains(Regex("https?://"))) e.message
                else "ZIP download or validation failed. Resume to retry, paste a fresh direct ZIP link, or select a local ZIP."
            dao.job(id)?.takeIf { it.state=="DOWNLOADING" }?.let { dao.saveJob(it.copy(state="FAILED",error=message)) }
        }
    } }

    /** Called only after durable verified media commits. Metadata JSON is retained in Room. */
    private suspend fun cleanCompletedDownload(id: String) {
        val download=dao.download(id) ?: return
        if(download.state=="CLEANED" || !TakeoutArchiveStore.canClean(dao.items(id))) return
        dao.saveDownload(download.copy(state="CLEANING"))
        try {
            for(item in dao.items(id).filter { it.state=="COMPLETE" }) {
                val stage=File(context.filesDir,"migration-stage/${item.id}")
                check(!stage.exists() || stage.delete()) { "Could not remove transfer staging." }
            }
            archives.delete(id)
            dao.saveDownload(download.copy(state="CLEANED",sealedLink="",error=null))
        } catch(e: CancellationException) { throw e
        } catch(e: Exception) {
            dao.saveDownload(download.copy(state="CLEANING",error="Phone ZIP cleanup failed. Retry cleanup."))
        }
    }

    suspend fun retryCleanup(id: String) = withContext(Dispatchers.IO) { lock.withLock {
        require(dao.job(id)?.state=="COMPLETE") { "Finish this import before cleanup." }
        cleanCompletedDownload(id)
    } }

    suspend fun discardDownloadedZip(id: String) = withContext(Dispatchers.IO) { lock.withLock {
        val job=dao.job(id) ?: return@withLock
        val download=dao.download(id) ?: return@withLock
        require(job.state !in listOf("RUNNING","DOWNLOADING")) { "Pause this import before deleting its ZIP." }
        for(item in dao.items(id)) {
            // These are MPA's own disposable transfer copies, never local/USB source documents.
            val stage=File(context.filesDir,"migration-stage/${item.id}")
            check(!stage.exists() || stage.delete()) { "Could not remove unfinished transfer copy. Retry phone ZIP deletion." }
            if(item.staged!=null) dao.updateItem(item.copy(staged=null))
        }
        archives.delete(id)
        db.withTransaction {
            dao.saveDownload(download.copy(state="CLEANED",sealedLink="",error=null))
            dao.saveJob(job.copy(state=if(job.state=="COMPLETE") "COMPLETE" else "CANCELLED",
                error="Downloaded ZIP removed from this phone. Verified media remains in Drive. To import remaining media, add the source ZIP again."))
        }
    } }

    suspend fun addTakeout(uris: List<Uri>, tree: Boolean=false, existing: String?=null): String = withContext(Dispatchers.IO) { lock.withLock {
        val job=existing?.let { dao.job(it) } ?: ImportJob(UUID.randomUUID().toString(),destination().key,"TAKEOUT")
        require(job.kind=="TAKEOUT" && job.state !in listOf("RUNNING","DOWNLOADING","CANCELLED")) { "Pause the existing import before adding parts." }
        val manifest=uris.flatMap { documents.manifest(job.id,it,tree) }
        require(manifest.size + dao.items(job.id).size <= 10000) { "Batch exceeds 10,000 entries." }
        db.withTransaction {
            dao.saveJob(job.copy(state="PREVIEW",error=null))
            manifest.forEach { dao.insertItem(it) }
            documents.match(dao.items(job.id)).forEach { dao.updateItem(it) }
            // Late metadata can attach to a previously completed media item without uploading again.
            for(item in dao.items(job.id).filter { it.state=="COMPLETE" && it.metadata!=null && it.photoId!=null })
                db.photos().find(requireNotNull(item.photoId))?.let { db.photos().update(codec.apply(it,codec.parse(requireNotNull(item.metadata)))) }
        }
        job.id
    } }

    /** Browse only inside an explicitly connected Drive source, separate from the import destination. */
    suspend fun driveArchives(binding: String, parent: String?=null, page: String?=null): DrivePage = withContext(Dispatchers.IO) {
        val source=db.drive().source(binding) ?: error("Reconnect the Drive ZIP source.")
        val api=app.sync.api(source.connection())
        val folder=parent ?: source.folderId
        // Browser parent IDs originate from the scoped list, but recheck ancestry before listing.
        if(folder!=source.folderId) {
            var current=api.file(folder); val seen=mutableSetOf<String>()
            require(!current.trashed && current.mimeType==DriveGateway.FOLDER_MIME) { "Select a Drive folder." }
            while(source.folderId !in current.parents) {
                require(!current.trashed && current.mimeType==DriveGateway.FOLDER_MIME && seen.add(current.id) && seen.size<=64) { "Folder is outside the connected Drive source." }
                current=api.file(current.parents.singleOrNull() ?: error("Folder ancestry unavailable."))
            }
        }
        api.archiveChildren(folder,page).also { require(!it.incompleteSearch) { "Drive ZIP listing is incomplete. Retry." } }
    }

    suspend fun addDriveTakeout(binding: String, remoteId: String, existing: String?=null): String = withContext(Dispatchers.IO) { lock.withLock {
        val source=db.drive().source(binding) ?: error("Reconnect the Drive ZIP source.")
        val job=existing?.let { dao.job(it) } ?: ImportJob(UUID.randomUUID().toString(),destination().key,"TAKEOUT")
        require(job.kind=="TAKEOUT" && job.state !in listOf("RUNNING","DOWNLOADING","CANCELLED")) { "Pause before adding ZIP parts." }
        val archive=DriveTakeoutArchive(app.sync.api(source.connection()),binding,source.folderId)
        val uri=archive.capture(remoteId)
        val reader=TakeoutDocuments(context) { archive.open(it,64L*1024*1024) }
        val manifest=reader.manifest(job.id,uri,false)
        require(manifest.isNotEmpty() && manifest.size+dao.items(job.id).size<=10000) { "Empty ZIP or batch exceeds 10,000 entries." }
        db.withTransaction {
            dao.saveJob(job.copy(state="PREVIEW",error=null))
            manifest.forEach { dao.insertItem(it) }
            documents.match(dao.items(job.id)).forEach { dao.updateItem(it) }
            for(item in dao.items(job.id).filter { it.state=="COMPLETE" && it.metadata!=null && it.photoId!=null })
                db.photos().find(requireNotNull(item.photoId))?.let { db.photos().update(codec.apply(it,codec.parse(requireNotNull(item.metadata)))) }
        }
        job.id
    } }

    private suspend fun stageTakeout(item: ImportItem, stage: File, client: okhttp3.OkHttpClient?, keepGoing: suspend ()->Unit): MediaFingerprint {
        val uri=Uri.parse(item.uri)
        if(uri.scheme!=DriveTakeoutArchive.SCHEME) return documents.stage(item,stage,keepGoing)
        val binding=requireNotNull(uri.authority)
        val source=db.drive().source(binding) ?: error("Reconnect this import's original Drive ZIP source.")
        val archive=DriveTakeoutArchive(app.sync.api(source.connection(),client),binding,source.folderId)
        return TakeoutDocuments(context) { archive.open(it,512L*1024*1024) }.stage(item,stage,keepGoing)
    }

    suspend fun createPicker(email: String): ImportJob = withContext(Dispatchers.IO) {
        val destination=destination()
        val session=PhotosPickerGateway(PhotosAuthorization(context).token(email)).create()
        val job=ImportJob(UUID.randomUUID().toString(),destination.key,"PICKER",email,session.id,session.uri,"PICKING")
        dao.saveJob(job); job
    }

    suspend fun checkPicker(id: String) = withContext(Dispatchers.IO) {
        val job=dao.job(id) ?: error("Selection job missing.")
        require(job.state=="PICKING") { "This selection is already saved or cancelled." }
        val api=PhotosPickerGateway(PhotosAuthorization(context).token(job.email))
        var session=api.session(requireNotNull(job.session))
        val deadline=System.nanoTime()/1000000 + minOf(60000,session.timeout)
        while(!session.ready && session.timeout>0 && System.nanoTime()/1000000 + session.interval <= deadline) {
            delay(session.interval)
            require(dao.job(id)?.state=="PICKING") { "Selection cancelled." }
            session=api.session(requireNotNull(job.session))
        }
        require(session.ready) { "Selection is unfinished or polling timed out. Finish in Google Photos, then check again." }
        val items=mutableListOf<ImportItem>(); var page: String?=null; val seen=mutableSetOf<String>()
        do {
            val result=api.items(session.id,page)
            items.addAll(result.first.map { media ->
                val provenance=JsonObject().apply { addProperty("provider","Google Photos Picker"); addProperty("mediaId",media.id)
                    addProperty("photosAccount",job.email); media.captured?.let { addProperty("capturedAt",it) }
                    addProperty("locationExcluded",true); addProperty("videoMayBeTranscoded",media.mime.startsWith("video/")) }.toString()
                ImportItem(TakeoutDocuments.stableId("${job.id}:${media.id}"),job.id,"",media.id,media.name,media.mime,0,
                    metadata=provenance,selected=media.mime.startsWith("image/") || media.mime.startsWith("video/"),
                    state=if(media.mime.startsWith("image/") || media.mime.startsWith("video/")) "PENDING" else "UNSUPPORTED")
            })
            require(items.size<=2000) { "Select at most 2,000 Photos items." }
            page=result.second
            require(page==null || seen.add(requireNotNull(page))) { "Google Photos repeated a page cursor." }
        } while(page!=null)
        db.withTransaction {
            require(dao.job(id)?.state=="PICKING")
            items.forEach { dao.insertItem(it) }; dao.saveJob(job.copy(state="PREVIEW",error=null))
        }
    }

    private suspend fun destination() = db.drive().importDestination()
        ?: error("Choose a default Drive backup destination in Storage before importing.")

    suspend fun select(id: String, selected: Boolean) {
        val item=dao.item(id) ?: return; val job=dao.job(item.job) ?: return
        require(job.state in listOf("PREVIEW","PAUSED","FAILED")) { "Pause the import before changing selection." }
        if(item.state !in listOf("COMPLETE","UNSUPPORTED","MATCHED","UNRESOLVED","INFORMATION")) dao.updateItem(item.copy(selected=selected))
    }
    suspend fun start(id: String) {
        val job=dao.job(id) ?: return
        require(job.state !in listOf("PICKING","DOWNLOADING","CANCELLED","COMPLETE"))
        require(db.drive().source(job.binding)!=null) { "Reconnect this import's original Drive source." }
        val download=dao.download(id)
        if(download!=null && (download.state !in listOf("READY","CLEANED") || (download.state=="READY" && dao.items(id).isEmpty()))) {
            dao.saveJob(job.copy(state="DOWNLOADING",error=null)); scheduleDownload(id); return
        }
        if(download?.state=="CLEANED") require(dao.items(id).none {
            it.selected && it.state!="COMPLETE" && it.uri==Uri.fromFile(archives.file(id)).toString()
        }) { "Downloaded ZIP was removed. Start a new import for its unfinished files." }
        db.withTransaction {
            dao.items(id).filter { it.state=="FAILED" }.forEach { dao.updateItem(it.copy(state="PENDING",error=null)) }
            dao.saveJob(job.copy(state="RUNNING",error=null))
        }
        schedule(id,ExistingWorkPolicy.REPLACE,visible=true)
    }
    suspend fun dismissErrors(id: String) {
        if(ImportErrorDismissal(db).dismiss(id)) ImportNotifications(context).clearSummary(id)
    }
    suspend fun pause(id: String, cancel: Boolean=false) {
        db.withTransaction {
            dao.job(id)?.takeIf { it.state !in listOf("COMPLETE","CANCELLED") }?.let {
                dao.saveJob(it.copy(state=if(cancel) "CANCELLED" else "PAUSED",
                    error=if(cancel) "Verified imports remain in Drive. Unfinished source files and any staged bytes are preserved." else null))
            }
        }
        dao.job(id)?.let { ImportNotifications(context).finish(id,ImportProgress.from(it,dao.items(id))) }
        if(android.os.Build.VERSION.SDK_INT>=34) ImportTransferScheduler(context).cancel(id)
        WorkManager.getInstance(context).cancelUniqueWork("mpa-import:$id")
        WorkManager.getInstance(context).cancelUniqueWork("mpa-takeout-download:$id")
    }
    suspend fun resumePending() {
        // UIDT creation requires a visible Activity. Existing system jobs resume themselves.
        if(android.os.Build.VERSION.SDK_INT<34) dao.jobs().filter { it.state=="RUNNING" }.forEach { schedule(it.id) }
        dao.jobs().filter { it.state=="DOWNLOADING" }.forEach { scheduleDownload(it.id) }
        dao.jobs().filter { it.state=="COMPLETE" }.forEach { retryCleanup(it.id) }
    }
    suspend fun resumeWhenVisible() {
        ImportForegroundRecovery(dao).resume({ db.drive().source(it)!=null }) { id, legacy ->
            schedule(id,if(legacy) ExistingWorkPolicy.REPLACE else ExistingWorkPolicy.APPEND_OR_REPLACE,visible=true)
        }
        if(android.os.Build.VERSION.SDK_INT>=34) dao.jobs().filter { it.state=="RUNNING" }.forEach {
            schedule(it.id,visible=true)
        }
    }
    private suspend fun schedule(id: String, policy: ExistingWorkPolicy=ExistingWorkPolicy.KEEP, visible: Boolean=false) {
        val job=dao.job(id) ?: return; val source=db.drive().source(job.binding) ?: return
        if(android.os.Build.VERSION.SDK_INT>=34) {
            val transfers=ImportTransferScheduler(context)
            if(transfers.pending(id)) return
            if(!visible) { ImportForegroundRecovery(dao).record(id); return }
            // Await cancellation registration before replacing a legacy WorkManager import.
            withContext(Dispatchers.IO) { WorkManager.getInstance(context).cancelUniqueWork("mpa-import:$id").result.get() }
            if(!transfers.schedule(id,source.unmeteredOnly,dao.items(id),job.kind)) ImportForegroundRecovery(dao).record(id)
            return
        }
        WorkManager.getInstance(context).enqueueUniqueWork("mpa-import:$id",
            policy,
            OneTimeWorkRequestBuilder<ImportWorker>().setInputData(workDataOf("job" to id))
                .setBackoffCriteria(BackoffPolicy.EXPONENTIAL,30,java.util.concurrent.TimeUnit.SECONDS)
                .setConstraints(Constraints.Builder().setRequiredNetworkType(if(source.unmeteredOnly) NetworkType.UNMETERED else NetworkType.CONNECTED).build())
                .addTag("mpa-import:${job.binding}").build())
    }

    suspend fun runOne(id: String, client: okhttp3.OkHttpClient?=null): Boolean = withContext(Dispatchers.IO) { lock.withLock {
        val job=dao.job(id) ?: return@withLock false
        if(job.state!="RUNNING") return@withLock false
        suspend fun keepGoing() {
            currentCoroutineContext().ensureActive()
            if(dao.job(id)?.state!="RUNNING") throw CancellationException("Import stopped.")
            require(db.drive().source(job.binding)!=null) { "Import Drive source disconnected." }
        }
        var item=dao.items(id).firstOrNull { it.selected && it.state in listOf("PENDING","STAGING","UPLOADING","VERIFIED") }
        if(item==null) {
            val failed=dao.items(id).any { it.selected && it.state=="FAILED" }
            val changed=dao.connectionState(job.id,if(failed) "FAILED" else "COMPLETE",job.error)==1
            if(changed && !failed) cleanCompletedDownload(id)
            return@withLock false
        }
        val initial=requireNotNull(item)
        try {
            val source=db.drive().source(job.binding) ?: error("Reconnect the original Drive source.")
            val api=app.sync.api(source.connection(),client)
            val folder=api.file(source.folderId)
            require(!folder.trashed && folder.canAddChildren) { "Original import destination is no longer writable." }
            // Fail before another upload when the portable catalogue cannot represent this batch.
            XmpCodec.encode(app.metadata.snapshot(source))
            var current=initial
            val stage=File(context.filesDir,"migration-stage/${current.id}")
            if(current.staged==null || !File(requireNotNull(current.staged)).isFile) {
                require(current.remoteId==null || current.sha256!=null) { "Upload checkpoint has no byte identity." }
                val stageDir=requireNotNull(stage.parentFile).apply { mkdirs() }
                require(stageDir.usableSpace > maxOf(current.size,16L*1024*1024)+16L*1024*1024) {
                    "Not enough free space to stage this item. Free storage and resume."
                }
                current=current.copy(state="STAGING",error=null); dao.updateItem(current)
                val hashes=if(job.kind=="PICKER") {
                    val token=PhotosAuthorization(context).token(job.email)
                    val picker=if(client==null) PhotosPickerGateway(token) else PhotosPickerGateway(token,client)
                    picker.download(picker.find(requireNotNull(job.session),current.path),stage,::keepGoing)
                } else stageTakeout(current,stage,client,::keepGoing)
                val sha=hashes.sha256; val md5=hashes.md5
                require(current.sha256==null || current.sha256==sha) { "Source bytes changed since interrupted transfer. Original checkpoint preserved." }
                current=current.copy(size=stage.length(),staged=stage.absolutePath,sha256=sha,md5=md5,state="UPLOADING")
                dao.updateItem(current)
            }
            keepGoing()
            val importThumb=File(context.filesDir,"thumbnails/${current.id}.jpg")
            val bitmap=PhotoRepository.mediaPreview(File(requireNotNull(current.staged)),current.mime,480)
            try {
                importThumb.parentFile?.mkdirs()
                importThumb.outputStream().use { check(bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG,85,it)) }
            } finally { bitmap.recycle() }
            val match=ImportDuplicateFinder(db.cloud(),api).find(job.binding,source.folderId,current,::keepGoing)
            val duplicateRemote=match?.remote
            val remote: DriveFile
            if(duplicateRemote!=null) {
                val hashes=MediaFingerprint.read(File(requireNotNull(current.staged)),::keepGoing)
                require(hashes.bytes==current.size && hashes.sha256==current.sha256 && hashes.md5==current.md5) {
                    "Staged bytes changed. Source and checkpoints are preserved."
                }
                remote=duplicateRemote; current=current.copy(remoteId=remote.id,state="VERIFIED",offset=current.size)
                dao.updateItem(current)
            } else {
                require(db.cloud().liveCount(source.key) < 10000) { "MPA's 10,000 asset catalogue limit reached. Split into a new Drive source." }
                val result=ImportTransfer(api).upload(current,source.folderId,{ dao.updateItem(it) },::keepGoing)
                current=result.first; remote=result.second
            }
            keepGoing()
            app.cache.lock.withLock { db.withTransaction {
                keepGoing()
                val key="${source.key}:${remote.id}"
                val previous=db.cloud().photo(key)
                val existingPhoto=previous?.photoId?.let { db.photos().find(it) }?.takeIf {
                    it.sourceBindingKey==source.key && it.sourceRemoteId==remote.id &&
                        it.sha256==current.sha256 && it.bytes==current.size
                }
                val media=File(requireNotNull(current.staged))
                val gps=runCatching { androidx.exifinterface.media.ExifInterface(media).latLong }.getOrNull()
                val photo=existingPhoto?.copy(cachedVersion=remote.version,displayName=remote.name) ?: Photo(current.id,current.name,"",importThumb.absolutePath,current.mime,current.size,System.currentTimeMillis(),requireNotNull(current.sha256),latitude=gps?.get(0),longitude=gps?.get(1),
                    capturedAt=PhotoRepository.captureTime(media),sourceBindingKey=source.key,sourceRemoteId=remote.id,
                    cachedVersion=remote.version,metadataAssetId=previous?.assetId ?: remote.assetId ?: current.id)
                val incoming=if(job.kind=="TAKEOUT" && current.metadata!=null) codec.parse(requireNotNull(current.metadata)) else null
                val base=if(existingPhoto==null && incoming!=null) photo.copy(capturedAt=incoming.captured ?: photo.capturedAt,
                    latitude=incoming.latitude ?: photo.latitude,longitude=incoming.longitude ?: photo.longitude) else photo
                var annotated=if(incoming!=null) codec.apply(base,incoming) else base
                if(job.kind=="PICKER" && annotated.importedMetadata.isBlank()) {
                    val provenance=com.google.gson.JsonParser.parseString(requireNotNull(current.metadata)).asJsonObject
                    annotated=annotated.copy(importedMetadata=JsonObject().apply { add("googlePhotosPicker",provenance) }.toString(),
                        capturedAt=annotated.capturedAt ?: provenance.get("capturedAt")?.asLong)
                }
                // No second full local copy. Verified migration originals are fetched through the normal bounded cache on demand.
                if(existingPhoto==null) db.photos().insert(annotated) else db.photos().update(annotated)
                if(current.album.isNotBlank()) {
                    val albumId=TakeoutDocuments.stableId("${source.key}:takeout:${current.album}")
                    if(db.library().allAlbums().none { it.id==albumId }) db.library().saveAlbum(Album(albumId,current.album,System.currentTimeMillis()))
                    db.library().addToAlbum(AlbumPhoto(albumId,annotated.id))
                }
                db.cloud().savePhoto(CloudPhoto(key,source.key,remote.id,remote.parents.firstOrNull() ?: source.folderId,remote.name,remote.mimeType,remote.bytes,remote.md5,remote.version,
                    remote.modifiedTime,annotated.capturedAt,annotated.latitude,annotated.longitude,
                    thumbnailPath=annotated.thumbnailPath.ifBlank { importThumb.absolutePath },thumbnailVersion=remote.version,
                    photoId=annotated.id,assetId=annotated.metadataAssetId,sourceAssetId=previous?.sourceAssetId ?: remote.sourceAssetId,thumbnailUpdatedAt=System.currentTimeMillis(),sha256=current.sha256))
                dao.updateItem(current.copy(state="COMPLETE",photoId=annotated.id,staged=null,error=null))
                // Project the candidate before committing: never silently discard annotations at XMP limits.
                XmpCodec.encode(app.metadata.snapshot(source))
            } }
            stage.delete() // Only after verified remote bytes and the atomic metadata/link commit.
            app.metadata.schedule(); app.people.schedule(); app.sync.schedule(source.connection(),true)
            true
        } catch(e: CancellationException) { throw e }
        catch(e: DriveConnectionException) { throw e }
        catch(e: Exception) {
            dao.item(initial.id)?.let { dao.updateItem(it.copy(state="FAILED",error=e.message ?: "Import failed. Source preserved.")) }
            dao.connectionState(id,"FAILED",e.message)
            false
        }
    } }

}

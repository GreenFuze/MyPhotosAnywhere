package com.myphotosanywhere.mpa.importing

import android.content.Context
import androidx.room.withTransaction
import androidx.work.*
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.metadata.XmpCodec
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Full provider JSON is retained inside XMP before the source sidecar is moved to Drive trash. */
class DriveTakeoutMetadata(private val context: Context) {
    private val app get()=context.applicationContext as MpaApplication
    private val db get()=app.database
    private val preferences=context.getSharedPreferences("takeout-conversion",Context.MODE_PRIVATE)
    private val lock=Mutex()
    fun enabled(binding: String)=preferences.getBoolean(binding,false)
    suspend fun enable(binding: String,value: Boolean) = withContext(Dispatchers.IO) {
        com.myphotosanywhere.mpa.core.CheckedPreferences(preferences).commit("Cannot save Takeout metadata consent.") { putBoolean(binding,value) }
        if(value) db.drive().source(binding)?.let { schedule(binding,it.unmeteredOnly) }
        else WorkManager.getInstance(context).cancelUniqueWork("mpa-takeout:$binding")
    }
    fun schedule(binding: String,unmetered: Boolean=true,append: Boolean=false) {
        if(!enabled(binding)) return
        WorkManager.getInstance(context).enqueueUniqueWork("mpa-takeout:$binding",if(append) ExistingWorkPolicy.APPEND_OR_REPLACE else ExistingWorkPolicy.KEEP,
            OneTimeWorkRequestBuilder<DriveTakeoutWorker>().setInputData(workDataOf("binding" to binding))
                .setBackoffCriteria(BackoffPolicy.LINEAR,30,java.util.concurrent.TimeUnit.SECONDS)
                .setConstraints(Constraints.Builder().setRequiredNetworkType(if(unmetered) NetworkType.UNMETERED else NetworkType.CONNECTED).build()).build())
    }
    suspend fun runOne(binding: String): Boolean = lock.withLock { withContext(Dispatchers.IO) {
        if(!enabled(binding)) return@withContext false
        val source=db.drive().source(binding) ?: return@withContext false
        val sidecar=db.imports().sidecars(binding).firstOrNull { it.state in listOf("PENDING","WAITING_MEDIA","WAITING_BACKUP","CONVERTED") }
            ?: return@withContext false
        try {
            require(app.settings.state.value.sidecarBackup) { "Enable XMP annotation backup first. Google JSON is preserved until its MPA backup is verified." }
            require(sidecar.version!=null && sidecar.md5!=null && sidecar.bytes in 1..TakeoutCodec.MAX_JSON_BYTES) { "Unsupported JSON size or revision. Source preserved." }
            val api=app.sync.api(source.connection())
            val before=api.file(sidecar.remoteId)
            if(before.trashed) { db.imports().updateSidecar(sidecar.copy(state="GONE",error=null)); return@withContext true }
            require(before.version==sidecar.version && before.md5==sidecar.md5 && before.bytes==sidecar.bytes &&
                before.name==sidecar.name && sidecar.parent in before.parents && app.sync.inside(api,before,source.folderId)) { "Sidecar changed or moved. Refresh Drive; JSON preserved." }
            val read=api.readMetadata(before.id)
            require(read.first.toByteArray(Charsets.UTF_8).size.toLong()==before.bytes &&
                java.security.MessageDigest.getInstance("MD5").digest(read.first.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }==before.md5)
            val codec=TakeoutCodec(); val metadata=codec.parse(read.first)
            val media=db.cloud().photos(binding).filter { !it.removed && it.parentId==sidecar.parent }
            val candidates=codec.candidates(sidecar.name,metadata,media.map { it.name })
            val matching=media.filter { it.name in candidates }
            require(matching.size==1) { "Missing or ambiguous media match. JSON preserved; resolve filenames in Drive and retry." }
            val row=matching.single()
            val remote=api.file(row.remoteId)
            require(!remote.trashed && remote.version==row.version && remote.md5==row.md5 && remote.bytes==row.bytes &&
                sidecar.parent in remote.parents && app.sync.inside(api,remote,source.folderId)) { "Media changed. Refresh Drive before converting metadata." }
            val photo=row.photoId?.let { db.photos().find(it) }
            if(photo==null) {
                require(remote.bytes in 1..TakeoutDocuments.MAX_MEDIA) { "Matching media exceeds the 250 MiB verification limit. JSON preserved." }
                val download=db.cloud().download(row.key)
                require(download?.state !in listOf("FAILED","AUTH_REQUIRED")) { download?.error ?: "Media verification failed. Retry the media download before converting JSON." }
                if(download==null || download.state=="COMPLETE") app.downloads.enqueue(row)
                db.imports().updateSidecar(sidecar.copy(state="WAITING_MEDIA",error="Waiting for a verified media download."))
                return@withContext true
            }
            require(photo.bytes==remote.bytes && photo.cachedVersion==remote.version && photo.sourceRemoteId==remote.id) { "Media linkage is stale. JSON preserved." }
            app.cache.lock.withLock { db.withTransaction {
                val current=db.photos().find(photo.id) ?: error("Photo missing.")
                require(db.cloud().photo(row.key)?.version==row.version && db.drive().source(binding)!=null)
                db.photos().update(codec.apply(current,metadata))
                db.imports().updateSidecar(sidecar.copy(state="CONVERTED",json=read.first,photoId=photo.id,error=null))
                XmpCodec.encode(app.metadata.snapshot(source)) // Transaction rolls back if portable limits fail.
            } }
            app.metadata.upload(binding)
            val state=db.metadata().state(binding) ?: error("MPA metadata backup missing.")
            val catalogId=state.remoteId ?: error("MPA metadata is still local-only. Google JSON preserved.")
            TakeoutCleanup(api).verifyAndTrash(source.accountId,source.folderId,before,read.second,read.first,remote,photo.sha256,catalogId) {
                require(enabled(binding) && app.settings.state.value.sidecarBackup && db.drive().source(binding)!=null)
            }
            db.imports().updateSidecar(sidecar.copy(state="COMPLETE",json=read.first,photoId=photo.id,error=null))
            app.cache.enforce()
            true
        } catch(e: CancellationException) { throw e }
        catch(e: Exception) {
            db.imports().updateSidecar((db.imports().sidecar(sidecar.key) ?: sidecar).copy(state="NEEDS_REVIEW",error=e.message ?: "Metadata conversion failed. JSON preserved."))
            true
        }
    } }
    suspend fun retry(binding: String) {
        db.imports().sidecars(binding).filter { it.state=="NEEDS_REVIEW" }.forEach {
            db.imports().updateSidecar(it.copy(state="PENDING",error=null))
        }
        val source=db.drive().source(binding) ?: return
        schedule(binding,source.unmeteredOnly)
    }
}

class DriveTakeoutWorker(context: Context,params: WorkerParameters): CoroutineWorker(context,params) {
    override suspend fun doWork(): Result {
        val app=applicationContext as MpaApplication
        val binding=inputData.getString("binding") ?: return Result.failure()
        val processed=app.takeoutMetadata.runOne(binding)
        if(processed) {
            if(app.database.imports().sidecars(binding).any { it.state=="WAITING_MEDIA" }) return Result.retry()
            app.database.drive().source(binding)?.let { app.takeoutMetadata.schedule(binding,it.unmeteredOnly,true) }
        }
        return Result.success()
    }
}

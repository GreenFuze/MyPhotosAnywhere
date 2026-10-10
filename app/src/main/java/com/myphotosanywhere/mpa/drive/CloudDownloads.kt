package com.myphotosanywhere.mpa.drive

import android.content.Context
import android.net.Uri
import androidx.room.withTransaction
import androidx.work.*
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.PhotoRepository
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.sync.withLock
import java.io.File
import java.util.concurrent.TimeUnit

class CloudDownloads(private val context: Context) {
    private val app get() = context.applicationContext as MpaApplication
    suspend fun enqueue(row: CloudPhoto,pin: Boolean = false,retry: Boolean = false) {
        val active=app.database.drive().connection() ?: error("Connect Drive first.")
        require(active.binding()?.key==row.bindingKey && !row.removed) { "Choose this photo's Drive library first." }
        val old=app.database.cloud().download(row.key)
        app.database.cloud().saveDownload(DownloadTask(row.key,row.bindingKey,row.remoteId,pin || old?.pinned==true))
        schedule(row.key,active.unmeteredOnly,retry)
    }
    private fun schedule(key: String,unmetered: Boolean,replace: Boolean = false) {
        WorkManager.getInstance(context).enqueueUniqueWork("mpa-download:$key",
            if(replace) ExistingWorkPolicy.REPLACE else ExistingWorkPolicy.KEEP,
            OneTimeWorkRequestBuilder<CloudDownloadWorker>().setInputData(workDataOf("key" to key))
                .setConstraints(Constraints.Builder().setRequiredNetworkType(if(unmetered) NetworkType.UNMETERED else NetworkType.CONNECTED).build())
                .setBackoffCriteria(BackoffPolicy.EXPONENTIAL,30,TimeUnit.SECONDS).addTag(TAG).build())
    }
    suspend fun resume() {
        val active=app.database.drive().connection() ?: return
        val key=active.binding()?.key ?: return
        app.database.cloud().pendingDownloads(key).forEach { schedule(it.key,active.unmeteredOnly) }
    }
    suspend fun retry() {
        val active=app.database.drive().connection() ?: return
        val binding=active.binding()?.key ?: return
        val tasks=app.database.cloud().observeDownloads().first()
        tasks.filter { it.bindingKey==binding && it.state!="COMPLETE" }.forEach {
            app.database.cloud().saveDownload(it.copy(state="PENDING",error=null)); schedule(it.key,active.unmeteredOnly,true)
        }
    }
    fun stop() { WorkManager.getInstance(context).cancelAllWorkByTag(TAG) }
    companion object { const val TAG="mpa-cloud-download" }
}
class CloudDownloadWorker(context: Context,params: WorkerParameters): CoroutineWorker(context,params) {
    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        val app=applicationContext as MpaApplication; val db=app.database; val dao=db.cloud()
        val key=inputData.getString("key") ?: return@withContext Result.failure()
        var task=dao.download(key) ?: return@withContext Result.success()
        val active=db.drive().connection() ?: return@withContext Result.success()
        if(active.binding()?.key!=task.bindingKey) return@withContext Result.success()
        val temp=File(app.filesDir,"download-stage/${java.security.MessageDigest.getInstance("SHA-256").digest(key.toByteArray()).joinToString(""){"%02x".format(it)}}.part")
        try {
            val row=dao.photo(key) ?: error("Photo is missing from the cloud index.")
            require(!row.removed) { "This photo is no longer available in the selected Drive folder." }
            val api=app.sync.api(active)
            val before=api.file(row.remoteId)
            require(!before.trashed && before.mimeType.startsWith("image/") || !before.trashed && before.mimeType.startsWith("video/"))
            require(before.md5!=null && before.version!=null && before.bytes>0 && before.bytes<=PhotoRepository.MAX_IMPORT_BYTES) {
                "A verified download requires a Drive checksum and a file no larger than 250 MiB."
            }
            require(app.sync.inside(api,before,requireNotNull(active.folderId))) { "This photo moved outside the selected folder." }
            val existing=row.photoId?.let { db.photos().find(it) }
            if(existing!=null && existing.originalPath.isNotBlank() && File(existing.originalPath).isFile &&
                row.version==before.version && (existing.sourceRemoteId==null || existing.cachedVersion==before.version)) {
                app.cache.lock.withLock { db.photos().find(existing.id)?.let { db.photos().update(it.copy(pinned=it.pinned || task.pinned)) } }
                dao.saveDownload(task.copy(state="COMPLETE",error=null)); return@withContext Result.success()
            }
            task=task.copy(state="DOWNLOADING",error=null); dao.saveDownload(task)
            temp.parentFile?.mkdirs()
            // Retries overwrite only a staging file. Completed media is never exposed before verification.
            api.download(before.id,temp,PhotoRepository.MAX_IMPORT_BYTES)
            require(temp.length()==before.bytes && DriveBackupWorker.digest(temp,"MD5")==before.md5) { "Downloaded file checksum did not match Drive." }
            val after=api.file(before.id)
            require(!after.trashed && before.version==after.version && before.md5==after.md5 &&
                app.sync.inside(api,after,requireNotNull(active.folderId))) { "The file changed or moved during download. Retry it." }
            require(db.drive().connection()?.binding()?.key==task.bindingKey) { "Selected Drive library changed. Download paused." }
            app.cache.lock.withLock {
                // A missing disposable original can be restored under its existing local ID, preserving albums and face corrections.
                val latest=dao.photo(key) ?: error("Cloud index changed.")
                val saved=latest.photoId?.let { db.photos().find(it) }
                if(saved!=null && saved.sourceRemoteId==before.id && saved.cachedVersion==before.version && saved.originalPath.isBlank()) {
                    val destination=File(app.filesDir,"originals/${saved.id}")
                    check(temp.renameTo(destination)) { "Cannot store downloaded photo." }
                    try { db.withTransaction {
                        db.photos().update(saved.copy(originalPath=destination.absolutePath,pinned=saved.pinned || task.pinned,
                            lastAccess=System.currentTimeMillis()))
                        dao.saveDownload(task.copy(state="COMPLETE",error=null))
                    } } catch(e: Exception) { destination.delete(); throw e }
                } else app.repository.importPhoto(Uri.fromFile(temp),nameOverride=before.name,mimeOverride=before.mimeType) { photo ->
                    db.photos().update(photo.copy(sourceBindingKey=task.bindingKey,sourceRemoteId=before.id,cachedVersion=before.version,
                        pinned=task.pinned,capturedAt=photo.capturedAt ?: before.capturedAt,
                        latitude=photo.latitude ?: before.latitude,longitude=photo.longitude ?: before.longitude,
                        parentId=before.sourceAssetId?.let { source -> db.photos().all().find { it.id==source }?.id }))
                    dao.savePhoto(latest.copy(photoId=photo.id,version=before.version,md5=before.md5,bytes=before.bytes))
                    db.drive().insertBackup(DriveBackup("${task.bindingKey}:${photo.id}",task.bindingKey,photo.id,active.accountId,
                        active.email,requireNotNull(active.folderId),active.mode,remoteId=before.id,state="COMPLETE",
                        uploadedBytes=photo.bytes,remoteMd5=before.md5,remoteVersion=before.version))
                    dao.saveDownload(task.copy(state="COMPLETE",error=null))
                }
            }
            app.people.schedule(); app.cache.enforce()
            Result.success()
        } catch(e: CancellationException) { throw e }
        catch(e: Exception) {
            val auth=e is DriveAuthorizationNeeded || e is DriveHttpException && e.status==401
            val retry=!auth && (e is java.io.IOException && (e !is DriveHttpException || e.retryable)) && runAttemptCount<5
            dao.saveDownload(task.copy(state=if(auth) "AUTH_REQUIRED" else if(retry) "RETRYING" else "FAILED",
                error=e.message ?: "Could not download this photo."))
            if(retry) Result.retry() else Result.success()
        } finally { temp.delete() }
    }
}

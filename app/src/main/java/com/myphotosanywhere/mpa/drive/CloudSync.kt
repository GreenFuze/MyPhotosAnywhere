package com.myphotosanywhere.mpa.drive

import android.content.Context
import androidx.work.*
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.data.PhotoRepository
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import java.util.UUID
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.util.concurrent.TimeUnit

class CloudSync(private val context: Context, private val database: PhotoDatabase) {
    private val app get() = context.applicationContext as MpaApplication
    private val cloud = database.cloud()
    private val sourceLocks = ConcurrentHashMap<String, Mutex>()
    private val networkSlots = Semaphore(2)
    private val ingestion by lazy { DriveIngestion(database, app.cache.lock, ::save) }
    fun schedule(active: DriveConnection, now: Boolean = false) {
        val binding=active.binding() ?: return
        val constraints=Constraints.Builder().setRequiredNetworkType(
            if(active.unmeteredOnly) NetworkType.UNMETERED else NetworkType.CONNECTED).build()
        val data=workDataOf("bindingKey" to binding.key)
        val manager=WorkManager.getInstance(context)
        manager.enqueueUniquePeriodicWork("mpa-sync:${binding.key}",ExistingPeriodicWorkPolicy.UPDATE,
            PeriodicWorkRequestBuilder<CloudSyncWorker>(15,TimeUnit.MINUTES).setConstraints(constraints)
                .setInputData(data).addTag(SYNC_TAG).addTag("$SYNC_TAG:${binding.key}").build())
        if(now) manager.enqueueUniqueWork("mpa-scan:${binding.key}",ExistingWorkPolicy.KEEP,
            OneTimeWorkRequestBuilder<CloudSyncWorker>().setConstraints(constraints).setInputData(data).addTag(SYNC_TAG).addTag("$SYNC_TAG:${binding.key}").build())
    }
    suspend fun api(active: DriveConnection): DriveGateway {
        val auth=DriveAuthorization(context)
        val token=auth.token(active.email,DriveMode.valueOf(active.mode))
        val api=DriveGateway(token)
        try { val user=api.user(); if(user.id!=active.accountId || !user.email.equals(active.email,true)) throw DriveAuthorizationNeeded() }
        catch(e: DriveHttpException) { if(e.status==401) auth.invalidate(token); throw e }
        return api
    }
    private suspend fun ensureBinding(binding: String) {
        currentCoroutineContext().ensureActive()
        check(database.drive().source(binding)!=null) { "This Drive source was disconnected. Sync paused." }
    }
    suspend fun run(active: DriveConnection): Boolean {
        val binding = requireNotNull(active.binding()).key
        return sourceLocks.computeIfAbsent(binding) { Mutex() }.withLock {
            networkSlots.withPermit { withContext(Dispatchers.IO) {
                val previous = cloud.sync(binding) ?: SyncState(binding)
                cloud.saveSync(previous.copy(running = true, error = null, status = "DISCOVERING"))
                try {
                    ensureBinding(binding)
                    val api = api(active)
                    val root = api.file(requireNotNull(active.folderId))
                    require(!root.trashed && root.mimeType == DriveGateway.FOLDER_MIME) { "The selected Drive folder is unavailable." }
                    val complete = ingestion.run(api, active)
                    ensureBinding(binding)
                    val state = requireNotNull(cloud.sync(binding))
                    cloud.saveSync(state.copy(running = false, error = null, status = if (complete) "IDLE" else "WAITING"))
                    complete
                } catch (e: CancellationException) {
                    withContext(NonCancellable) {
                        cloud.saveSync((cloud.sync(binding) ?: previous).copy(running = false, status = "WAITING"))
                    }
                    throw e
                } catch (e: Exception) {
                    val auth = e is DriveAuthorizationNeeded || e is DriveHttpException && e.status == 401
                    cloud.saveSync((cloud.sync(binding) ?: previous).copy(running = false,
                        status = if (auth) "AUTH_REQUIRED" else "FAILED", error = e.message ?: "Drive sync failed."))
                    throw e
                }
            } }
        }
    }

    internal suspend fun reconcile(api: DriveGateway, active: DriveConnection, start: String) =
        ingestion.reconcile(api, active, start)

    fun continueScan(active: DriveConnection) {
        val binding = requireNotNull(active.binding()).key
        val constraints = Constraints.Builder().setRequiredNetworkType(
            if (active.unmeteredOnly) NetworkType.UNMETERED else NetworkType.CONNECTED).build()
        // Appending also works when this pass was periodic. A crash before enqueue is recovered
        // by the next periodic/foreground refresh from the same durable checkpoint.
        WorkManager.getInstance(context).enqueueUniqueWork("mpa-scan:$binding", ExistingWorkPolicy.APPEND_OR_REPLACE,
            OneTimeWorkRequestBuilder<CloudSyncWorker>().setConstraints(constraints)
                .setInputData(workDataOf("bindingKey" to binding)).addTag(SYNC_TAG)
                .addTag("$SYNC_TAG:$binding").build())
    }
    internal suspend fun save(binding: String,parent: String,file: DriveFile,generation: String) {
        val old=cloud.photo("$binding:${file.id}")
        val backup=database.drive().backupForRemote(binding,file.id)
        val account=database.drive().source(binding)?.accountId
        val shared=if(account!=null && file.md5!=null) cloud.sameAccountCopy(account,file.id,file.md5,file.bytes) else null
        val oldMatches=file.md5!=null && old?.md5==file.md5 && old.bytes==file.bytes
        val backupMatches=file.md5!=null && backup?.remoteMd5==file.md5 && backup.uploadedBytes==file.bytes
        val linkedId=(if(oldMatches) old?.photoId else null) ?:
            (if(backupMatches) backup?.photoId else null) ?: shared?.photoId
        val linked=linkedId?.let { database.photos().find(it) }
        if(linked!=null && linked.sourceRemoteId==file.id && linked.cachedVersion==old?.version && oldMatches)
            database.photos().update(linked.copy(cachedVersion=file.version,displayName=file.name))
        val thumbnailPath = if (oldMatches) requireNotNull(old).thumbnailPath else {
            val content = UUID.nameUUIDFromBytes("${file.md5}:${file.bytes}:${file.version}".toByteArray(Charsets.UTF_8))
            File(context.filesDir,"cloud-thumbnails/$binding/${file.id}-$content.jpg").absolutePath
        }
        cloud.savePhoto(CloudPhoto("$binding:${file.id}",binding,file.id,parent,file.name,file.mimeType,file.bytes,file.md5,
            file.version,file.modifiedTime,file.capturedAt,file.latitude,file.longitude,
            thumbnailPath,if(oldMatches) old?.thumbnailVersion else null,linkedId,false,generation,file.assetId,file.sourceAssetId,
            if(oldMatches) old?.thumbnailUpdatedAt ?: 0 else 0))
    }
    suspend fun thumbnail(api: DriveGateway,binding: String,file: DriveFile) {
        val row=cloud.photo("$binding:${file.id}") ?: return
        if(file.version!=null && row.thumbnailVersion==file.version && File(row.thumbnailPath).isFile) return
        val link=file.thumbnailLink ?: return
        val target=File(row.thumbnailPath); target.parentFile?.mkdirs()
        val temp=File(target.parentFile,"${file.id}.part")
        try {
            api.thumbnail(link,temp)
            val bitmap=PhotoRepository.preview(temp,480)
            try { temp.outputStream().use { check(bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG,85,it)) } }
            finally { bitmap.recycle() }
            app.cache.lock.withLock {
                ensureBinding(binding)
                val current=cloud.photo(row.key) ?: return@withLock
                if(current.version==file.version && temp.renameTo(target)) cloud.savePhoto(current.copy(thumbnailVersion=file.version,thumbnailUpdatedAt=System.currentTimeMillis()))
            }
        } catch(e: CancellationException) { throw e }
        catch(_: Exception) { /* The metadata remains browsable when a thumbnail is unavailable. */ }
        finally { temp.delete() }
    }
    suspend fun inside(api: DriveGateway,file: DriveFile,root: String): Boolean {
        val queue=ArrayDeque<String>(); file.parents.forEach { queue.add(it) }; val visited=mutableSetOf<String>()
        while(queue.isNotEmpty()) {
            val id=queue.removeFirst(); if(id==root) return true
            if(!visited.add(id)) continue
            require(visited.size<=64) { "Drive ancestry is too deep." }
            val parent=api.file(id)
            if(!parent.trashed) parent.parents.forEach { queue.add(it) }
        }
        return false
    }
    fun stop(binding: String) {
        val manager=WorkManager.getInstance(context)
        manager.cancelAllWorkByTag("$SYNC_TAG:$binding")
        // These names also cancel workers created by 0.3, before per-source tags existed.
        manager.cancelUniqueWork("mpa-sync:$binding"); manager.cancelUniqueWork("mpa-scan:$binding")
    }
    companion object { const val SYNC_TAG="mpa-cloud-sync" }
}
class CloudSyncWorker(context: Context,params: WorkerParameters): CoroutineWorker(context,params) {
    override suspend fun doWork(): Result {
        val app=applicationContext as MpaApplication
        val binding=inputData.getString("bindingKey") ?: return Result.failure()
        val active=app.database.drive().source(binding)?.connection() ?: return Result.success()
        return try {
            val complete = app.sync.run(active)
            app.people.schedule() // Existing consent/schedule gates and bounded download queue apply.
            if (!complete) app.database.drive().source(binding)?.let { app.sync.continueScan(it.connection()) }
            // Downloads/indexing and explicit storage controls enforce cache budgets separately;
            // network re-verification of cached originals must not extend this discovery pass.
            Result.success()
        }
        catch(e: CancellationException) { throw e }
        catch(e: DriveAuthorizationNeeded) { Result.success() }
        catch(e: DriveHttpException) { if(e.retryable) Result.retry() else Result.success() }
        catch(e: java.io.IOException) { Result.retry() }
        catch(_: Exception) { Result.failure() }
    }
}

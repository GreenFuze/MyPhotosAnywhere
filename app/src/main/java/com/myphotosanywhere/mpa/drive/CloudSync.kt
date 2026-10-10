package com.myphotosanywhere.mpa.drive

import android.content.Context
import androidx.room.withTransaction
import androidx.work.*
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.data.PhotoRepository
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.File
import java.util.UUID
import java.util.concurrent.TimeUnit

class CloudSync(private val context: Context, private val database: PhotoDatabase) {
    private val app get() = context.applicationContext as MpaApplication
    private val cloud = database.cloud()
    private val scanLock = Mutex()
    fun schedule(active: DriveConnection, now: Boolean = false) {
        val binding=active.binding() ?: return
        val constraints=Constraints.Builder().setRequiredNetworkType(
            if(active.unmeteredOnly) NetworkType.UNMETERED else NetworkType.CONNECTED).build()
        val data=workDataOf("bindingKey" to binding.key)
        val manager=WorkManager.getInstance(context)
        manager.enqueueUniquePeriodicWork("mpa-sync:${binding.key}",ExistingPeriodicWorkPolicy.UPDATE,
            PeriodicWorkRequestBuilder<CloudSyncWorker>(15,TimeUnit.MINUTES).setConstraints(constraints)
                .setInputData(data).addTag(SYNC_TAG).build())
        if(now) manager.enqueueUniqueWork("mpa-scan:${binding.key}",ExistingWorkPolicy.KEEP,
            OneTimeWorkRequestBuilder<CloudSyncWorker>().setConstraints(constraints).setInputData(data).addTag(SYNC_TAG).build())
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
        check(database.drive().connection()?.binding()?.key==binding) { "Selected Drive library changed. Sync paused." }
    }
    suspend fun run(active: DriveConnection) = scanLock.withLock { withContext(Dispatchers.IO) {
        val binding=requireNotNull(active.binding()).key
        val previous=cloud.sync(binding) ?: SyncState(binding)
        cloud.saveSync(previous.copy(running=true,error=null))
        try {
            val api=api(active)
            val root=api.file(requireNotNull(active.folderId))
            require(!root.trashed && root.mimeType==DriveGateway.FOLDER_MIME) { "The selected Drive folder is unavailable." }
            if(previous.changeToken==null || System.currentTimeMillis()-previous.lastScan>24*60*60*1000L) {
                scan(api,active)
            } else try {
                reconcile(api,active,previous.changeToken)
            } catch(e: DriveHttpException) { if(e.status==410) scan(api,active) else throw e }
            ensureBinding(binding)
            val completed=cloud.sync(binding) ?: previous
            cloud.saveSync(completed.copy(running=false,error=null))
        } catch(e: CancellationException) { cloud.saveSync((cloud.sync(binding) ?: previous).copy(running=false)); throw e }
        catch(e: Exception) { cloud.saveSync((cloud.sync(binding) ?: previous).copy(running=false,error=e.message ?: "Drive sync failed.")); throw e }
    }
    }
    private suspend fun scan(api: DriveGateway,active: DriveConnection) {
        val binding=requireNotNull(active.binding()).key
        val generation=UUID.randomUUID().toString()
        val token=api.startToken() // Fetch before enumeration so concurrent modifications are replayed.
        val root=requireNotNull(active.folderId)
        val queue=ArrayDeque<String>(); queue.add(root)
        val visited=mutableSetOf<String>()
        cloud.saveFolder(CloudFolder(binding,root,null,active.folderName ?: "Photo folder",generation))
        while(queue.isNotEmpty()) {
            ensureBinding(binding)
            val folder=queue.removeFirst(); if(!visited.add(folder)) continue
            require(visited.size<=10000) { "This library has too many folders for one scan." }
            var page: String?=null
            do {
                ensureBinding(binding)
                val result=api.children(folder,page)
                app.cache.lock.withLock { database.withTransaction {
                    result.files.forEach { file ->
                        if(file.mimeType==DriveGateway.FOLDER_MIME) {
                            cloud.saveFolder(CloudFolder(binding,file.id,folder,file.name,generation)); queue.add(file.id)
                        } else save(binding,folder,file,generation)
                    }
                } }
                result.files.filter { it.mimeType!=DriveGateway.FOLDER_MIME }.forEach { thumbnail(api,binding,it) }
                page=result.nextPage
            } while(page!=null)
        }
        ensureBinding(binding)
        app.cache.lock.withLock { database.withTransaction {
            cloud.markAbsent(binding,generation); cloud.pruneFolders(binding,generation)
            cloud.saveSync(SyncState(binding,token,System.currentTimeMillis(),running=true))
        } }
        reconcile(api,active,token)
    }
    private suspend fun reconcile(api: DriveGateway,active: DriveConnection,start: String) {
        val binding=requireNotNull(active.binding()).key
        var token=start
        while(true) {
            ensureBinding(binding)
            val page=api.changes(token)
            val folders=cloud.folders(binding).map { it.remoteId }.toSet()
            // Folder changes require an authoritative tree scan; no recursive removal on incomplete data.
            if(page.changes.any { it.file?.mimeType==DriveGateway.FOLDER_MIME || (it.removed && it.id in folders) }) {
                scanWithoutReplay(api,active); return
            }
            app.cache.lock.withLock { database.withTransaction {
                page.changes.forEach { change ->
                    val file=change.file
                    if(change.removed || file==null || file.trashed || file.parents.none { it in folders }) cloud.markRemoved(binding,change.id)
                    else if(file.mimeType.startsWith("image/") || file.mimeType.startsWith("video/"))
                        save(binding,file.parents.first { it in folders },file,cloud.photo("$binding:${file.id}")?.seenGeneration ?: "changes")
                }
                // Transactional checkpoint: retries replay a page, never skip its rows.
                val next=page.nextPage ?: page.newStart ?: error("Drive returned no next change token.")
                cloud.saveSync((cloud.sync(binding) ?: SyncState(binding)).copy(changeToken=next,running=true))
            } }
            page.changes.mapNotNull { it.file }.filter { !it.trashed && it.parents.any { p -> p in folders } }.forEach { thumbnail(api,binding,it) }
            token=page.nextPage ?: return
        }
    }
    private suspend fun scanWithoutReplay(api: DriveGateway,active: DriveConnection) {
        // A fresh pre-scan token catches changes during the scan on the next scheduled pass.
        // Avoid repeatedly recursing into scans when folders are being changed continuously.
        val binding=requireNotNull(active.binding()).key
        val generation=UUID.randomUUID().toString(); val token=api.startToken()
        val root=requireNotNull(active.folderId); val queue=ArrayDeque<String>(); queue.add(root)
        val visited=mutableSetOf<String>()
        cloud.saveFolder(CloudFolder(binding,root,null,active.folderName ?: "Photo folder",generation))
        while(queue.isNotEmpty()) {
            ensureBinding(binding); val parent=queue.removeFirst(); if(!visited.add(parent)) continue
            require(visited.size<=10000)
            var page: String?=null
            do {
                val result=api.children(parent,page); ensureBinding(binding)
                app.cache.lock.withLock { database.withTransaction { result.files.forEach { f ->
                    if(f.mimeType==DriveGateway.FOLDER_MIME) { cloud.saveFolder(CloudFolder(binding,f.id,parent,f.name,generation)); queue.add(f.id) }
                    else save(binding,parent,f,generation)
                } } }
                result.files.filter { it.mimeType!=DriveGateway.FOLDER_MIME }.forEach { thumbnail(api,binding,it) }; page=result.nextPage
            } while(page!=null)
        }
        ensureBinding(binding)
        app.cache.lock.withLock { database.withTransaction {
            cloud.markAbsent(binding,generation); cloud.pruneFolders(binding,generation)
            cloud.saveSync(SyncState(binding,token,System.currentTimeMillis(),running=true))
        } }
    }
    private suspend fun save(binding: String,parent: String,file: DriveFile,generation: String) {
        val old=cloud.photo("$binding:${file.id}")
        val backup=database.drive().backupForRemote(binding,file.id)
        cloud.savePhoto(CloudPhoto("$binding:${file.id}",binding,file.id,parent,file.name,file.mimeType,file.bytes,file.md5,
            file.version,file.modifiedTime,file.capturedAt,file.latitude,file.longitude,
            old?.thumbnailPath ?: File(context.filesDir,"cloud-thumbnails/$binding/${file.id}.jpg").absolutePath,
            old?.thumbnailVersion,old?.photoId ?: backup?.photoId,false,generation,file.assetId,file.sourceAssetId))
    }
    private suspend fun thumbnail(api: DriveGateway,binding: String,file: DriveFile) {
        val row=cloud.photo("$binding:${file.id}") ?: return
        if(row.thumbnailVersion==file.version && File(row.thumbnailPath).isFile) return
        val link=file.thumbnailLink ?: return
        val target=File(row.thumbnailPath); target.parentFile?.mkdirs()
        val temp=File(target.parentFile,"${file.id}.part")
        try {
            api.thumbnail(link,temp)
            val bitmap=PhotoRepository.preview(temp,480)
            try { temp.outputStream().use { check(bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG,85,it)) } }
            finally { bitmap.recycle() }
            app.cache.lock.withLock {
                val current=cloud.photo(row.key) ?: return@withLock
                if(current.version==file.version && temp.renameTo(target)) cloud.savePhoto(current.copy(thumbnailVersion=file.version))
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
    fun stop() { WorkManager.getInstance(context).cancelAllWorkByTag(SYNC_TAG) }
    companion object { const val SYNC_TAG="mpa-cloud-sync" }
}
class CloudSyncWorker(context: Context,params: WorkerParameters): CoroutineWorker(context,params) {
    override suspend fun doWork(): Result {
        val app=applicationContext as MpaApplication
        val binding=inputData.getString("bindingKey") ?: return Result.failure()
        val active=app.database.drive().connection() ?: return Result.success()
        if(active.binding()?.key!=binding) return Result.success()
        return try { app.sync.run(active); app.cache.enforce(); Result.success() }
        catch(e: CancellationException) { throw e }
        catch(e: DriveAuthorizationNeeded) { Result.success() }
        catch(e: DriveHttpException) { if(e.retryable) Result.retry() else Result.success() }
        catch(e: java.io.IOException) { Result.retry() }
        catch(_: Exception) { Result.failure() }
    }
}

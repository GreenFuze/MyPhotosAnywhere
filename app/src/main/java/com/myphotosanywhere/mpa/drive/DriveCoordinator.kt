package com.myphotosanywhere.mpa.drive

import android.content.Context
import androidx.room.withTransaction
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.workDataOf
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.data.PhotoDatabase
import java.util.concurrent.TimeUnit

class DriveCoordinator(private val context: Context, private val database: PhotoDatabase) {
    val connection = database.drive().observeConnection()
    val sources = database.drive().observeSources()
    val backups = database.drive().observeBackups()

    suspend fun enqueue(photo: Photo) {
        if(database.deviceMedia().find(photo.id)!=null) return // Browsing/viewing camera files never authorizes uploads.
        if (photo.sourceRemoteId != null || photo.originalPath.isBlank() || database.trash().hidesPhoto(photo.id)) return
        val active = database.drive().backupDestination()?.connection() ?: return
        val binding = active.binding() ?: return
        if (!active.backupEnabled) return
        // A new default applies to unassigned assets; never silently copy or retarget old queued assets.
        val assigned=database.drive().backupsForPhoto(photo.id)
        if(assigned.isNotEmpty() && assigned.none { it.bindingKey==binding.key }) return
        val task = DriveBackup("${binding.key}:${photo.id}", binding.key, photo.id, active.accountId,
            active.email, requireNotNull(active.folderId), active.mode)
        database.drive().insertBackup(task)
        val saved = database.drive().backup(task.id) ?: return
        if (saved.state !in listOf("COMPLETE", "FAILED", "AUTH_REQUIRED")) schedule(saved, active.unmeteredOnly)
    }

    /** Explicit backup reuses an unfinished captured job; changing the default cannot retarget it. */
    internal suspend fun enqueueManual(photoId: String,bindingKey: String): String {
        val app=context.applicationContext as com.myphotosanywhere.mpa.MpaApplication
        var photo=database.photos().find(photoId) ?: throw java.io.IOException("This photo is no longer available.")
        if(photo.originalPath.isBlank() || !java.io.File(photo.originalPath).isFile) photo=app.deviceMedia.materialize(photoId)
        val file=java.io.File(photo.originalPath)
        if(file.length()!=photo.bytes || DriveBackupWorker.digest(file,"SHA-256")!=photo.sha256)
            throw java.io.IOException("The original changed. Its source file is preserved; refresh and retry.")
        val md5=DriveBackupWorker.digest(file,"MD5")
        val task=database.withTransaction {
            if(database.trash().hidesPhoto(photoId)) throw java.io.IOException("Restore this photo from Drive Trash before backing it up.")
            val assigned=database.drive().backupsForPhoto(photoId)
            val unfinished=assigned.firstOrNull { it.state!="COMPLETE" }
            if(unfinished!=null && unfinished.bindingKey!=bindingKey)
                throw java.io.IOException("This backup already has a captured destination (${unfinished.email}). Resume it there; changing the default does not retarget saved work.")
            val source=database.drive().source(unfinished?.bindingKey ?: bindingKey)
                ?: throw java.io.IOException("Reconnect the captured Drive source before resuming this backup.")
            val previous=unfinished ?: assigned.firstOrNull { it.bindingKey==source.key }
            if(previous?.state=="COMPLETE") {
                val indexed=previous.remoteId?.let { database.cloud().photo("${source.key}:$it") }
                if(indexed?.removed==true || previous.remoteMd5!=md5 || previous.remoteVersion==null ||
                    indexed!=null && (indexed.version!=previous.remoteVersion || indexed.bytes!=photo.bytes || indexed.md5!=md5))
                    throw java.io.IOException("The previous Drive backup changed or was removed. Restore or refresh its source before retrying; the device original was kept.")
            }
            val value=previous ?: DriveBackup("manual:"+java.util.UUID.randomUUID(),source.key,photoId,source.accountId,source.email,source.folderId,source.mode)
            database.drive().insertBackup(value)
            val authorization=database.deviceMedia().authorization(value.id)
            if(authorization==null) database.deviceMedia().authorize(com.myphotosanywhere.mpa.data.ManualDeviceBackup(value.id,photoId,source.key,photo.sha256,photo.bytes))
            else if(authorization.bindingKey!=source.key || authorization.sha256!=photo.sha256 || authorization.bytes!=photo.bytes)
                throw java.io.IOException("The prepared original no longer matches its saved backup authorization. The captured job was preserved.")
            value
        }
        if(task.state=="COMPLETE") return "This original is already verified in Drive."
        val source=database.drive().source(task.bindingKey) ?: throw java.io.IOException("Reconnect the captured Drive source.")
        schedule(task,source.unmeteredOnly,replace=true)
        return "Backup queued to ${source.folderName} (${source.email}). Other camera photos are not uploaded automatically."
    }

    internal suspend fun resumeManual(binding: String?=null,retryFailures: Boolean=false) {
        var after=""
        while(true) {
            val batch=database.deviceMedia().pendingManual(after,100)
            if(batch.isEmpty()) break
            for(authorization in batch) {
                if(binding!=null && authorization.bindingKey!=binding) continue
                val task=database.drive().backup(authorization.taskId) ?: error("Manual authorization references a missing backup task.")
                val source=database.drive().source(task.bindingKey) ?: continue
                if(!database.trash().hidesPhoto(task.photoId) && (retryFailures || task.state !in listOf("FAILED","AUTH_REQUIRED")))
                    schedule(task,source.unmeteredOnly,replace=retryFailures)
            }
            after=batch.last().taskId
        }
    }

    suspend fun resume(replace: Boolean = false, retryFailures: Boolean = false) {
        resumeManual(retryFailures=retryFailures)
        val active = database.drive().backupDestination()?.connection() ?: return
        if (!active.backupEnabled) return
        val binding = active.binding() ?: return
        database.photos().all().forEach { enqueue(it) }
        database.drive().backups(binding.key).filter {
            it.state != "COMPLETE" && !database.trash().hidesPhoto(it.photoId) && (retryFailures || it.state !in listOf("FAILED", "AUTH_REQUIRED"))
        }.forEach {
            schedule(it, active.unmeteredOnly, replace)
        }
    }

    private fun schedule(task: DriveBackup, unmetered: Boolean, replace: Boolean = false) {
        val request = OneTimeWorkRequestBuilder<DriveBackupWorker>()
            .setInputData(workDataOf("backupId" to task.id))
            .setConstraints(Constraints.Builder().setRequiredNetworkType(
                if (unmetered) NetworkType.UNMETERED else NetworkType.CONNECTED).build())
            .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS)
            .addTag(BACKUP_TAG).addTag("$BACKUP_TAG:${task.bindingKey}").build()
        WorkManager.getInstance(context).enqueueUniqueWork("mpa:${task.id}",
            if (replace) ExistingWorkPolicy.REPLACE else ExistingWorkPolicy.KEEP, request)
    }

    suspend fun pause() {
        val active = database.drive().connection() ?: return
        val binding = active.binding()?.key ?: return
        database.drive().source(binding)?.let { database.drive().saveSource(it.copy(backupEnabled=false)) }
        database.drive().saveConnection(active.copy(backupEnabled = false))
        WorkManager.getInstance(context).cancelAllWorkByTag("$BACKUP_TAG:$binding")
    }

    suspend fun disconnect() {
        val binding=database.drive().connection()?.binding()?.key
        if(binding!=null) {
            database.drive().disconnectSource(binding)
            val app=context.applicationContext as com.myphotosanywhere.mpa.MpaApplication
            app.sync.stop(binding); app.downloads.stop(binding); app.metadata.stop(binding)
            WorkManager.getInstance(context).cancelAllWorkByTag("$BACKUP_TAG:$binding")
        }
        database.drive().disconnect()
    }

    companion object { const val BACKUP_TAG = "mpa-drive-backup" }
}

package com.myphotosanywhere.mpa.drive

import android.content.Context
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
    val backups = database.drive().observeBackups()

    suspend fun enqueue(photo: Photo) {
        if (photo.sourceRemoteId != null || photo.originalPath.isBlank()) return
        val active = database.drive().connection() ?: return
        val binding = active.binding() ?: return
        if (!active.backupEnabled) return
        val task = DriveBackup("${binding.key}:${photo.id}", binding.key, photo.id, active.accountId,
            active.email, requireNotNull(active.folderId), active.mode)
        database.drive().insertBackup(task)
        val saved = database.drive().backup(task.id) ?: return
        if (saved.state !in listOf("COMPLETE", "FAILED", "AUTH_REQUIRED")) schedule(saved, active.unmeteredOnly)
    }

    suspend fun resume(replace: Boolean = false, retryFailures: Boolean = false) {
        val active = database.drive().connection() ?: return
        if (!active.backupEnabled) return
        val binding = active.binding() ?: return
        database.photos().all().forEach { enqueue(it) }
        database.drive().backups(binding.key).filter {
            it.state != "COMPLETE" && (retryFailures || it.state !in listOf("FAILED", "AUTH_REQUIRED"))
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
            .addTag(BACKUP_TAG).build()
        WorkManager.getInstance(context).enqueueUniqueWork("mpa:${task.id}",
            if (replace) ExistingWorkPolicy.REPLACE else ExistingWorkPolicy.KEEP, request)
    }

    suspend fun pause() {
        val active = database.drive().connection() ?: return
        database.drive().saveConnection(active.copy(backupEnabled = false))
        WorkManager.getInstance(context).cancelAllWorkByTag(BACKUP_TAG)
    }

    suspend fun disconnect() {
        (context.applicationContext as com.myphotosanywhere.mpa.MpaApplication).sync.stop()
        (context.applicationContext as com.myphotosanywhere.mpa.MpaApplication).downloads.stop()
        database.drive().disconnect()
        WorkManager.getInstance(context).cancelAllWorkByTag(BACKUP_TAG)
    }

    companion object { const val BACKUP_TAG = "mpa-drive-backup" }
}

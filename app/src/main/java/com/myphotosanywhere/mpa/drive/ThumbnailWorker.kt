package com.myphotosanywhere.mpa.drive

import android.content.Context
import androidx.work.*
import com.myphotosanywhere.mpa.MpaApplication
import kotlinx.coroutines.CancellationException
import java.io.File

class ThumbnailWorker(context: Context,params: WorkerParameters): CoroutineWorker(context,params) {
    override suspend fun doWork(): Result {
        val app=applicationContext as MpaApplication
        val key=inputData.getString("key") ?: return Result.failure()
        val row=app.database.cloud().photo(key) ?: return Result.success()
        val active=app.database.drive().source(row.bindingKey)?.connection() ?: return Result.success()
        if(row.removed) return Result.success()
        return try {
            val api=app.sync.api(active)
            val file=api.file(row.remoteId)
            if(file.version==row.version && !file.trashed && app.sync.inside(api,file,requireNotNull(active.folderId))) app.sync.thumbnail(api,row.bindingKey,file)
            Result.success()
        } catch(e: CancellationException) { throw e }
        catch(e: DriveHttpException) { if(e.retryable) Result.retry() else Result.success() }
        catch(e: DriveAuthorizationNeeded) { Result.success() }
        catch(e: java.io.IOException) { Result.retry() }
        catch(error: com.google.android.gms.common.api.ApiException) {
            val code=error.statusCode
            if(code in setOf(com.google.android.gms.common.api.CommonStatusCodes.NETWORK_ERROR,
                com.google.android.gms.common.api.CommonStatusCodes.TIMEOUT,com.google.android.gms.common.api.CommonStatusCodes.INTERNAL_ERROR,
                com.google.android.gms.common.api.CommonStatusCodes.CONNECTION_SUSPENDED_DURING_CALL,
                com.google.android.gms.common.api.CommonStatusCodes.RECONNECTION_TIMED_OUT,
                com.google.android.gms.common.api.CommonStatusCodes.RECONNECTION_TIMED_OUT_DURING_UPDATE)) Result.retry()
            else Result.failure(workDataOf("error" to "Review this source's Google access configuration (code $code)."))
        }
    }
    companion object {
        fun request(context: Context,row: CloudPhoto,active: DriveConnection?) {
            if(active==null || row.removed || row.bindingKey!=active.binding()?.key || (com.myphotosanywhere.mpa.thumbnails.ThumbnailPolicy.currentProvider(row.thumbnailVersion,row.version) && File(row.thumbnailPath).isFile)) return
            WorkManager.getInstance(context).enqueueUniqueWork("mpa-thumbnail:${row.key}",ExistingWorkPolicy.KEEP,
                OneTimeWorkRequestBuilder<ThumbnailWorker>().setInputData(workDataOf("key" to row.key))
                    .setConstraints(Constraints.Builder().setRequiredNetworkType(
                        if(active.unmeteredOnly) NetworkType.UNMETERED else NetworkType.CONNECTED).build())
                    .addTag(CloudSync.SYNC_TAG).addTag("${CloudSync.SYNC_TAG}:${row.bindingKey}").build())
        }
    }
}

package com.myphotosanywhere.mpa.trash

import android.content.Context
import androidx.work.*
import com.myphotosanywhere.mpa.MpaApplication
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.IOException

/** WorkManager persists scheduling; each captured intent is independently reconciled before mutation. */
class DriveTrashWorker(context: Context,params: WorkerParameters): CoroutineWorker(context,params) {
    override suspend fun doWork(): Result=withContext(Dispatchers.IO) {
        val app=applicationContext as MpaApplication
        val started=android.os.SystemClock.elapsedRealtime()
        for(row in app.database.trash().pending()) {
            if(android.os.SystemClock.elapsedRealtime()-started>=120_000) return@withContext Result.retry()
            try { withTimeout(120_000) { app.trash.process(row.key) } }
            catch(_: TimeoutCancellationException) {
                app.trash.failure(row.key,IOException("Drive verification timed out. The saved intent will be reconciled before retrying."),true)
                return@withContext Result.retry()
            }
            catch(cancelled: CancellationException) { throw cancelled }
            catch(error: com.google.android.gms.common.api.ApiException) {
                val failure=DriveTrashFailures.google(error.statusCode)
                val transient=DriveTrashFailures.retryable(failure)
                app.trash.failure(row.key,failure,transient)
                if(transient) return@withContext Result.retry()
            }
            catch(error: IOException) {
                val transient=DriveTrashFailures.retryable(error)
                app.trash.failure(row.key,error,transient)
                if(transient) return@withContext Result.retry()
            }
        }
        if(app.database.trash().pendingCount()>0) Result.retry() else Result.success()
    }

    companion object {
        internal suspend fun schedule(app: MpaApplication) {
            if(withContext(Dispatchers.IO) { app.database.trash().pendingCount() }==0) return
            WorkManager.getInstance(app).enqueueUniqueWork("mpa-drive-trash",ExistingWorkPolicy.APPEND_OR_REPLACE,
                OneTimeWorkRequestBuilder<DriveTrashWorker>()
                    .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
                    .addTag("mpa-drive-trash").build())
        }
    }
}

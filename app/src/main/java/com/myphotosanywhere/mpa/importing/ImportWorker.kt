package com.myphotosanywhere.mpa.importing

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.ForegroundInfo
import androidx.work.WorkerParameters
import com.myphotosanywhere.mpa.MpaApplication
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*

/** Yield between files, retaining the repository's durable upload checkpoints on interruption. */
class ImportBatch(private val runOne: suspend () -> Boolean, private val shouldYield: () -> Boolean) {
    suspend fun run(): Boolean {
        while(runOne()) { if(shouldYield()) return true }
        return false
    }
}

class ImportWorker(context: Context, params: WorkerParameters): CoroutineWorker(context,params) {
    private val app get()=applicationContext as MpaApplication
    private val notices=ImportNotifications(context)
    private val notificationId get()=ImportTransferIds(applicationContext).id(requireNotNull(inputData.getString("job")))
    override suspend fun getForegroundInfo(): ForegroundInfo {
        val job=inputData.getString("job") ?: error("Missing import job")
        return notices.foreground(notificationId,snapshot(job),job)
    }

    private suspend fun snapshot(id: String): ImportProgress {
        val dao=app.database.imports()
        return ImportProgress.from(requireNotNull(dao.job(id)) { "Import job missing" },dao.items(id))
    }

    @OptIn(FlowPreview::class)
    override suspend fun doWork(): Result {
        val job=inputData.getString("job") ?: return Result.failure()
        if(app.database.imports().job(job)?.state!="RUNNING") return Result.success()
        if(ImportTransferScheduler.supported && ImportTransferScheduler(applicationContext).pending(job)) return Result.success()
        return try {
            try { setForeground(getForegroundInfo()) } catch(e: IllegalStateException) {
                if(!ImportForegroundRecovery.isStartRefused(e)) throw e
                val waiting=ImportForegroundRecovery(app.database.imports()).record(job)
                val progress=snapshot(job)
                notices.finish(job,if(waiting) progress.copy(state="WAITING") else progress)
                return Result.success()
            }
            app.database.imports().clearRunningError(job)
            notices.clearSummary(job)
            val execution=ImportExecution(app.database.imports(),{ app.imports.runOne(job) },{
                setForeground(notices.foreground(notificationId,it,job))
            })
            val success=execution.run(job)
            notices.finish(job,snapshot(job))
            if(success) Result.success() else Result.failure()
        } catch(e: CancellationException) { throw e }
        catch(_: Exception) {
            app.database.imports().connectionState(job,"FAILED",
                "Background import stopped. Open MPA and resume; transfer checkpoints are preserved.")
            notices.finish(job,snapshot(job))
            Result.failure()
        }
    }
}

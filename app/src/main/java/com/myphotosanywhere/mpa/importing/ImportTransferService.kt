package com.myphotosanywhere.mpa.importing

import android.app.job.JobParameters
import android.app.job.JobService
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import androidx.annotation.RequiresApi
import com.myphotosanywhere.mpa.MpaApplication
import kotlinx.coroutines.*

/** Android owns the wake lock, network constraint and notification lifetime for this transfer. */
@RequiresApi(34)
class ImportTransferService: JobService() {
    private val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate)
    private val running=mutableMapOf<Int,Job>()
    private val app get()=application as MpaApplication

    override fun onStartJob(params: JobParameters): Boolean {
        val id=requireNotNull(params.extras.getString("job")) { "Missing import identity." }
        val notices=ImportNotifications(this)
        // Post synchronously, before Room/authentication/network work (the system allows 10 seconds).
        setNotification(params,params.jobId,notices.notification(
            ImportProgress(params.extras.getString("kind") ?: "TAKEOUT","RUNNING",0,0,"Restoring import progress"),id),JOB_END_NOTIFICATION_POLICY_REMOVE)
        val transfer=scope.launch(start=CoroutineStart.LAZY) {
            try {
                val network=params.network
                if(network==null) { jobFinished(params,true); return@launch }
                val client=ImportNetwork.client(network)
                val execution=ImportExecution(app.database.imports(),{ app.imports.runOne(id,client) },{ progress ->
                    setNotification(params,params.jobId,notices.notification(progress,id),JOB_END_NOTIFICATION_POLICY_REMOVE)
                })
                notices.clearSummary(id)
                execution.run(id)
                app.database.imports().job(id)?.let { notices.finish(id,execution.snapshot(id)) }
                jobFinished(params,false)
            } catch(e: CancellationException) { throw e }
            catch(error: Exception) {
                app.database.imports().connectionState(id,"FAILED","Background import stopped. Open MPA to resume; checkpoints are preserved.")
                app.database.imports().job(id)?.let {
                    notices.finish(id,ImportProgress.from(it,app.database.imports().items(id)))
                }
                jobFinished(params,false)
                if(error !is SecurityException) throw error
            } finally { if(running[params.jobId]===currentCoroutineContext().job) running.remove(params.jobId) }
        }
        running[params.jobId]=transfer
        transfer.start()
        return true
    }

    override fun onStopJob(params: JobParameters): Boolean {
        running.remove(params.jobId)?.cancel()
        Log.i("MPA-Import","Transfer stopped by Android: reason=${params.stopReason}")
        // Pause/cancel first changes Room; a later OS retry exits without transferring any bytes.
        return params.stopReason!=JobParameters.STOP_REASON_USER
    }
    override fun onDestroy() { scope.cancel(); super.onDestroy() }
}

/** Graceful Pause keeps completed media and every resumable-upload checkpoint. */
class ImportPauseReceiver: BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val id=intent.getStringExtra("job") ?: return
        val pending=goAsync()
        CoroutineScope(SupervisorJob()+Dispatchers.IO).launch {
            try { (context.applicationContext as MpaApplication).imports.pause(id) }
            finally { pending.finish() }
        }
    }
}

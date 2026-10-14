package com.myphotosanywhere.mpa.importing

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.work.ForegroundInfo
import com.myphotosanywhere.mpa.MainActivity
import com.myphotosanywhere.mpa.R

/** Progress counts only committed, verified media; uploading bytes are not imported files. */
data class ImportProgress(val kind: String, val state: String, val complete: Int, val total: Int,
    val detail: String) {
    companion object {
        fun from(job: ImportJob, items: List<ImportItem>): ImportProgress {
            val selected=items.filter { it.job==job.id && it.selected }
            val current=selected.firstOrNull { it.state in listOf("STAGING","UPLOADING","VERIFIED") }
            val detail=when(job.state) {
                "PREVIEW" -> job.error ?: "Review selection, then import selected files"
                "PICKING" -> job.error ?: "Choose photos in the Google Photos picker"
                "DOWNLOADING" -> job.error ?: "Downloading Takeout ZIP"
                "WAITING_FOR_APP" -> job.error ?: "Resume this import to continue"
                "COMPLETE" -> "Import complete"
                "FAILED" -> job.error ?: "Import needs attention. Review the error and resume."
                "PAUSED" -> "Paused · Resume when ready"
                "CANCELLED" -> "Remaining import cancelled"
                else -> job.error ?: when(current?.state) {
                    "STAGING" -> "Preparing ${current.name}"
                    "UPLOADING" -> "Uploading ${current.name}" + if(current.size>0)
                        " · ${(current.offset.coerceIn(0,current.size)*100/current.size)}%" else ""
                    "VERIFIED" -> "Saving ${current.name}"
                    else -> "Preparing import · Waiting for the next file"
                }
            }
            return ImportProgress(job.kind,job.state,selected.count { it.state=="COMPLETE" },selected.size,ImportStatusText.inApp(detail))
        }
    }
}

class ImportNotifications(private val context: Context) {
    private val manager=context.getSystemService(NotificationManager::class.java)
    init { manager.createNotificationChannel(NotificationChannel(CHANNEL,"Photo imports",NotificationManager.IMPORTANCE_LOW)) }

    fun enabled(): Boolean = NotificationManagerCompat.from(context).areNotificationsEnabled() &&
        manager.getNotificationChannel(CHANNEL)?.importance != NotificationManager.IMPORTANCE_NONE

    fun notification(progress: ImportProgress, job: String?=null): Notification {
        val open=PendingIntent.getActivity(context,0,Intent(context,MainActivity::class.java)
            .setAction(ACTION_OPEN).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val running=progress.state=="RUNNING"
        val detail=ImportStatusText.notification(progress)
        return NotificationCompat.Builder(context,CHANNEL)
            .setSmallIcon(R.drawable.ic_import_notification)
            .setContentTitle("MPA · ${if(progress.kind=="TAKEOUT") "Takeout" else "Photos Picker"} import")
            .setContentText("${progress.complete}/${progress.total} verified in Drive · $detail")
            .setStyle(NotificationCompat.BigTextStyle().bigText("${progress.complete}/${progress.total} verified in Drive\n$detail"))
            .setContentIntent(open).setOnlyAlertOnce(true).setSilent(true)
            .setOngoing(running).setAutoCancel(!running).setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setCategory(NotificationCompat.CATEGORY_PROGRESS)
            .apply {
                if(running) {
                    setProgress(progress.total.coerceAtLeast(1),progress.complete,progress.total==0)
                    if(job!=null) addAction(0,"Pause import",PendingIntent.getBroadcast(context,
                        ImportTransferIds(context).id(job),Intent(context,ImportPauseReceiver::class.java).putExtra("job",job),
                        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
                }
            }
            .build()
    }

    fun foreground(id: Int, progress: ImportProgress, job: String?=null): ForegroundInfo = if(Build.VERSION.SDK_INT>=29)
        ForegroundInfo(id,notification(progress,job),ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        else ForegroundInfo(id,notification(progress,job))

    fun clearSummary(job: String) { manager.cancel(job,SUMMARY_ID) }

    fun finish(job: String, progress: ImportProgress) {
        if(progress.state !in listOf("COMPLETE","FAILED","PAUSED","CANCELLED","WAITING") || !enabled()) return
        // A separate ID survives WorkManager removing its foreground notification.
        try { ImportHistory(context).withSummary(job,progress.state) {
            manager.notify(job,SUMMARY_ID,notification(progress))
        } } catch(_: SecurityException) {
            // Permission can be revoked between the check and notification. Imports remain valid.
        }
    }

    companion object {
        const val ACTION_OPEN="com.myphotosanywhere.mpa.OPEN_IMPORTS"
        const val CHANNEL="photo-imports"
        private const val SUMMARY_ID=1
    }
}

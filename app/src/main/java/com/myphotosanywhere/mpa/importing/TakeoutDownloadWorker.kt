package com.myphotosanywhere.mpa.importing

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.work.CoroutineWorker
import androidx.work.ForegroundInfo
import androidx.work.WorkerParameters
import com.myphotosanywhere.mpa.MainActivity
import com.myphotosanywhere.mpa.MpaApplication
import kotlinx.coroutines.CancellationException

class TakeoutDownloadWorker(context: Context, params: WorkerParameters): CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val app=applicationContext as MpaApplication
        val job=inputData.getString("job") ?: return Result.failure()
        try {
            setForeground(info(app.database.imports().download(job)))
            app.imports.runDownload(job) { setForeground(info(it)) }
        } catch(e: CancellationException) { throw e }
        catch(_: Exception) {
            app.database.imports().job(job)?.takeIf { it.state=="DOWNLOADING" }?.let {
                app.database.imports().saveJob(it.copy(state="FAILED",error="Android stopped the ZIP download. Open MPA and resume."))
            }
        }
        return Result.success()
    }
    override suspend fun getForegroundInfo(): ForegroundInfo = info(null)

    private fun info(download: TakeoutDownload?): ForegroundInfo {
        val context=applicationContext
        val manager=context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.createNotificationChannel(NotificationChannel("takeout-downloads","Takeout downloads",NotificationManager.IMPORTANCE_LOW))
        val open=PendingIntent.getActivity(context,0,Intent(context,MainActivity::class.java),PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val total=download?.total ?: -1
        val bytes=download?.bytes ?: 0
        val text=if(total>0) "${bytes*100/total}% downloaded" else "${bytes/(1024*1024)} MiB downloaded"
        val notification=NotificationCompat.Builder(context,"takeout-downloads")
            .setSmallIcon(android.R.drawable.stat_sys_download).setContentTitle("Importing Takeout ZIP")
            .setContentText("$text · Open MPA to pause").setContentIntent(open).setOngoing(true).setOnlyAlertOnce(true)
            .setProgress(100,if(total>0) (bytes*100/total).toInt() else 0,total<=0).build()
        return if(Build.VERSION.SDK_INT>=29) ForegroundInfo(id.hashCode(),notification,ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
            else ForegroundInfo(id.hashCode(),notification)
    }
}

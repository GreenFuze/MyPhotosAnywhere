package com.myphotosanywhere.mpa.importing

import android.app.job.JobInfo
import android.app.job.JobScheduler
import android.content.ComponentName
import android.content.Context
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.os.Build
import android.os.PersistableBundle
import androidx.annotation.RequiresApi

/** IDs survive process death and never collide across imports, even for equal String hashes. */
class ImportTransferIds(context: Context) {
    private val preferences=context.getSharedPreferences("import-transfer-ids",Context.MODE_PRIVATE)
    fun id(job: String): Int = synchronized(lock) {
        require(job.isNotBlank())
        val existing=preferences.getInt("job:$job",0)
        if(existing!=0) return@synchronized existing
        val next=preferences.getInt("next",1_000_000)
        check(next in 1_000_000 until Int.MAX_VALUE) { "Import notification ID limit reached." }
        check(preferences.edit().putInt("job:$job",next).putInt("next",next+1).commit()) {
            "Cannot save import scheduling identity."
        }
        next
    }
    companion object { private val lock=Any() }
}

/** Only the visible Activity / explicit Import or Resume action may create a UIDT job. */
@RequiresApi(34)
class ImportTransferScheduler(private val context: Context) {
    private val scheduler=context.getSystemService(JobScheduler::class.java).forNamespace(NAMESPACE)
    private val ids=ImportTransferIds(context)
    fun pending(job: String): Boolean = scheduler.getPendingJob(ids.id(job))!=null
    fun cancel(job: String) { scheduler.cancel(ids.id(job)) }
    fun schedule(job: String, unmetered: Boolean, items: List<ImportItem>, kind: String): Boolean {
        if(pending(job)) return true
        return scheduler.schedule(info(context,ids.id(job),job,unmetered,items,kind))==JobScheduler.RESULT_SUCCESS
    }
    companion object {
        const val NAMESPACE="mpa-import-transfers"
        fun info(context: Context, id: Int, job: String, unmetered: Boolean, items: List<ImportItem>, kind: String="TAKEOUT"): JobInfo {
            val network=NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                .apply { if(unmetered) addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) }.build()
            // Include downloaded Picker bytes as well as uploads. Unknown lengths stay unknown.
            val remaining=items.filter { it.selected && it.state!="COMPLETE" }
            val bytes=if(remaining.any { it.size<=0 }) JobInfo.NETWORK_BYTES_UNKNOWN.toLong() else
                remaining.fold(0L) { total,item -> Math.addExact(total,(item.size-item.offset).coerceAtLeast(0)) }
            return JobInfo.Builder(id,ComponentName(context,ImportTransferService::class.java))
                .setUserInitiated(true).setRequiredNetwork(network)
                .setEstimatedNetworkBytes(JobInfo.NETWORK_BYTES_UNKNOWN.toLong(),bytes)
                .setExtras(PersistableBundle().apply { putString("job",job); putString("kind",kind) }).build()
        }
        @get:androidx.annotation.ChecksSdkIntAtLeast(api=34)
        val supported get()=Build.VERSION.SDK_INT>=34
    }
}

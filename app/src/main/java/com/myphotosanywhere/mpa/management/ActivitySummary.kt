package com.myphotosanywhere.mpa.management

import com.myphotosanywhere.mpa.importing.ImportJob
import com.myphotosanywhere.mpa.importing.ImportHistory
import com.myphotosanywhere.mpa.drive.SyncState

/** Never infer completion or percentage from partially known background operations. */
internal class ActivitySummary(jobs: List<ImportJob>,sync: List<SyncState>) {
    val label: String?=when {
        jobs.any { it.state=="FAILED" || it.error!=null } || sync.any { it.error!=null } -> "Activity needs attention"
        jobs.any { it.state=="WAITING_FOR_APP" } -> "Import waiting to resume"
        jobs.any { it.state in listOf("RUNNING","DOWNLOADING") } -> "${jobs.count { it.state in listOf("RUNNING","DOWNLOADING") }} imports running${if(sync.any { it.running }) " · refreshing Drive" else ""}"
        sync.any { it.running } -> "Refreshing Drive library"
        jobs.any { it.state=="PAUSED" } -> "Import paused"
        jobs.any { !ImportHistory.canDismiss(it.state) } -> "Import ready to review"
        else -> null
    }
}

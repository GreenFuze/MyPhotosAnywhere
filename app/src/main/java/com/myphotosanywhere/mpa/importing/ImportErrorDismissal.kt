package com.myphotosanywhere.mpa.importing

import androidx.room.withTransaction
import com.myphotosanywhere.mpa.data.PhotoDatabase

/** Acknowledges old messages without deleting source files, history or resumable transfer state. */
class ImportErrorDismissal(private val database: PhotoDatabase) {
    suspend fun dismiss(id: String): Boolean = database.withTransaction {
        val dao=database.imports()
        val job=dao.job(id) ?: return@withTransaction false
        // Recheck under the transaction: a worker may have resumed since the button was displayed.
        if(!canDismiss(job)) return@withTransaction false
        dao.clearJobError(id)
        dao.clearItemErrors(id)
        dao.clearDownloadError(id)
        true
    }

    companion object {
        fun canDismiss(job: ImportJob): Boolean = job.state in listOf("PREVIEW","FAILED","PAUSED","CANCELLED","COMPLETE")
        fun hasErrors(job: ImportJob, items: List<ImportItem>, download: TakeoutDownload?): Boolean =
            job.error!=null || items.any { it.job==job.id && it.error!=null } ||
                (download!=null && download.job==job.id && download.error!=null)
    }
}

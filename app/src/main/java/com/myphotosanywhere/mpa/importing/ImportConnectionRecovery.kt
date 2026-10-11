package com.myphotosanywhere.mpa.importing

import com.myphotosanywhere.mpa.drive.DriveConnectionException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

/** Only the job's connection status changes; reserved IDs, staging and acknowledged offsets stay intact. */
class ImportConnectionRecovery(private val dao: ImportDao) {
    suspend fun record(id: String, attempt: Int, failure: DriveConnectionException): Boolean {
        currentCoroutineContext().ensureActive()
        val retry=attempt<MAX_RETRIES
        val message=failure.message + " Your ZIP and verified imports are preserved." + if(retry) " Retrying automatically (${attempt+1}/$MAX_RETRIES)."
            else " Automatic retries stopped. Restore the connection, then choose Import selected / Resume."
        // Pause/cancel/disconnect can race a network failure. Never put a stopped job back into RUNNING.
        val changed=dao.connectionState(id,if(retry) "RUNNING" else "FAILED",message)
        return changed==1 && retry
    }
    companion object { const val MAX_RETRIES=3 }
}

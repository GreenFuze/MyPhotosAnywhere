package com.myphotosanywhere.mpa.importing

/** A visible Activity can wake blocked work without changing any transfer checkpoints. */
class ImportForegroundRecovery(private val dao: ImportDao) {
    suspend fun record(id: String): Boolean = dao.connectionState(id,"WAITING_FOR_APP",
        "Android could not start background import. Reopen MPA to continue; transfer checkpoints are preserved.") == 1

    suspend fun resume(connected: suspend (String) -> Boolean, enqueue: suspend (String, Boolean) -> Unit) {
        for(job in dao.jobs()) {
            val legacy=job.state=="RUNNING" && job.error==LEGACY_MESSAGE
            if((job.state=="WAITING_FOR_APP" || legacy) && connected(job.binding) && dao.claimForegroundResume(job.id,LEGACY_MESSAGE)==1)
                enqueue(job.id,legacy)
        }
    }

    companion object {
        const val LEGACY_MESSAGE="Waiting for Android to allow background import. Open MPA to continue."
        fun isStartRefused(error: Throwable): Boolean {
            var cause: Throwable?=error
            repeat(8) {
                val current=cause ?: return false
                if(current.javaClass.simpleName=="ForegroundServiceStartNotAllowedException") return true
                cause=current.cause
            }
            return false
        }
    }
}

package com.myphotosanywhere.mpa.importing

import com.myphotosanywhere.mpa.drive.DriveConnectionException
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*

/** Both schedulers run the same durable engine. Connection backoff retains the active notification. */
class ImportExecution(private val dao: ImportDao,
    private val runOne: suspend () -> Boolean,
    private val publish: suspend (ImportProgress) -> Unit,
    private val wait: suspend (Long) -> Unit = { delay(it) }) {
    suspend fun snapshot(id: String): ImportProgress = ImportProgress.from(
        requireNotNull(dao.job(id)) { "Import missing." },dao.items(id))

    @OptIn(FlowPreview::class)
    suspend fun run(id: String): Boolean = coroutineScope {
        if(dao.job(id)?.state!="RUNNING") return@coroutineScope true
        dao.clearRunningError(id)
        publish(snapshot(id))
        val updates=launch {
            combine(dao.observeJob(id),dao.observeJobItems(id)) { job,items ->
                job?.let { ImportProgress.from(it,items) }
            }.filterNotNull().distinctUntilChanged().sample(1000).collect { publish(it) }
        }
        try {
            var attempts=0
            while(dao.job(id)?.state=="RUNNING") {
                currentCoroutineContext().ensureActive()
                try {
                    if(!runOne()) break
                    attempts=0
                } catch(e: DriveConnectionException) {
                    val retry=ImportConnectionRecovery(dao).record(id,attempts,e)
                    publish(snapshot(id))
                    if(!retry) break
                    wait(30_000L shl attempts)
                    attempts++
                    dao.clearRunningError(id)
                }
            }
            dao.job(id)?.state!="FAILED"
        } catch(e: CancellationException) { throw e }
        catch(_: Exception) {
            dao.connectionState(id,"FAILED","Background import stopped. Open MPA and resume; transfer checkpoints are preserved.")
            false
        } finally { updates.cancelAndJoin() }
    }
}

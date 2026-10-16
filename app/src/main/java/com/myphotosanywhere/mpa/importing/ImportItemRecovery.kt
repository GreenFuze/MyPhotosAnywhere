package com.myphotosanywhere.mpa.importing

import androidx.room.withTransaction
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.drive.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import java.io.IOException

/** External item data can fail validation without making another image unimportable. */
class ImportItemDataException(message: String,cause: Throwable?=null): IllegalArgumentException(message,cause)
class ImportDestinationUnavailable(message: String,cause: Throwable?=null): IOException(message,cause)

/** Convert only explicitly scoped external input/decoder failures, never database invariants. */
class ImportItemInput {
    suspend fun <T> read(description: String,action: suspend ()->T): T = try { action() }
    catch(error: CancellationException) { throw error }
    catch(error: ImportItemDataException) { throw error }
    catch(error: PhotosPickerRequestException) { throw error }
    catch(error: com.google.gson.JsonParseException) { throw invalid(description,error) }
    catch(error: IllegalArgumentException) { throw invalid(description,error) }
    catch(error: IllegalStateException) { throw invalid(description,error) }
    catch(error: ArithmeticException) { throw invalid(description,error) }
    catch(error: ClassCastException) { throw invalid(description,error) }
    catch(error: UnsupportedOperationException) { throw invalid(description,error) }
    private fun invalid(description: String,error: RuntimeException)=
        ImportItemDataException("$description: ${error.message ?: "unsupported or invalid data"}",error)
}

/** A failed image is not retried again in this pass; durable verified successes never rewind. */
class ImportItemRecovery(private val database: PhotoDatabase) {
    private val dao get()=database.imports()
    suspend fun attempt(jobId: String,itemId: String,action: suspend ()->Unit): Boolean {
        return try { action();true }
        catch(error: CancellationException) { throw error }
        catch(error: DriveConnectionException) { throw error }
        catch(error: DriveAuthorizationNeeded) { blocked(jobId,error) }
        catch(error: PhotosPickerAuthorizationNeeded) { blocked(jobId,error) }
        catch(error: com.google.android.gms.common.api.ApiException) {
            blocked(jobId,ImportDestinationUnavailable("Google account access is unavailable. Reconnect this import's account and retry.",error))
        }
        catch(error: PhotosPickerRequestException) {
            if(error.blocksJob) blocked(jobId,error) else failed(jobId,itemId,error)
        }
        catch(error: ImportDestinationUnavailable) { blocked(jobId,error) }
        catch(error: DriveHttpException) {
            when {
                error.retryable -> throw error
                error.status==401 -> blocked(jobId,error)
                else -> failed(jobId,itemId,error)
            }
        }
        catch(error: IOException) { failed(jobId,itemId,error) }
        catch(error: SecurityException) { failed(jobId,itemId,error) }
        catch(error: ImportItemDataException) { failed(jobId,itemId,error) }
    }
    private suspend fun blocked(jobId: String,error: Exception): Boolean {
        currentCoroutineContext().ensureActive()
        dao.connectionState(jobId,"FAILED",reason(error)+" Verified photos and transfer checkpoints are preserved. Resume after resolving this source or connection.")
        return false
    }
    private suspend fun failed(jobId: String,itemId: String,error: Exception): Boolean=database.withTransaction {
        currentCoroutineContext().ensureActive()
        val job=dao.job(jobId) ?: kotlin.error("Import job disappeared while recording an item failure.")
        if(job.state!="RUNNING") return@withTransaction false
        val item=dao.item(itemId) ?: kotlin.error("Import item disappeared while recording its failure.")
        check(item.job==jobId)
        if(item.state=="COMPLETE") {
            // A follow-up scheduler/cleanup failure must not cause an already verified re-upload.
            dao.connectionState(jobId,"FAILED","The photo was verified, but follow-up work needs attention. "+reason(error))
            false
        } else {
            check(item.state in setOf("PENDING","STAGING","UPLOADING","VERIFIED"))
            dao.updateItem(item.copy(state="FAILED",error=reason(error)))
            true
        }
    }
    suspend fun finish(jobId: String): Boolean=database.withTransaction {
        val failed=dao.selectedFailureCount(jobId)
        dao.connectionState(jobId,if(failed==0) "COMPLETE" else "FAILED",if(failed==0) null else
            "$failed selected ${if(failed==1) "file" else "files"} could not be imported. Other selected files were processed. Resume retries only failed or unfinished files.")==1 && failed==0
    }
    private fun reason(error: Exception)=error.message?.take(1000) ?: "This file could not be imported. Its source and checkpoints are preserved."
}

/** Retry is an exact-column transaction; successful and unselected records stay untouched. */
class ImportRetryPreparation(private val database: PhotoDatabase) {
    suspend fun prepare(id: String)=database.withTransaction {
        val dao=database.imports();val job=dao.job(id) ?: throw IOException("Saved import is unavailable.")
        require(job.state !in setOf("PICKING","DOWNLOADING","CANCELLED","COMPLETE")) { "This import cannot be resumed in its current state." }
        require(database.drive().source(job.binding)!=null) { "Reconnect this import's original Drive source." }
        dao.retrySelectedFailures(id)
        dao.saveJob(job.copy(state="RUNNING",error=null))
    }
}

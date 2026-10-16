package com.myphotosanywhere.mpa.importing

import androidx.room.withTransaction
import com.google.gson.JsonParseException
import com.myphotosanywhere.mpa.data.PhotoDatabase
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import java.io.IOException

data class MetadataRepairFailure(val itemId: String,val name: String,val reason: String)
data class MetadataRepairResult(val applied: Int,val unchanged: Int,val failed: Int,
    val failures: List<MetadataRepairFailure>)

/** Reapply retained provider annotations, never re-read, re-upload or retarget original media. */
class TakeoutMetadataRepair(private val database: PhotoDatabase) {
    private val codec=TakeoutCodec()

    suspend fun repair(jobId: String): MetadataRepairResult {
        val job=database.imports().job(jobId) ?: throw IOException("Saved import is unavailable.")
        if(job.kind!="TAKEOUT" || job.state in ACTIVE) throw IOException("Pause the Takeout import before repairing metadata.")
        var after="";var applied=0;var unchanged=0;var failed=0
        val failures=mutableListOf<MetadataRepairFailure>()
        while(true) {
            currentCoroutineContext().ensureActive()
            val page=database.imports().metadataRepairPage(jobId,after,60)
            if(page.isEmpty()) break
            for(item in page) {
                currentCoroutineContext().ensureActive()
                try { if(apply(job,item)) applied++ else unchanged++ }
                catch(error: IOException) {
                    failed++
                    if(failures.size<20) failures+=MetadataRepairFailure(item.id,item.name,
                        error.message ?: "Metadata could not be applied; the saved record is preserved.")
                }
            }
            after=page.last().id
        }
        return MetadataRepairResult(applied,unchanged,failed,failures)
    }

    /** Each transaction reads current values; an old screen snapshot cannot overwrite an edit. */
    private suspend fun apply(job: ImportJob,expected: ImportItem): Boolean=database.withTransaction {
        val latestJob=database.imports().job(job.id) ?: throw IOException("Saved import is unavailable.")
        if(latestJob.binding!=job.binding || latestJob.state in ACTIVE) throw IOException("Import changed. Pause it and retry metadata repair.")
        val item=database.imports().item(expected.id) ?: throw IOException("Saved import item is unavailable.")
        if(item!=expected || item.job!=job.id || item.state!="COMPLETE") throw IOException("Import item changed. Retry metadata repair.")
        val photo=item.photoId?.let { database.photos().find(it) } ?: throw IOException("Imported photo is unavailable.")
        if(item.sha256==null || !item.sha256.matches(Regex("[0-9a-f]{64}")) || item.size<=0 ||
            photo.sha256!=item.sha256 || photo.bytes!=item.size || photo.sourceBindingKey!=job.binding ||
            item.remoteId.isNullOrBlank() || photo.sourceRemoteId!=item.remoteId)
            throw IOException("Saved byte identity or Drive source no longer matches. No photo was changed.")
        val raw=item.metadata ?: throw IOException("No saved Takeout sidecar. Add its matching ZIP part first.")
        val repaired=try { codec.apply(photo,codec.parse(raw)) }
        catch(error: JsonParseException) { throw IOException("Invalid saved Takeout JSON; the source record is preserved.",error) }
        catch(error: IllegalArgumentException) { throw IOException("Takeout metadata conflicts or is invalid; existing annotations and the source record are preserved.",error) }
        catch(error: IllegalStateException) { throw IOException("Invalid saved Takeout JSON structure; the source record is preserved.",error) }
        catch(error: ArithmeticException) { throw IOException("Invalid Takeout timestamp; the source record is preserved.",error) }
        if(repaired==photo) false else { database.photos().update(repaired);true }
    }

    companion object { private val ACTIVE=setOf("RUNNING","DOWNLOADING","WAITING_FOR_APP","PICKING") }
}

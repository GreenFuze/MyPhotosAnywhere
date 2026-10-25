package com.myphotosanywhere.mpa.processor.client

import com.google.gson.JsonArray
import com.google.gson.JsonObject
import com.myphotosanywhere.mpa.drive.DriveWorkspaceChange
import com.myphotosanywhere.mpa.drive.DriveWorkspaceChangesPage
import com.myphotosanywhere.mpa.drive.DriveWorkspaceCursor
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
import java.io.IOException
import java.util.Collections
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

internal class ProcessorWorkspaceHistoryRecovery : IOException("Drive history contains a removed, moved or unavailable metadata identity. Its unresolved recovery and saved library were preserved.")
internal interface ProcessorWorkspaceHistorySource {
    suspend fun changes(cursor: DriveWorkspaceCursor): DriveWorkspaceChangesPage
}
internal data class ProcessorWorkspaceHistorySummary(val kind: String,val id: String,val recoveryRequired: Boolean,val occurrences: Long,
    val firstSha256: String,val lastSha256: String,val chainSha256: String) {
    val key: String get() = ProcessorWorkspaceObservations.hash((kind + ":" + id).toByteArray(Charsets.UTF_8))
}
internal data class ProcessorWorkspaceReplayState(val owner: UUID,val start: DriveWorkspaceCursor,val cursor: DriveWorkspaceCursor,
    val totalPages: Long,val batchPages: Int,val active: Boolean,val atEnd: Boolean,val inventoryChanged: Boolean,val fenced: Boolean = false)
internal data class ProcessorWorkspaceReplayBatch(val attempt: ProcessorWorkspaceAttempt,val state: ProcessorWorkspaceReplayState)
internal data class ProcessorWorkspaceHistoryEvent(val kind: String,val id: String,val recoveryRequired: Boolean,val sha256: String) {
    init {
        com.myphotosanywhere.mpa.drive.DriveWorkspaceMetadata.opaque(id)
        if(kind !in setOf("file","drive") || !sha256.matches(Regex("[0-9a-f]{64}"))) throw ProcessorWorkspaceConflict()
    }
    val key: String get() = ProcessorWorkspaceObservations.hash((kind + ":" + id).toByteArray(Charsets.UTF_8))
}
internal class ProcessorWorkspaceHistoryEvidence(val from: DriveWorkspaceCursor,val next: DriveWorkspaceCursor,val atEnd: Boolean,
    events: List<ProcessorWorkspaceHistoryEvent>,candidates: List<ProcessorWorkspaceCandidate>,val inventoryChanged: Boolean) {
    val events: List<ProcessorWorkspaceHistoryEvent> = Collections.unmodifiableList(events.toList())
    val candidates: List<ProcessorWorkspaceCandidate> = Collections.unmodifiableList(candidates.toList())
    init { if(this.events.size > 100 || this.candidates.size > 100 || this.candidates.map { it.fileId }.distinct().size != this.candidates.size || !from.sameScope(next)) throw ProcessorWorkspaceConflict() }
}

/** Actual Google history replay. A 128-page limit yields a resumable pending batch,
 * never discards an older interval. Every relevant observation and candidate commits
 * atomically BEFORE another request or operational failure. This is not authority or
 * catalog activation; graph, signing policy and final unchanged admission remain required. */
internal class ProcessorWorkspaceHistory(private val observations: ProcessorWorkspaceObservations,private val source: ProcessorWorkspaceHistorySource) {
    suspend fun replay(prepared: ProcessorWorkspacePrepared): ProcessorWorkspaceReplayBatch = withContext(Dispatchers.IO) {
        var batch = observations.startReplay(prepared.attempt)
        if(prepared.reads != observations.reads(batch.attempt)) throw ProcessorWorkspaceConflict()
        val present = prepared.reads.associate { it.candidate.fileId to it.candidate }
        val workspace = observations.workspaceFolder(batch.attempt)
        val visited = hashSetOf(batch.state.cursor.token)
        repeat(128) {
            currentCoroutineContext().ensureActive()
            val page = source.changes(batch.state.cursor)
            val evidence = evidence(workspace,present,batch.state.cursor,page,false)
            batch = observations.rememberHistoryPage(batch,evidence,false)
            currentCoroutineContext().ensureActive()
            if(evidence.events.any { it.recoveryRequired }) throw ProcessorWorkspaceHistoryRecovery()
            if(evidence.inventoryChanged) throw ProcessorWorkspaceConflict()
            if(page.atEnd != null) return@withContext batch
            if(!visited.add(batch.state.cursor.token)) throw ProcessorWorkspaceConflict()
        }
        observations.pauseReplay(batch)
    }
    /** A rejected final fence records the actual page without advancing its cursor.
     * An unchanged result is only evidence for a later atomic authority/graph admission. */
    suspend fun checkUnchanged(batch: ProcessorWorkspaceReplayBatch,reads: List<ProcessorWorkspaceRead>): ProcessorWorkspaceReplayBatch = withContext(Dispatchers.IO) {
        if(reads != observations.replayedContext(batch).reads) throw ProcessorWorkspaceConflict()
        val workspace = observations.workspaceFolder(batch.attempt)
        val evidence = evidence(workspace,reads.associate { it.candidate.fileId to it.candidate },batch.state.cursor,source.changes(batch.state.cursor),true)
        if(evidence.events.isNotEmpty() || evidence.inventoryChanged || !evidence.atEnd) {
            observations.rememberHistoryPage(batch,evidence,true)
            throw ProcessorWorkspaceConflict()
        }
        observations.rememberUnchanged(batch,evidence)
    }
    private fun evidence(folder: String,present: Map<String,ProcessorWorkspaceCandidate>,from: DriveWorkspaceCursor,page: DriveWorkspaceChangesPage,folders: Boolean): ProcessorWorkspaceHistoryEvidence {
        val next = page.next ?: checkNotNull(page.atEnd)
        if(!next.sameScope(from) || page.next?.token == from.token) throw ProcessorWorkspaceConflict()
        val events = arrayListOf<ProcessorWorkspaceHistoryEvent>();val candidates = linkedMapOf<String,ProcessorWorkspaceCandidate>();var changed = false
        for(item in page.changes) {
            val file = item.file
            val relevant = item.kind == "drive" && (from.sharedDriveId == null || item.id == from.sharedDriveId) || item.kind == "file" &&
                (item.removed || file == null || item.id == folder || item.id in present || folder in file.parents)
            if(!relevant) {
                if(folders && file?.mimeType == "application/vnd.google-apps.folder") {
                    events.add(ProcessorWorkspaceHistoryEvent(item.kind,item.id,false,digest(item)));changed = true
                }
                continue
            }
            var recovery = item.kind == "drive" || item.id == folder || item.removed || file == null || file.trashed ||
                folder !in file.parents || file.downloadable == false || file.bytes == null || file.bytes < 1 || file.mimeType !in ProcessorWorkspaceCandidate.MIME
            if(!recovery) {
                val metadata = checkNotNull(file)
                var candidate = ProcessorWorkspaceCandidate(item.id,metadata.version,metadata.bytes,metadata.mimeType)
                recovery = !candidate.readable
                val actual = present[item.id]
                if(!recovery && actual != null) {
                    recovery = candidate.bytes != actual.bytes || candidate.mimeType != actual.mimeType
                    if(!recovery) { changed = changed || candidate.version > actual.version;if(candidate.version < actual.version) candidate = actual }
                } else if(!recovery) changed = true
                if(!recovery && (candidates[item.id]?.version ?: 0) < candidate.version) candidates[item.id] = candidate
            }
            events.add(ProcessorWorkspaceHistoryEvent(item.kind,item.id,recovery,digest(item)))
        }
        return ProcessorWorkspaceHistoryEvidence(from,next,page.atEnd != null,events,candidates.values.toList(),changed)
    }
    private fun digest(value: DriveWorkspaceChange): String {
        val root = JsonObject().apply {
            addProperty("kind",value.kind);addProperty("id",value.id);addProperty("removed",value.removed)
            add("file",value.file?.let { file -> JsonObject().apply {
                addProperty("id",file.id);addProperty("mime",file.mimeType);addProperty("version",file.version.toString());addProperty("bytes",file.bytes?.toString())
                add("parents",JsonArray().apply { file.parents.forEach { add(it) } });addProperty("trashed",file.trashed)
                addProperty("sha256",file.sha256);addProperty("downloadable",file.downloadable);addProperty("drive",file.sharedDriveId)
            } } ?: com.google.gson.JsonNull.INSTANCE)
        }
        return ProcessorWorkspaceObservations.hash(CanonicalProtocolJson(8192).encode(root.toString().toByteArray(Charsets.UTF_8)))
    }
}

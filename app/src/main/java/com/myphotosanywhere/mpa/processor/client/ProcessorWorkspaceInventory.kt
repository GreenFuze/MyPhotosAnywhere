package com.myphotosanywhere.mpa.processor.client

import com.myphotosanywhere.mpa.drive.DriveWorkspaceCursor
import com.myphotosanywhere.mpa.drive.DriveWorkspaceMetadata
import com.myphotosanywhere.mpa.drive.DriveWorkspacePage
import java.io.IOException
import java.util.Collections
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

internal interface ProcessorWorkspaceInventorySource {
    val connectionSha256: String
    suspend fun cursor(): DriveWorkspaceCursor
    suspend fun page(token: String? = null): DriveWorkspacePage
    suspend fun readCaptured(expected: DriveWorkspaceMetadata): ByteArray
}
internal interface ProcessorWorkspaceCapturedSource : ProcessorWorkspaceInventorySource {
    suspend fun capture(fileId: String): DriveWorkspaceMetadata
}
/** Listed/read evidence awaiting change replay and full graph/authority admission.
 * It is always pending; no UI may interpret this value as a ready/activated library. */
internal class ProcessorWorkspacePrepared(val attempt: ProcessorWorkspaceAttempt,reads: List<ProcessorWorkspaceRead>) {
    val reads: List<ProcessorWorkspaceRead> = Collections.unmodifiableList(reads.toList())
}

/** Joins all borrowed Google reads. Even cancellation/verification failure leaves a
 * durable incomplete attempt and every listed identity. A successor fences the actual
 * previous attempt; an old read cannot commit or invoke verification after replacement. */
internal class ProcessorWorkspaceInventory(private val observations: ProcessorWorkspaceObservations,private val source: ProcessorWorkspaceInventorySource) {
    suspend fun prepare(verifyAndStage: suspend (DriveWorkspaceMetadata,ByteArray)->Unit): ProcessorWorkspacePrepared = withContext(Dispatchers.IO) {
        var attempt = observations.begin(source.connectionSha256) // This MUST succeed before the first network call.
        attempt = observations.start(attempt,source.cursor())
        val captured = linkedMapOf<String,DriveWorkspaceMetadata>()
        val allIds = hashSetOf<String>(); val tokens = hashSetOf<String>();var token: String? = null;var pages = 0
        do {
            currentCoroutineContext().ensureActive()
            if (++pages > 128) throw ProcessorWorkspaceLimit()
            val page = source.page(token)
            if (page.incompleteSearch) throw IOException("Drive returned an incomplete workspace inventory.")
            val candidates = ArrayList<ProcessorWorkspaceCandidate>()
            for (metadata in page.files) {
                if (!allIds.add(metadata.id)) throw ProcessorWorkspaceConflict()
                if (metadata.mimeType !in ProcessorWorkspaceCandidate.MIME) continue
                if (metadata.trashed) throw ProcessorWorkspaceConflict()
                val candidate = ProcessorWorkspaceCandidate(metadata.id,metadata.version,metadata.bytes,metadata.mimeType)
                captured[metadata.id] = metadata; candidates.add(candidate)
            }
            // Persist the entire page before reading any object, including a later bad one.
            attempt = observations.rememberCandidates(attempt,candidates)
            token = page.nextPage
            if (token != null && !tokens.add(token)) throw ProcessorWorkspaceConflict()
        } while (token != null)
        val candidates = captured.values.map { ProcessorWorkspaceCandidate(it.id,it.version,it.bytes,it.mimeType) }
        attempt = observations.sealInventory(attempt,candidates)
        for (metadata in captured.values) {
            currentCoroutineContext().ensureActive()
            val candidate = ProcessorWorkspaceCandidate(metadata.id,metadata.version,metadata.bytes,metadata.mimeType)
            if (!candidate.readable) throw IOException("Metadata is not ready for a bounded read. Its observed identity was retained for recovery.")
            val bytes = source.readCaptured(metadata)
            // Preserve the actual read identity even if its subsequent semantic verification fails.
            attempt = observations.rememberRead(attempt,candidate,bytes)
            currentCoroutineContext().ensureActive();verifyAndStage(metadata,bytes);currentCoroutineContext().ensureActive()
        }
        ProcessorWorkspacePrepared(attempt,observations.reads(attempt))
    }
}

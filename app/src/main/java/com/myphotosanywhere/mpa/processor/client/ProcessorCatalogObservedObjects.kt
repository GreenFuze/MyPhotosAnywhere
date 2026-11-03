package com.myphotosanywhere.mpa.processor.client

import com.myphotosanywhere.mpa.processor.protocol.CatalogObjectReference
import com.myphotosanywhere.mpa.processor.protocol.CatalogObjectSource
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import java.util.Collections
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

/** Retained metadata read identities for staging, never current-policy authority. */
internal class ProcessorCatalogReadContext(val batch: ProcessorWorkspaceReplayBatch,val workspace: CatalogWorkspace,reads: List<ProcessorWorkspaceRead>) {
    val reads: List<ProcessorWorkspaceRead> = Collections.unmodifiableList(reads.toList())
}

/** Binds ancestry/covered-facts reads to one durable listed/read/replayed attempt.
 * No unknown identity, same-content replacement, changed version or retired owner may
 * return bytes to a semantic consumer. Actual Google I/O remains borrowed and joined;
 * originals, activation and credentials are not owned or mutated by this adapter. */
internal class ProcessorCatalogObservedObjects private constructor(private val observations: ProcessorWorkspaceObservations,
    private val source: ProcessorWorkspaceCapturedSource,private val context: ProcessorCatalogReadContext) : CatalogObjectSource {
    private val byId = context.reads.associateBy { it.candidate.fileId }
    override suspend fun read(reference: CatalogObjectReference): ByteArray = withContext(Dispatchers.IO) {
        currentCoroutineContext().ensureActive()
        val expected = byId[reference.fileId] ?: throw ProcessorWorkspaceConflict()
        if(source.connectionSha256 != context.batch.attempt.connectionSha256 || reference.accountId != context.workspace.accountId ||
            reference.workspaceId != context.workspace.folderId || reference.bytes != expected.candidate.bytes || reference.sha256 != expected.sha256) throw ProcessorWorkspaceConflict()
        observations.assertCatalogRead(context,expected)
        val metadata = source.capture(reference.fileId)
        currentCoroutineContext().ensureActive()
        if(metadata.id != expected.candidate.fileId || metadata.version != expected.candidate.version || metadata.bytes != expected.candidate.bytes || metadata.mimeType != expected.candidate.mimeType ||
            metadata.trashed || metadata.downloadable != true || context.workspace.folderId !in metadata.parents || metadata.sharedDriveId != context.batch.state.cursor.sharedDriveId ||
            metadata.sha256 != null && metadata.sha256 != expected.sha256 || source.connectionSha256 != context.batch.attempt.connectionSha256) throw ProcessorWorkspaceConflict()
        observations.assertCatalogRead(context,expected)
        val borrowed = source.readCaptured(metadata)
        currentCoroutineContext().ensureActive()
        if(borrowed.size.toLong() != expected.candidate.bytes || source.connectionSha256 != context.batch.attempt.connectionSha256) throw ProcessorWorkspaceConflict()
        val owned = borrowed.copyOf()
        // The existing read is checked against actual bytes before another consumer
        // sees them; an equal read must not silently change this retained attempt.
        if(observations.rememberRead(context.batch.attempt,expected.candidate,owned) != context.batch.attempt) throw ProcessorWorkspaceConflict()
        observations.assertCatalogRead(context,expected)
        currentCoroutineContext().ensureActive();owned
    }
    companion object {
        suspend fun create(observations: ProcessorWorkspaceObservations,source: ProcessorWorkspaceCapturedSource,batch: ProcessorWorkspaceReplayBatch): ProcessorCatalogObservedObjects {
            if(source.connectionSha256 != batch.attempt.connectionSha256) throw ProcessorWorkspaceConflict()
            val context = observations.replayedContext(batch)
            if(source.connectionSha256 != batch.attempt.connectionSha256) throw ProcessorWorkspaceConflict()
            return ProcessorCatalogObservedObjects(observations,source,context)
        }
    }
}

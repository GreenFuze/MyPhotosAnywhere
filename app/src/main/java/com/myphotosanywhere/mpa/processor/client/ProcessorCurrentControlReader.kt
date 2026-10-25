package com.myphotosanywhere.mpa.processor.client

import com.myphotosanywhere.mpa.drive.DriveWorkspaceMetadata
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import com.myphotosanywhere.mpa.processor.protocol.LibraryCurrentControl
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentReader
import java.security.MessageDigest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

/** An exact captured root head, still PENDING. This never grants activation or writes. */
internal class ProcessorControlCapture internal constructor(internal val owner: Any,internal val attempt: ProcessorControlAttempt,
    internal val metadata: DriveWorkspaceMetadata,val head: LibraryCurrentControl)

/** One serial borrowed-I/O owner. Close first denies admission, then joins actual I/O
 * even when a provider ignores cancellation. The composition root must close this
 * reader before its borrowed Google/source/state owners. No task or retry is detached.
 * A fenced capture cannot start another read. Future graph readers must participate
 * in this same lifetime before fenced evidence can be used for atomic activation. */
internal class ProcessorCurrentControlReader(private val observations: ProcessorCurrentControlObservations,
    private val source: ProcessorWorkspaceCapturedSource,private val pin: LibraryConnectionDescriptor) {
    private val serial = Mutex()
    private val identity = Any()
    @Volatile private var closed = false

    suspend fun capture(): ProcessorControlCapture = operation {
        val attempt = observations.begin(pin.documentSha256)
        checkRunning()
        val fileId = pin.currentFileId ?: throw ProcessorWorkspaceConflict()
        val metadata = source.capture(fileId)
        checkRunning();validate(metadata)
        val reading = observations.rememberCapture(attempt,metadata)
        checkRunning()
        val borrowed = source.readCaptured(metadata)
        checkRunning()
        if (borrowed.size.toLong() != metadata.bytes) throw ProcessorWorkspaceConflict()
        val owned = borrowed.copyOf()
        val read = observations.rememberBytes(reading,owned)
        checkRunning()
        val (captured,head) = observations.observe(read,metadata,owned)
        checkRunning();ProcessorControlCapture(identity,captured,metadata,head)
    }

    suspend fun assertCaptured(capture: ProcessorControlCapture) = operation {
        if (capture.owner !== identity) throw ProcessorWorkspaceConflict()
        observations.assertCaptured(capture.attempt)
    }

    suspend fun fence(capture: ProcessorControlCapture): ProcessorControlAttempt = operation {
        if (capture.owner !== identity) throw ProcessorWorkspaceConflict()
        observations.assertCaptured(capture.attempt)
        checkRunning()
        val metadata = source.capture(checkNotNull(pin.currentFileId))
        checkRunning()
        if (!metadata.sameRevision(capture.metadata)) {
            observations.rejectFence(capture.attempt,metadata)
            throw ProcessorWorkspaceConflict()
        }
        validate(metadata)
        observations.assertCaptured(capture.attempt)
        val borrowed = source.readCaptured(metadata)
        checkRunning()
        if (borrowed.size.toLong() != metadata.bytes || hash(borrowed.copyOf()) != capture.head.envelopeSha256) {
            observations.rejectFence(capture.attempt,metadata)
            throw ProcessorWorkspaceConflict()
        }
        observations.fence(capture.attempt).also { checkRunning() }
    }

    suspend fun close() {
        closed = true
        withContext(NonCancellable + Dispatchers.IO) { serial.withLock { } }
    }

    private suspend fun <T> operation(action: suspend ()->T): T = withContext(Dispatchers.IO) {
        serial.withLock { checkRunning();action() }
    }
    private suspend fun checkRunning() {
        currentCoroutineContext().ensureActive()
        check(!closed) { "Current-control read owner is closed." }
        if (source.connectionSha256 != pin.documentSha256) throw ProcessorWorkspaceConflict()
    }
    private fun validate(metadata: DriveWorkspaceMetadata) {
        if (metadata.id != pin.currentFileId || metadata.mimeType != "application/json" || metadata.trashed || metadata.downloadable != true ||
            pin.workspace.folderId !in metadata.parents || metadata.bytes == null || metadata.bytes !in 1..ProtocolDocumentReader.MAX_DOCUMENT_BYTES.toLong())
            throw ProcessorWorkspaceConflict()
    }
    private fun hash(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it.toInt() and 255) }
}

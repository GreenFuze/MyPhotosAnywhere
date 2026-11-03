package com.myphotosanywhere.mpa.processor.client

import com.myphotosanywhere.mpa.drive.DriveWorkspaceMetadata
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import com.myphotosanywhere.mpa.processor.protocol.LibraryCurrentControl
import com.myphotosanywhere.mpa.processor.protocol.CatalogObjectReference
import com.myphotosanywhere.mpa.processor.protocol.CatalogPublisherAuthorization
import com.myphotosanywhere.mpa.processor.protocol.CatalogSourceConfiguration
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

/** Exact selected policy bytes/semantics, still PENDING. Final control fencing and
 * current-state/complete-graph atomic admission remain mandatory before activation. */
internal class ProcessorControlPolicies private constructor(val capture: ProcessorControlCapture,
    val publishers: CatalogPublisherAuthorization,val sources: CatalogSourceConfiguration,
    private val publisherBody: ByteArray,private val sourceBody: ByteArray) {
    fun publisherEnvelope(): ByteArray = publisherBody.copyOf()
    fun sourceEnvelope(): ByteArray = sourceBody.copyOf()
    companion object {
        fun verify(capture: ProcessorControlCapture,pin: LibraryConnectionDescriptor,publisher: ByteArray,source: ByteArray): ProcessorControlPolicies {
            val ownedPublisher=publisher.copyOf();val ownedSource=source.copyOf()
            return ProcessorControlPolicies(capture,capture.head.readPublishers(ownedPublisher,pin),capture.head.readSources(ownedSource,pin),ownedPublisher,ownedSource)
        }
    }
}

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
        requireCapture(capture)
    }

    suspend fun readSelectedPolicies(capture: ProcessorControlCapture): ProcessorControlPolicies = operation {
        requireCapture(capture)
        val publishers=readPolicy(capture,capture.head.publishers.reference)
        // Deny an invalid/revoked catalog selection before another policy request.
        capture.head.readPublishers(publishers,pin)
        val sources=readPolicy(capture,capture.head.sources.reference)
        val selected=ProcessorControlPolicies.verify(capture,pin,publishers,sources)
        checkRunning();requireCapture(capture);selected
    }

    suspend fun fence(capture: ProcessorControlCapture): ProcessorControlAttempt = operation {
        requireCapture(capture)
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
    private suspend fun requireCapture(capture: ProcessorControlCapture) {
        if (capture.owner !== identity || capture.head.connectionSha256 != pin.documentSha256 || capture.head.sequence != capture.attempt.sequence ||
            capture.head.envelopeSha256 != capture.attempt.contentSha256 || capture.metadata.id != pin.currentFileId ||
            capture.metadata.version != capture.attempt.version || capture.metadata.sharedDriveId != capture.attempt.driveId)
            throw ProcessorWorkspaceConflict()
        observations.assertCaptured(capture.attempt)
    }
    private suspend fun readPolicy(capture: ProcessorControlCapture,reference: CatalogObjectReference): ByteArray {
        requireCapture(capture);checkRunning()
        if (reference.accountId != pin.workspace.accountId || reference.workspaceId != pin.workspace.folderId || reference.fileId == pin.currentFileId)
            throw ProcessorWorkspaceConflict()
        val metadata=source.capture(reference.fileId)
        checkRunning();requireCapture(capture)
        if (metadata.id != reference.fileId || metadata.version < 1 || metadata.bytes != reference.bytes || metadata.mimeType != "application/json" ||
            metadata.trashed || metadata.downloadable != true || pin.workspace.folderId !in metadata.parents || metadata.sharedDriveId != capture.metadata.sharedDriveId ||
            metadata.sha256 != null && metadata.sha256 != reference.sha256) throw ProcessorWorkspaceConflict()
        val borrowed=source.readCaptured(metadata)
        checkRunning();requireCapture(capture)
        if (borrowed.size.toLong() != reference.bytes) throw ProcessorWorkspaceConflict()
        val owned=borrowed.copyOf()
        if (hash(owned) != reference.sha256) throw ProcessorWorkspaceConflict()
        return owned
    }
    private fun validate(metadata: DriveWorkspaceMetadata) {
        if (metadata.id != pin.currentFileId || metadata.mimeType != "application/json" || metadata.trashed || metadata.downloadable != true ||
            pin.workspace.folderId !in metadata.parents || metadata.bytes == null || metadata.bytes !in 1..ProtocolDocumentReader.MAX_DOCUMENT_BYTES.toLong())
            throw ProcessorWorkspaceConflict()
    }
    private fun hash(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it.toInt() and 255) }
}

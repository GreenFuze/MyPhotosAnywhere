package com.myphotosanywhere.mpa.processor.client

import com.myphotosanywhere.mpa.drive.DriveGateway
import com.myphotosanywhere.mpa.drive.DriveWorkspaceCursor
import com.myphotosanywhere.mpa.drive.DriveWorkspaceChangesPage
import com.myphotosanywhere.mpa.drive.DriveWorkspaceMetadata
import com.myphotosanywhere.mpa.drive.DriveWorkspacePage
import com.myphotosanywhere.mpa.processor.protocol.CatalogObjectReference
import com.myphotosanywhere.mpa.processor.protocol.CatalogObjectSource
import com.myphotosanywhere.mpa.processor.protocol.CatalogPublicationReader
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import java.io.IOException
import java.security.MessageDigest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

/** Borrows a captured Google-only gateway and independently approved pin. These reads
 * are transport evidence, NOT a completed observation fence, controller acceptance or
 * catalog activation. The caller must persist each listed candidate before reading it.
 * No original-file mutation, local media copy or desktop/LAN endpoint is created. */
internal class ProcessorLibraryDriveObjects(private val gateway: DriveGateway,private val pin: LibraryConnectionDescriptor) : CatalogObjectSource, ProcessorWorkspaceCapturedSource, ProcessorWorkspaceHistorySource {
    private val scopeLock = Mutex()
    private var boundScope: Pair<String,String?>? = null
    override val connectionSha256: String get() = pin.documentSha256
    override suspend fun cursor(): DriveWorkspaceCursor {
        account();val folder = workspace()
        val value = gateway.catalogWorkspaceCursor(folder.sharedDriveId)
        account();workspace()
        return DriveWorkspaceCursor(pin.workspace.accountId,folder.sharedDriveId,value)
    }
    override suspend fun changes(cursor: DriveWorkspaceCursor): DriveWorkspaceChangesPage {
        account();val folder = workspace()
        if (cursor.accountId != pin.workspace.accountId || cursor.sharedDriveId != folder.sharedDriveId)
            throw IOException("Drive history cursor belongs to another account or corpus.")
        val result = gateway.catalogWorkspaceChanges(cursor)
        account();workspace()
        return result
    }
    override suspend fun page(token: String?): DriveWorkspacePage {
        account(); workspace()
        val result = gateway.catalogWorkspacePage(pin.workspace.folderId,token)
        account()
        if (result.incompleteSearch || result.files.any { it.trashed || pin.workspace.folderId !in it.parents || it.id == pin.workspace.folderId })
            throw IOException("Drive did not return a complete metadata workspace page. Previous library content was kept.")
        return result
    }

    override suspend fun capture(fileId: String): DriveWorkspaceMetadata {
        DriveWorkspaceMetadata.opaque(fileId)
        if (fileId == pin.workspace.folderId) throw IOException("A catalog object cannot alias its workspace.")
        account(); workspace()
        val metadata = gateway.catalogWorkspaceFile(fileId)
        if (metadata.id != fileId || metadata.trashed || pin.workspace.folderId !in metadata.parents)
            throw IOException("The catalog object left its approved metadata workspace.")
        account(); return metadata
    }

    override suspend fun readCaptured(expected: DriveWorkspaceMetadata): ByteArray {
        val before = capture(expected.id)
        if (!before.sameRevision(expected)) throw IOException("The captured metadata object changed before its read.")
        return readOwned(before)
    }

    override suspend fun read(reference: CatalogObjectReference): ByteArray {
        if (reference.accountId != pin.workspace.accountId || reference.workspaceId != pin.workspace.folderId ||
            reference.bytes !in 1..CatalogPublicationReader.MAX_COMPRESSED_BYTES.toLong() || !reference.sha256.matches(Regex("[0-9a-f]{64}")))
            throw IOException("Catalog reference is outside the approved metadata workspace or byte bound.")
        val before = capture(reference.fileId)
        if (before.bytes != reference.bytes || before.sha256 != null && before.sha256 != reference.sha256)
            throw IOException("Catalog reference changed its captured immutable identity.")
        return readOwned(before).also { if (hash(it) != reference.sha256) throw IOException("Catalog object differs from its immutable SHA-256 reference.") }
    }

    private suspend fun readOwned(before: DriveWorkspaceMetadata): ByteArray {
        val bytes = before.bytes
        val maximum = if (before.mimeType == "application/json") CatalogPublicationReader.MAX_INDEX_BYTES else CatalogPublicationReader.MAX_COMPRESSED_BYTES
        if (before.downloadable != true || before.mimeType !in METADATA_MIME || bytes == null || bytes !in 1..maximum.toLong())
            throw IOException("This is not a bounded MPA metadata object. Original media was not read.")
        val result = gateway.catalogWorkspaceBytes(before.id,bytes)
        currentCoroutineContext().ensureActive()
        val after = capture(before.id)
        if (!before.sameRevision(after) || before.sha256 != null && hash(result) != before.sha256)
            throw IOException("Metadata changed during its read. Previous library content was kept.")
        currentCoroutineContext().ensureActive(); return result
    }
    private suspend fun account() {
        val actual = gateway.catalogWorkspaceAccount()
        if (actual.id != pin.workspace.accountId) throw IOException("The Google connection no longer matches the approved library account.")
        currentCoroutineContext().ensureActive()
    }
    private suspend fun workspace(): DriveWorkspaceMetadata {
        val actual = gateway.catalogWorkspaceFile(pin.workspace.folderId)
        if (actual.id != pin.workspace.folderId || actual.mimeType != DriveGateway.FOLDER_MIME || actual.trashed)
            throw IOException("The approved metadata workspace is unavailable.")
        scopeLock.withLock {
            val observed = pin.workspace.accountId to actual.sharedDriveId
            if (boundScope == null) boundScope = observed
            else if (boundScope != observed) throw IOException("The metadata workspace changed its captured Drive corpus.")
        }
        return actual
    }
    private companion object {
        val METADATA_MIME = setOf("application/json","application/octet-stream","application/gzip","application/x-gzip")
        suspend fun hash(bytes: ByteArray): String = withContext(Dispatchers.Default) {
            MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it.toInt() and 255) }
        }
    }
}

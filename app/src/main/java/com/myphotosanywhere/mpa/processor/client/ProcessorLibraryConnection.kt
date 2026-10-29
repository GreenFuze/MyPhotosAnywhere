package com.myphotosanywhere.mpa.processor.client

import com.myphotosanywhere.mpa.drive.DriveGateway
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import java.io.IOException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

internal data class ProcessorLibraryAccount(val id: String, val email: String) {
    init {
        if (!ACCOUNT_ID.matches(id)) throw IOException("Google returned an invalid account identity. Choose your account again.")
        validateEmail(email)
    }
    companion object {
        private val ACCOUNT_ID = Regex("[A-Za-z0-9_-]{1,256}")
        fun validateEmail(email: String) {
            if (email.isBlank() || email.length > 320 || email.any { it.isWhitespace() || it.isISOControl() } || !Charsets.UTF_8.newEncoder().canEncode(email))
                throw IOException("Google returned an invalid account email. Choose your account again.")
        }
    }
}
internal data class ProcessorLibraryWorkspace(val id: String, val name: String, val version: Long, val folder: Boolean, val trashed: Boolean)
internal interface ProcessorLibraryConnectionReader {
    suspend fun account(): ProcessorLibraryAccount
    suspend fun workspace(id: String): ProcessorLibraryWorkspace
}

/** Read-only adapter borrowing an already owned, captured Google connection. Never
 * constructs a desktop URL or writes Google files. All calls join DriveGateway's I/O. */
internal class ProcessorLibraryDriveReader(private val gateway: DriveGateway) : ProcessorLibraryConnectionReader {
    override suspend fun account(): ProcessorLibraryAccount {
        val user = gateway.user()
        return ProcessorLibraryAccount(user.id,user.email)
    }
    override suspend fun workspace(id: String): ProcessorLibraryWorkspace {
        val value = gateway.file(id)
        val version = value.version?.toLongOrNull()?.takeIf { it > 0 && it.toString() == value.version }
            ?: throw IOException("Google did not return a valid workspace revision. Review the library again.")
        return ProcessorLibraryWorkspace(value.id, value.name, version, value.mimeType == "application/vnd.google-apps.folder", value.trashed)
    }
}

internal class ProcessorLibraryConnectionReview internal constructor(
    val descriptor: LibraryConnectionDescriptor,
    val workspace: ProcessorLibraryWorkspace,
    val account: ProcessorLibraryAccount,
)

/** A decoded code is merely a preview. Actual account/workspace reads are repeated
 * after the human approves its exact digest and before the immutable local pin commits.
 * This grants no device signing authority and does not activate a catalog. */
internal class ProcessorLibraryConnection(private val pins: ProcessorLibraryStateStore, private val reader: ProcessorLibraryConnectionReader) {
    suspend fun review(code: String): ProcessorLibraryConnectionReview {
        val descriptor = LibraryConnectionDescriptor.decode(code)
        val observed = observe(descriptor)
        val existing = pins.current()
        if (existing != null && existing.encode() != descriptor.encode()) throw ProcessorLibraryPinConflict()
        return ProcessorLibraryConnectionReview(descriptor, observed.first, observed.second)
    }

    suspend fun approve(review: ProcessorLibraryConnectionReview, approvedDocumentSha256: String): LibraryConnectionDescriptor {
        if (approvedDocumentSha256 != review.descriptor.documentSha256) throw IOException("The reviewed library connection changed. Review it again before approving.")
        val current = observe(review.descriptor)
        if (current.first != review.workspace || current.second != review.account) throw IOException("The Google Drive workspace changed. Review its current state before approving.")
        currentCoroutineContext().ensureActive()
        return pins.approve(review)
    }

    private suspend fun observe(descriptor: LibraryConnectionDescriptor): Pair<ProcessorLibraryWorkspace,ProcessorLibraryAccount> {
        val before = reader.account()
        if (before.id != descriptor.workspace.accountId) throw IOException("Sign in to the Google account used by this library. No library connection was changed.")
        currentCoroutineContext().ensureActive()
        val workspace = reader.workspace(descriptor.workspace.folderId)
        if (workspace.id != descriptor.workspace.folderId || !workspace.folder || workspace.trashed || workspace.version < 1)
            throw IOException("This library metadata folder is unavailable. Its saved connection was preserved.")
        val after = reader.account()
        if (after != before) throw IOException("The Google account changed during library review. No library connection was changed.")
        currentCoroutineContext().ensureActive()
        return workspace to after
    }
}

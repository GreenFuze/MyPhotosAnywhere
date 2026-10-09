package com.myphotosanywhere.mpa.drive

import android.app.Application
import android.app.PendingIntent
import android.content.Intent
import android.net.Uri
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.viewModelScope
import com.google.android.gms.auth.api.identity.AuthorizationResult
import com.google.android.gms.auth.api.identity.Identity
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.PhotoRepository
import java.io.File
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

data class FolderBrowser(
    val parentId: String = "root",
    val parentName: String = "My Drive",
    val folders: List<DriveFile> = emptyList(),
    val images: List<DriveFile> = emptyList(),
    val nextFolders: String? = null,
    val nextImages: String? = null,
)

class DriveViewModel(application: Application, private val saved: SavedStateHandle) : AndroidViewModel(application) {
    private val app = application as MpaApplication
    private val dao = app.database.drive()
    private val auth = DriveAuthorization(application)
    val connection = app.drive.connection.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), null)
    val backups = app.drive.backups.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())
    private val _busy = MutableStateFlow(false)
    val busy = _busy.asStateFlow()
    private val _message = MutableStateFlow<String?>(null)
    val message = _message.asStateFlow()
    private val _browser = MutableStateFlow<FolderBrowser?>(null)
    val browser = _browser.asStateFlow()
    private val path = mutableListOf<Pair<String, String>>()

    fun clearMessage() { _message.value = null }
    fun report(message: String) { _message.value = message }

    fun authorize(email: String, mode: DriveMode, launch: (PendingIntent) -> Unit) = perform {
        saved["email"] = email; saved["mode"] = mode.name
        val result = auth.request(email, mode)
        if (result.hasResolution()) launch(requireNotNull(result.pendingIntent)) else accept(result)
    }

    fun authorizationResult(intent: Intent) = perform {
        accept(Identity.getAuthorizationClient(app).getAuthorizationResultFromIntent(intent))
    }

    private suspend fun accept(result: AuthorizationResult) {
        val mode = DriveMode.valueOf(saved.get<String>("mode") ?: error("Choose a Drive access mode again."))
        val email = saved.get<String>("email") ?: error("Choose your Google account again.")
        if (!result.grantedScopes.contains(mode.scope)) throw DriveAuthorizationNeeded()
        val api = DriveGateway(result.accessToken ?: throw DriveAuthorizationNeeded())
        val user = api.user()
        check(user.email.equals(email, ignoreCase = true)) { "Google returned a different account. Please reconnect." }
        val previous = dao.connection()
        val same = previous != null && previous.accountId == user.id && previous.mode == mode.name
        val active = if (same) requireNotNull(previous).copy(email = user.email) else
            DriveConnection(accountId = user.id, email = user.email, mode = mode.name)
        dao.saveConnection(active)
        path.clear()
        _browser.value = readBrowser(api, "root", "My Drive")
        if (same && active.backupEnabled) app.drive.resume(retryFailures = true)
        _message.value = "Connected as ${user.email}. Choose your photo folder."
    }

    fun browse(folder: DriveFile? = null) = perform {
        val active = dao.connection() ?: error("Connect Drive first.")
        val api = authorizedApi(active)
        val current = _browser.value
        if (folder != null && current != null) path.add(current.parentId to current.parentName)
        val id = folder?.id ?: "root"
        val name = folder?.name ?: "My Drive"
        if (folder == null) path.clear()
        _browser.value = readBrowser(api, id, name)
    }

    fun back() = perform {
        val previous = path.lastOrNull() ?: return@perform
        val api = authorizedApi(dao.connection() ?: error("Connect Drive first."))
        _browser.value = readBrowser(api, previous.first, previous.second)
        path.removeAt(path.lastIndex)
    }

    fun moreFolders() = perform {
        val current = _browser.value ?: return@perform
        val next = current.nextFolders ?: return@perform
        val page = authorizedApi(dao.connection() ?: error("Connect Drive first.")).folders(current.parentId, next)
        _browser.value = current.copy(folders = (current.folders + page.files).distinctBy { it.id }, nextFolders = page.nextPage)
    }

    fun moreImages() = perform {
        val current = _browser.value ?: return@perform
        val next = current.nextImages ?: return@perform
        val page = authorizedApi(dao.connection() ?: error("Connect Drive first.")).images(current.parentId, next)
        _browser.value = current.copy(images = (current.images + page.files).distinctBy { it.id }, nextImages = page.nextPage)
    }

    fun createFolder(name: String) = perform {
        val active = dao.connection() ?: error("Connect Drive first.")
        val api = authorizedApi(active)
        val current = _browser.value ?: FolderBrowser()
        val created = api.createFolder(name, current.parentId)
        path.add(current.parentId to current.parentName)
        _browser.value = readBrowser(api, created.id, created.name)
    }

    fun useFolder() = perform {
        val selected = _browser.value ?: error("Open a folder first.")
        require(selected.parentId != "root") { "Choose a dedicated photo folder, not the root of Drive." }
        val active = dao.connection() ?: error("Connect Drive first.")
        val file = authorizedApi(active).file(selected.parentId)
        require(file.mimeType == DriveGateway.FOLDER_MIME && file.canAddChildren) { "This folder is not writable." }
        // Folder choice pauses backup. A separate visible action opts in to uploading existing local photos.
        app.drive.pause()
        dao.saveConnection(active.copy(folderId = file.id, folderName = file.name, backupEnabled = false))
        _browser.value = null
        _message.value = "Selected ${file.name}. Enable backup when you are ready."
    }

    fun browseSelected() = perform {
        val active = dao.connection() ?: error("Connect Drive first.")
        val folder = active.folderId ?: error("Choose a folder first.")
        path.clear()
        _browser.value = readBrowser(authorizedApi(active), folder, active.folderName ?: "Photo folder")
    }

    fun closeBrowser() { if (!_busy.value) _browser.value = null }

    fun enableBackup() = perform {
        val active = dao.connection() ?: error("Connect Drive first.")
        require(active.folderId != null) { "Choose a folder first." }
        dao.saveConnection(active.copy(backupEnabled = true))
        app.drive.resume(retryFailures = true)
        _message.value = "Backup queued. Originals stay on this device."
    }

    fun retryBackup() = perform { app.drive.resume(retryFailures = true); _message.value = "Pending backups queued again." }
    fun pauseBackup() = perform { app.drive.pause() }
    fun unmeteredOnly(enabled: Boolean) = perform {
        val active = dao.connection() ?: return@perform
        dao.saveConnection(active.copy(unmeteredOnly = enabled))
        app.drive.resume(replace = true)
    }
    fun disconnect() = perform { app.drive.disconnect(); _browser.value = null; path.clear() }

    fun importImage(file: DriveFile) = perform {
        val active = dao.connection() ?: error("Connect Drive first.")
        val selected = _browser.value ?: error("Open your selected photo folder first.")
        require(selected.parentId == active.folderId) { "Import photos from your selected photo folder." }
        require(file.mimeType.startsWith("image/")) { "Select an image." }
        val binding = requireNotNull(active.binding())
        val previous = dao.backups(binding.key).find { it.remoteId == file.id && it.state == "COMPLETE" }
        if (previous != null && app.database.photos().find(previous.photoId) != null) {
            _message.value = "This photo is already available offline."; return@perform
        }
        val api = authorizedApi(active)
        val before = api.file(file.id)
        require(active.folderId in before.parents) { "This file moved outside your selected folder." }
        require(before.md5 != null && before.version != null) { "Drive did not provide checksum and revision information for this image." }
        val temp = File(app.cacheDir, "drive-import/${UUID.randomUUID()}")
        temp.parentFile?.mkdirs()
        try {
            api.download(before.id, temp, PhotoRepository.MAX_IMPORT_BYTES)
            val md5 = withContext(Dispatchers.IO) { DriveBackupWorker.digest(temp, "MD5") }
            require(temp.length() == before.bytes && md5 == before.md5) {
                "Download checksum failed; try again."
            }
            val after = api.file(before.id)
            require(before.version == after.version && before.md5 == after.md5 && active.folderId in after.parents) {
                "This photo changed during download; try again."
            }
            // Import directly from the app's own private file, retaining the Drive name and MIME type.
            app.repository.importPhoto(Uri.fromFile(temp), nameOverride = before.name, mimeOverride = before.mimeType) { photo ->
                // This callback runs in the photo transaction: a restart cannot queue this download as a new upload.
                dao.insertBackup(DriveBackup("${binding.key}:${photo.id}", binding.key, photo.id, active.accountId,
                    active.email, requireNotNull(active.folderId), active.mode, remoteId = before.id,
                    state = "COMPLETE", uploadedBytes = photo.bytes, remoteMd5 = before.md5, remoteVersion = before.version))
            }
            _message.value = "${before.name} is now available offline."
        } finally { temp.delete() }
    }

    private suspend fun authorizedApi(active: DriveConnection): DriveGateway {
        val token = auth.token(active.email, DriveMode.valueOf(active.mode))
        val api = DriveGateway(token)
        try {
            check(api.user().id == active.accountId) { "Google account changed. Reconnect Drive." }
        } catch (error: DriveHttpException) {
            if (error.status == 401) auth.invalidate(token)
            throw error
        }
        return api
    }

    private suspend fun readBrowser(api: DriveGateway, id: String, name: String): FolderBrowser {
        val folders = api.folders(id)
        val images = if (id != "root") api.images(id) else DrivePage(emptyList(), null)
        return FolderBrowser(id, name, folders.files, images.files, folders.nextPage, images.nextPage)
    }

    private fun perform(action: suspend () -> Unit) {
        if (_busy.value) return
        _busy.value = true
        viewModelScope.launch {
            try { action() }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (error: com.google.android.gms.common.api.ApiException) {
                _message.value = "Google authorization failed (${error.statusCode}). Check Google Cloud Android OAuth setup."
            }
            catch (error: Exception) { _message.value = error.message ?: "Drive operation failed. Local photos are preserved." }
            finally { _busy.value = false }
        }
    }
}

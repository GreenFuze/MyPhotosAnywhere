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
import kotlinx.coroutines.flow.combine
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
    val sources = app.drive.sources.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())
    val connection = combine(app.drive.connection,app.drive.sources) { browser,sources ->
        browser?.let { selected -> sources.find { it.key==selected.binding()?.key }?.connection()
            ?: selected.copy(backupEnabled=false) }
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), null)
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

    fun authorizationResult(resultCode: Int,intent: Intent?) = perform {
        val result=GoogleAuthorizationReturn.resolve(resultCode,intent) {
            Identity.getAuthorizationClient(app).getAuthorizationResultFromIntent(it)
        }
        accept(result)
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
        for(source in dao.sources().filter { it.accountId==user.id }) {
            val refreshed=source.copy(email=user.email)
            dao.saveSource(refreshed)
            app.sync.schedule(refreshed.connection(),true); app.downloads.retry(source.key)
        }
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
        require(!file.trashed && file.mimeType == DriveGateway.FOLDER_MIME) { "This folder is unavailable." }
        val key=DriveBinding(active.accountId,file.id).key
        val old=dao.source(key)
        val source=DriveSource.from(active.copy(folderId=file.id,folderName=file.name,
            backupEnabled=old?.backupEnabled ?: false,unmeteredOnly=old?.unmeteredOnly ?: active.unmeteredOnly))
        dao.saveSource(source); dao.saveConnection(source.connection()); app.metadata.changed()
        app.sync.schedule(source.connection(),true); app.downloads.retry(source.key)
        _browser.value = null
        _message.value = "Added ${file.name}. Your other Drive sources and backup destination are preserved."
    }

    fun browseSelected() = perform {
        val active = dao.connection() ?: error("Connect Drive first.")
        val folder = active.folderId ?: error("Choose a folder first.")
        path.clear()
        _browser.value = readBrowser(authorizedApi(active), folder, active.folderName ?: "Photo folder")
    }

    fun selectSource(source: DriveSource) = perform {
        val stored=dao.source(source.key) ?: error("Reconnect this source first.")
        dao.saveConnection(stored.connection()); path.clear(); _browser.value=null
    }
    fun addAccount() = perform {
        dao.disconnect(); path.clear(); _browser.value=null
        _message.value="Choose another Google account. Existing sources continue syncing."
    }

    fun closeBrowser() { if (!_busy.value) _browser.value = null }

    fun enableBackup() = perform {
        val active = dao.connection() ?: error("Connect Drive first.")
        require(active.folderId != null) { "Choose a folder first." }
        val binding=requireNotNull(active.binding()).key
        val folder=authorizedApi(active).file(requireNotNull(active.folderId))
        require(!folder.trashed && folder.canAddChildren) { "This folder is read-only. Choose a writable backup destination." }
        val previous=dao.backupDestination()
        dao.chooseBackupDestination(binding)
        if(previous!=null && previous.key!=binding) androidx.work.WorkManager.getInstance(app)
            .cancelAllWorkByTag("${DriveCoordinator.BACKUP_TAG}:${previous.key}")
        dao.saveConnection(active.copy(backupEnabled = true))
        app.drive.resume(retryFailures = true)
        _message.value = "${active.folderName} is the single backup destination. Imports and edited copies queued."
    }

    fun retryBackup() = perform { app.drive.resume(retryFailures = true); _message.value = "Pending backups queued again." }
    fun pauseBackup() = perform { app.drive.pause() }
    fun unmeteredOnly(enabled: Boolean) = perform {
        val active = dao.connection() ?: return@perform
        val key=active.binding()?.key
        dao.saveConnection(active.copy(unmeteredOnly = enabled))
        if(key!=null) dao.source(key)?.let { source ->
            dao.saveSource(source.copy(unmeteredOnly=enabled))
            app.sync.schedule(source.connection().copy(unmeteredOnly=enabled),true)
            app.downloads.retry(key)
            app.metadata.stop(key); app.metadata.schedule()
        }
        app.drive.resume(replace = true)
    }
    fun disconnect() = perform { app.drive.disconnect(); _browser.value = null; path.clear() }

    fun importImage(file: DriveFile) = perform {
        val active=dao.connection() ?: error("Connect Drive first.")
        val folder=requireNotNull(active.folderId)
        val before=authorizedApi(active).file(file.id)
        require(folder in before.parents && !before.trashed) { "Select an image inside your photo folder." }
        val binding=requireNotNull(active.binding()).key
        val old=app.database.cloud().photo("$binding:${before.id}")
        val row=old ?: CloudPhoto("$binding:${before.id}",binding,before.id,folder,before.name,before.mimeType,
            before.bytes,before.md5,before.version,before.modifiedTime,before.capturedAt,before.latitude,before.longitude,
            thumbnailPath=File(app.filesDir,"cloud-thumbnails/$binding/${before.id}.jpg").absolutePath,
            photoId=dao.backupForRemote(binding,before.id)?.photoId)
        app.database.cloud().savePhoto(row)
        app.downloads.enqueue(row,true)
        _message.value="Offline download queued."
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
            catch (error: Exception) { _message.value = GoogleAuthorizationReturn.message(error) }
            finally { _busy.value = false }
        }
    }
}

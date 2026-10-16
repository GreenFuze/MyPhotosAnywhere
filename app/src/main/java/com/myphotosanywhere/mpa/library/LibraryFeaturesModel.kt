package com.myphotosanywhere.mpa.library

import android.app.Application
import android.content.Intent
import android.net.Uri
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.room.withTransaction
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.core.StorageSettings
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.CloudPhoto
import com.myphotosanywhere.mpa.people.FaceMatching
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.sync.withLock
import java.io.File
import java.util.UUID

internal data class DeviceCopyRequest(val name: String,val identity: DeviceCopyIdentity)

@OptIn(ExperimentalCoroutinesApi::class)
class LibraryFeaturesModel(application: Application): AndroidViewModel(application) {
    val app=application as MpaApplication
    private val db=app.database
    private val legacyActive=MutableStateFlow(false)
    fun legacyLoading(enabled: Boolean) { legacyActive.value=enabled }
    val albums=db.library().albums().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val memberships=legacyActive.flatMapLatest { if(it) db.library().memberships() else flowOf(emptyList()) }.stateIn(viewModelScope,SharingStarted.WhileSubscribed(0,0),emptyList())
    val people=db.library().people().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val faces=legacyActive.flatMapLatest { if(it) db.library().faces() else flowOf(emptyList()) }.stateIn(viewModelScope,SharingStarted.WhileSubscribed(0,0),emptyList())
    val cloud=legacyActive.flatMapLatest { if(it) db.cloud().observePhotos() else flowOf(emptyList()) }.stateIn(viewModelScope,SharingStarted.WhileSubscribed(0,0),emptyList())
    val sync=db.cloud().observeSync().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val downloads=db.cloud().observeDownloads().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val connection=app.drive.connection.stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),null)
    val sources=app.drive.sources.stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val settings=app.settings.state.asStateFlow()
    val indexProgress=app.people.progress.asStateFlow()
    val metadata=db.metadata().observe().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val metadataPreview=MutableStateFlow<com.myphotosanywhere.mpa.metadata.MetadataPreview?>(null)
    val message=MutableStateFlow<String?>(null)
    val busy=MutableStateFlow(false)
    internal val deviceCopyRequest=MutableStateFlow<DeviceCopyRequest?>(null)
    internal fun requestRemoveDownload(photo: Photo) {
        if(busy.value) return
        val identity=DeviceCopyIdentity.from(photo)
        if(identity==null) message.value="This original has no verified Drive copy. It is protected on this device."
        else deviceCopyRequest.value=DeviceCopyRequest(photo.displayName,identity)
    }
    internal fun dismissRemoveDownload() { if(!busy.value) deviceCopyRequest.value=null }
    internal fun confirmRemoveDownload(onClosing: ()->Unit) {
        if(busy.value) return
        val request=deviceCopyRequest.value ?: return
        deviceCopyRequest.value=null;busy.value=true
        message.value="Verifying the Drive original before removing this download…"
        onClosing()
        viewModelScope.launch {
            try { message.value=app.cache.removeDownloadedCopy(request.identity) }
            catch(cancelled: CancellationException) { throw cancelled }
            catch(error: java.io.IOException) { message.value=error.message ?: "The device copy was kept. Retry." }
            catch(error: android.database.sqlite.SQLiteException) { message.value=error.message ?: "Cannot update download availability. Retry." }
            catch(_: SecurityException) { message.value="Android blocked access to this download. It was kept." }
            finally { busy.value=false }
        }
    }
    fun clearMessage() { message.value=null }
    fun perform(action: suspend ()->Unit) {
        if(busy.value) return
        busy.value=true
        viewModelScope.launch {
            try { action(); app.metadata.changed() }
            catch(e: CancellationException) { throw e }
            catch(e: Exception) { message.value=e.message ?: "Operation failed. Your photos are preserved." }
            finally { busy.value=false }
        }
    }
    fun pinPhoto(id: String,pin: Boolean) = perform {
        app.cache.lock.withLock { db.photos().find(id)?.let { db.photos().update(it.copy(pinned=pin)) } }
        val photo=db.photos().find(id)
        if(pin && photo!=null && photo.originalPath.isBlank() && photo.sourceRemoteId!=null)
            db.cloud().photo("${photo.sourceBindingKey}:${photo.sourceRemoteId}")?.let { app.downloads.enqueue(it,true) }
    }
    fun updatePhoto(id: String,transform: (Photo)->Photo) = perform {
        app.cache.lock.withLock { db.photos().find(id)?.let { db.photos().update(transform(it)) } }
    }
    fun createAlbum(name: String) = perform {
        require(name.isNotBlank() && name.length<=100)
        db.library().saveAlbum(Album(UUID.randomUUID().toString(),name.trim(),System.currentTimeMillis()))
    }
    fun renameAlbum(album: Album,name: String) = perform {
        require(name.isNotBlank() && name.length<=100)
        if(db.library().renameAlbum(album.id,name.trim())!=1) throw java.io.IOException("This album is no longer available.")
    }
    fun addToAlbum(albumId: String,photoId: String) = perform { db.library().addToAlbum(AlbumPhoto(albumId,photoId)) }
    fun removeFromAlbum(albumId: String,photoId: String) = perform { db.library().removeFromAlbum(albumId,photoId) }
    fun deleteAlbum(album: Album) = perform {
        db.withTransaction { db.library().clearAlbum(album.id); db.library().deleteAlbum(album.id) }
        message.value="Album removed. Photos are preserved."
    }
    fun pinAlbum(album: Album,pin: Boolean) = perform {
        app.cache.lock.withLock {
            if(db.library().pinAlbum(album.id,pin)!=1) throw java.io.IOException("This album is no longer available.")
        }
        if(pin) db.library().members(album.id).forEach { member ->
            val photo=db.photos().find(member.photoId)
            if(photo!=null && photo.originalPath.isBlank() && photo.sourceRemoteId!=null)
                db.cloud().photo("${photo.sourceBindingKey}:${photo.sourceRemoteId}")?.let { app.downloads.enqueue(it) }
        }
    }
    fun download(row: CloudPhoto,pin: Boolean = false,retry: Boolean = false) = perform { app.downloads.enqueue(row,pin,retry); message.value="Download queued; unmetered network preference applies." }
    fun retryDownloads() = perform { app.downloads.retry() }
    fun resumeForegroundImports() {
        viewModelScope.launch { app.imports.resumeWhenVisible() }
    }
    private var lastForegroundRefresh=0L
    fun refreshCloudAutomatically() {
        viewModelScope.launch {
            val now=android.os.SystemClock.elapsedRealtime()
            if(lastForegroundRefresh!=0L && now-lastForegroundRefresh<30_000) return@launch
            val connected=db.drive().sources()
            if(connected.isEmpty()) return@launch
            lastForegroundRefresh=now
            connected.forEach { app.sync.schedule(it.connection(),true) }
        }
    }
    fun refreshCloud() = perform {
        val sources=db.drive().sources()
        require(sources.isNotEmpty()) { "Connect a Drive source in Storage first." }
        sources.forEach { app.sync.schedule(it.connection(),true) }
        message.value="Refresh queued for ${sources.size} Drive sources."
    }
    fun refreshSource(binding: String) = perform {
        val source = db.drive().source(binding) ?: error("Reconnect this Drive source first.")
        app.sync.schedule(source.connection(), true)
        message.value = "Refresh queued for ${source.folderName}; its network preference applies."
    }
    fun updateSettings(value: StorageSettings) {
        app.settings.update(value); app.people.schedule()
        viewModelScope.launch { app.metadata.changed() }
    }
    fun trimCache(clear: Boolean = false) = perform { message.value=app.cache.enforce(clear) }
    fun deletePeopleIndex() = perform { app.people.deleteIndex(); message.value="Face crops, names, and embeddings deleted from this phone." }
    fun scanFaces() = perform { app.people.scanNow(); message.value="On-device scan queued; global identities are shared across all connected sources." }
    fun rescanFaces() = perform { app.people.rescanFaces(); message.value="Full face rescan queued. Existing names and corrections are preserved; connected originals may be downloaded again." }
    fun pauseIndexing() { app.people.pause() }
    fun previewMetadata(key: String) = perform { metadataPreview.value=app.metadata.preview(key) }
    fun recoverMetadata() = perform {
        val preview=metadataPreview.value ?: error("Preview recovery first.")
        app.metadata.recover(preview); metadataPreview.value=null
        message.value="Annotations recovered for ${preview.matching} checksum-matched photos. Other photos are preserved."
    }
    fun backupMetadata() = perform { app.metadata.changed(); message.value="XMP backups queued. Network preferences apply; conflicts require recovery review." }
    fun exportMetadata(key: String,uri: Uri) = perform {
        val source=db.drive().source(key) ?: error("Reconnect this source.")
        val xml=com.myphotosanywhere.mpa.metadata.XmpCodec.encode(app.metadata.snapshot(source))
        withContext(Dispatchers.IO) { app.contentResolver.openOutputStream(uri)?.use { it.write(xml.toByteArray(Charsets.UTF_8)) }
            ?: error("Cannot write XMP export.") }
        message.value="Source annotations exported as XMP. Original media is unchanged."
    }
    fun assign(faceIds: List<String>,name: String,personId: String? = null) = perform {
        require(name.isNotBlank() && name.length<=100)
        app.people.assign(faceIds,Person(personId ?: UUID.randomUUID().toString(),name.trim()))
    }
    fun acceptFace(id: String,person: Person) = perform { app.people.assign(listOf(id),person) }
    fun unassignFace(id: String) = perform { app.people.unassign(id) }
    fun renamePerson(person: Person,name: String) = perform {
        require(name.isNotBlank() && name.length<=100); db.library().savePerson(person.copy(name=name.trim()))
    }
    fun mergePeople(source: String,target: String) = perform { app.people.merge(source,target); app.people.refreshSuggestions() }
    fun deletePerson(id: String) = perform { app.people.deletePerson(id) }
    fun share(photo: Photo,launch: (Intent)->Unit) = perform {
        val file=File(app.cacheDir,"handoff/${UUID.randomUUID()}.${if(photo.mimeType.startsWith("video/")) "mp4" else "jpg"}")
        file.parentFile?.mkdirs()
        app.cache.withOriginal(photo.id) { current,original ->
            if(current.mimeType.startsWith("video/")) original.copyTo(file,true)
            else {
                val bitmap=PhotoRepository.preview(original,4096)
                try { file.outputStream().use { check(bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG,95,it)) } }
                finally { bitmap.recycle() }
            }
        }
        val uri=androidx.core.content.FileProvider.getUriForFile(app,"${app.packageName}.handoff",file)
        launch(Intent.createChooser(Intent(Intent.ACTION_SEND).apply {
            type=if(photo.mimeType.startsWith("video/")) photo.mimeType else "image/jpeg"
            putExtra(Intent.EXTRA_STREAM,uri); clipData=android.content.ClipData.newUri(app.contentResolver,"MPA photo",uri)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        },"Share photo").apply { addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION) })
    }
    fun export(photo: Photo,uri: Uri) = export(photo.id,uri)
    fun export(id: String,uri: Uri) = perform {
        app.cache.withOriginal(id) { _,original ->
            app.contentResolver.openOutputStream(uri)?.use { out ->
                original.inputStream().use { it.copyTo(out) }
            } ?: error("Cannot write the exported photo.")
        }
        message.value="Export saved. Original bytes and metadata preserved."
    }
    init { viewModelScope.launch {
        db.drive().sources().forEach { app.sync.schedule(it.connection(),true) }
        app.downloads.resume(); app.people.schedule(); app.metadata.schedule(); app.imports.resumePending()
    } }
}

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

class LibraryFeaturesModel(application: Application): AndroidViewModel(application) {
    val app=application as MpaApplication
    private val db=app.database
    val albums=db.library().albums().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val memberships=db.library().memberships().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val people=db.library().people().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val faces=db.library().faces().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val cloud=db.cloud().observePhotos().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val sync=db.cloud().observeSync().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val downloads=db.cloud().observeDownloads().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val connection=app.drive.connection.stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),null)
    val settings=app.settings.state.asStateFlow()
    val message=MutableStateFlow<String?>(null)
    val busy=MutableStateFlow(false)
    fun clearMessage() { message.value=null }
    fun perform(action: suspend ()->Unit) {
        if(busy.value) return
        busy.value=true
        viewModelScope.launch {
            try { action() }
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
        require(name.isNotBlank() && name.length<=100); db.library().saveAlbum(album.copy(name=name.trim()))
    }
    fun addToAlbum(albumId: String,photoId: String) = perform { db.library().addToAlbum(AlbumPhoto(albumId,photoId)) }
    fun removeFromAlbum(albumId: String,photoId: String) = perform { db.library().removeFromAlbum(albumId,photoId) }
    fun deleteAlbum(album: Album) = perform {
        db.withTransaction { db.library().clearAlbum(album.id); db.library().deleteAlbum(album.id) }
        message.value="Album removed. Photos are preserved."
    }
    fun pinAlbum(album: Album,pin: Boolean) = perform {
        app.cache.lock.withLock { db.library().saveAlbum(album.copy(pinned=pin)) }
        if(pin) db.library().members(album.id).forEach { member ->
            val photo=db.photos().find(member.photoId)
            if(photo!=null && photo.originalPath.isBlank() && photo.sourceRemoteId!=null)
                db.cloud().photo("${photo.sourceBindingKey}:${photo.sourceRemoteId}")?.let { app.downloads.enqueue(it) }
        }
    }
    fun download(row: CloudPhoto,pin: Boolean = false) = perform { app.downloads.enqueue(row,pin); message.value="Download queued; unmetered network preference applies." }
    fun retryDownloads() = perform { app.downloads.retry() }
    fun refreshCloud() = perform {
        val active=db.drive().connection() ?: error("Connect Drive in Storage first.")
        require(active.folderId!=null); app.sync.schedule(active,true); message.value="Drive refresh queued."
    }
    fun updateSettings(value: StorageSettings) {
        app.settings.update(value); app.people.schedule()
    }
    fun trimCache(clear: Boolean = false) = perform { message.value=app.cache.enforce(clear) }
    fun deletePeopleIndex() = perform { app.people.deleteIndex(); message.value="Face crops, names, and embeddings deleted from this phone." }
    fun scanFaces() { app.people.schedule(); message.value="On-device indexing queued. Downloads are indexed when they become available." }
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
    fun export(photo: Photo,uri: Uri) = perform {
        app.cache.withOriginal(photo.id) { _,original ->
            app.contentResolver.openOutputStream(uri)?.use { out ->
                original.inputStream().use { it.copyTo(out) }
            } ?: error("Cannot write the exported photo.")
        }
        message.value="Export saved. Original bytes and metadata preserved."
    }
    init { viewModelScope.launch {
        val active=db.drive().connection()
        if(active?.folderId!=null) app.sync.schedule(active,true)
        app.downloads.resume(); app.people.schedule()
    } }
}

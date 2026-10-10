package com.myphotosanywhere.mpa.edit

import android.app.Application
import android.graphics.Bitmap
import android.net.Uri
import androidx.exifinterface.media.ExifInterface
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.EditDraft
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import java.io.File
import java.util.UUID

@OptIn(kotlinx.coroutines.FlowPreview::class)
class EditorModel(application: Application): AndroidViewModel(application) {
    private val app=application as MpaApplication
    val recipe=MutableStateFlow(EditRecipe())
    val preview=MutableStateFlow<Bitmap?>(null)
    val busy=MutableStateFlow(false)
    val error=MutableStateFlow<String?>(null)
    val savedId=MutableStateFlow<String?>(null)
    val historyVersion=MutableStateFlow(0)
    private var photoId: String?=null
    private var history=EditHistory(EditRecipe())
    val canUndo get()=history.canUndo
    val canRedo get()=history.canRedo
    private var rendering: Job?=null
    fun open(id: String) {
        if(photoId==id) return
        photoId=id; savedId.value=null; preview.value=null
        rendering?.cancel()
        rendering=viewModelScope.launch {
            val reader=app.cache.acquireReader(id)
            try {
            val draft=app.database.library().draft(id)
            val initial=draft?.recipe?.let { runCatching { EditRecipe.parse(it) }.getOrNull() } ?: EditRecipe()
            history=EditHistory(initial); recipe.value=initial; historyVersion.value++
            recipe.debounce(200).collectLatest { value ->
                try {
                    app.database.library().saveDraft(EditDraft(id,value.json(),System.currentTimeMillis()))
                    app.metadata.changed()
                    val bitmap=app.cache.withOriginal(id) { _,file -> PhotoRenderer.render(file,value,1024) }
                    try { currentCoroutineContext().ensureActive(); preview.value=bitmap }
                    catch(e: CancellationException) { bitmap.recycle(); throw e }
                } catch(e: CancellationException) { throw e }
                catch(e: Exception) { error.value=e.message ?: "Could not render this photo." }
            }
            } finally { reader.close() }
        }
    }
    fun change(value: EditRecipe) { if(!busy.value) recipe.value=value.validate() }
    fun commit(value: EditRecipe) { if(!busy.value) { history.commit(value); recipe.value=value; historyVersion.value++ } }
    fun undo() { if(!busy.value) { recipe.value=history.undo(); historyVersion.value++ } }
    fun redo() { if(!busy.value) { recipe.value=history.redo(); historyVersion.value++ } }
    fun save() {
        val id=photoId ?: return
        if(busy.value) return
        busy.value=true; error.value=null
        val value=recipe.value.validate()
        viewModelScope.launch {
            val temp=File(app.cacheDir,"editor/${UUID.randomUUID()}.jpg"); temp.parentFile?.mkdirs()
            try {
                val source=app.cache.withOriginal(id) { photo,file ->
                    val bitmap=PhotoRenderer.render(file,value)
                    try { temp.outputStream().use { out -> check(bitmap.compress(Bitmap.CompressFormat.JPEG,95,out)); out.fd.sync() } }
                    finally { bitmap.recycle() }
                    photo.capturedAt?.let { captured ->
                        val exif=ExifInterface(temp)
                        exif.setAttribute(ExifInterface.TAG_DATETIME_ORIGINAL,java.text.SimpleDateFormat("yyyy:MM:dd HH:mm:ss",java.util.Locale.US).apply {
                            timeZone=java.util.TimeZone.getTimeZone("UTC")
                        }.format(java.util.Date(captured)))
                        exif.setAttribute(ExifInterface.TAG_OFFSET_TIME_ORIGINAL,"+00:00");exif.saveAttributes()
                    }
                    if(value.keepLocation && photo.latitude!=null && photo.longitude!=null) {
                        val exif=ExifInterface(temp); exif.setLatLong(photo.latitude,photo.longitude); exif.saveAttributes()
                    }
                    photo
                }
                val result=app.repository.importPhoto(Uri.fromFile(temp),nameOverride="${source.displayName.substringBeforeLast('.')}-edited.jpg",
                    mimeOverride="image/jpeg") { photo ->
                    app.database.photos().update(photo.copy(parentId=id,editProvider="MPA",editInstructions=value.json(),
                        capturedAt=source.capturedAt ?: source.importedAt,placeLabel=if(value.keepLocation) source.placeLabel else ""))
                    app.database.library().deleteDraft(id)
                }
                try { app.drive.enqueue(app.database.photos().find(result.id) ?: result) }
                catch(e: CancellationException) { throw e }
                catch(_: Exception) { /* The next backup resume queues the durable edited copy. */ }
                app.people.schedule(); app.metadata.changed()
                savedId.value=result.id
            } catch(e: CancellationException) { throw e }
            catch(e: Exception) { error.value=e.message ?: "Could not save this edit." }
            finally { temp.delete(); busy.value=false }
        }
    }
    fun close() { rendering?.cancel(); photoId=null; preview.value=null }
}

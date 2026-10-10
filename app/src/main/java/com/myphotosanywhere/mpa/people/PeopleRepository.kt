package com.myphotosanywhere.mpa.people

import android.content.Context
import android.graphics.Bitmap
import androidx.room.withTransaction
import androidx.work.*
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.face.*
import com.google.mlkit.vision.label.ImageLabeling
import com.google.mlkit.vision.label.defaults.ImageLabelerOptions
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.core.LibrarySettings
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.tasks.await
import java.io.File
import java.util.UUID

class PeopleRepository(private val context: Context,private val db: PhotoDatabase,
    private val cache: MediaCache,private val settings: LibrarySettings) {
    private val indexLock=Mutex()
    fun schedule() {
        if(!settings.state.value.faceIndexing && !settings.state.value.objectIndexing) return
        WorkManager.getInstance(context).enqueueUniqueWork("mpa-local-index",ExistingWorkPolicy.KEEP,
            OneTimeWorkRequestBuilder<PeopleWorker>().setConstraints(Constraints.Builder().setRequiresBatteryNotLow(true).build()).build())
    }
    suspend fun index() = indexLock.withLock { withContext(Dispatchers.IO) {
        val detector=FaceDetection.getClient(FaceDetectorOptions.Builder().setPerformanceMode(FaceDetectorOptions.PERFORMANCE_MODE_ACCURATE)
            .setLandmarkMode(FaceDetectorOptions.LANDMARK_MODE_ALL).setMinFaceSize(.03f).build())
        val labeler=ImageLabeling.getClient(ImageLabelerOptions.Builder().setConfidenceThreshold(.7f).build())
        var model: FaceEmbedding?=null
        try {
            if(settings.state.value.faceIndexing) model=FaceEmbedding(context)
            for(photo in db.photos().all()) {
                currentCoroutineContext().ensureActive()
                if(!settings.state.value.faceIndexing && !settings.state.value.objectIndexing) break
                if(!photo.mimeType.startsWith("image/") || photo.originalPath.isBlank() || !File(photo.originalPath).isFile) continue
                val scanned=db.library().scan(photo.id)
                val facesNeeded=settings.state.value.faceIndexing && (scanned==null || scanned.error!=null || scanned.sha256!=photo.sha256)
                val objectsNeeded=settings.state.value.objectIndexing && photo.labels.isBlank()
                if(!facesNeeded && !objectsNeeded) continue
                try {
                    cache.withOriginal(photo.id) { current,file ->
                        val image=PhotoRepository.preview(file,1600)
                        try {
                            val input=InputImage.fromBitmap(image,0)
                            if(objectsNeeded) {
                                val labels=labeler.process(input).await().map { it.text }.distinct().joinToString(", ")
                                if(settings.state.value.objectIndexing) db.photos().find(photo.id)?.let { db.photos().update(it.copy(labels=labels.ifBlank { "Unclassified" })) }
                            }
                            if(facesNeeded && settings.state.value.faceIndexing) {
                                val found=detector.process(input).await()
                                val saved=mutableListOf<FaceRecord>()
                                try {
                                    for((index,face) in found.withIndex()) {
                                        if(!settings.state.value.faceIndexing) break
                                        val id="${photo.id}:$index"; val previous=db.library().face(id)
                                        if(previous!=null) continue // Never overwrite a user's identity correction.
                                        val rect=face.boundingBox
                                        val x=rect.left.coerceIn(0,image.width-1); val y=rect.top.coerceIn(0,image.height-1)
                                        val w=(rect.right-x).coerceIn(1,image.width-x); val h=(rect.bottom-y).coerceIn(1,image.height-y)
                                        val crop=Bitmap.createBitmap(image,x,y,w,h)
                                        val small=Bitmap.createScaledBitmap(crop,160,160,true)
                                        val path=File(context.filesDir,"faces/${photo.id}-$index.jpg"); path.parentFile?.mkdirs()
                                        try { path.outputStream().use { check(small.compress(Bitmap.CompressFormat.JPEG,85,it)) } }
                                        finally { if(small!==crop) small.recycle(); if(crop!==image) crop.recycle() }
                                        val aligned=model?.aligned(image,face)
                                        val embedding=if(aligned==null) null else try { model?.embed(aligned) } finally { aligned.recycle() }
                                        saved.add(FaceRecord(id,photo.id,path.absolutePath,embedding))
                                    }
                                    if(settings.state.value.faceIndexing) db.withTransaction {
                                        saved.forEach { db.library().saveFace(it) }; db.library().saveScan(FaceScan(photo.id,current.sha256))
                                    } else saved.forEach { File(it.cropPath).delete() }
                                } catch(e: Exception) { saved.forEach { File(it.cropPath).delete() }; throw e }
                            }
                        } finally { image.recycle() }
                    }
                } catch(e: CancellationException) { throw e }
                catch(e: Exception) { if(settings.state.value.faceIndexing) db.library().saveScan(FaceScan(photo.id,photo.sha256,e.message ?: "Indexing failed.")) }
            }
            if(settings.state.value.faceIndexing) refreshSuggestions()
        } finally { detector.close(); labeler.close(); model?.close() }
    } }
    suspend fun refreshSuggestions() {
        val faces=db.library().allFaces()
        val confirmed=faces.mapNotNull { f -> if(f.personId==null || f.embedding==null) null else f.personId to FaceMatching.decode(f.embedding) }
        db.withTransaction { faces.filter { it.personId==null && !it.corrected && it.embedding!=null }.forEach { f ->
            val suggestion=FaceMatching.suggest(FaceMatching.decode(requireNotNull(f.embedding)),confirmed)
            db.library().saveFace(f.copy(suggestedPersonId=suggestion?.first,similarity=suggestion?.second))
        } }
    }
    suspend fun assign(faceIds: List<String>,person: Person) {
        db.withTransaction {
            db.library().savePerson(person)
            faceIds.forEach { id -> db.library().face(id)?.let {
                db.library().saveFace(it.copy(personId=person.id,suggestedPersonId=null,similarity=null,corrected=true))
            } }
        }
        refreshSuggestions()
    }
    suspend fun unassign(id: String) { db.library().face(id)?.let {
        db.library().saveFace(it.copy(personId=null,suggestedPersonId=null,similarity=null,corrected=true))
    } }
    suspend fun merge(source: String,target: String) = db.withTransaction {
        require(source!=target); db.library().mergePeople(source,target)
        db.library().clearSuggestions(source); db.library().deletePerson(source)
    }
    suspend fun deletePerson(id: String) = db.withTransaction {
        db.library().unassignPerson(id); db.library().clearSuggestions(id); db.library().deletePerson(id)
    }
    suspend fun deleteIndex() {
        settings.update(settings.state.value.copy(faceIndexing=false))
        WorkManager.getInstance(context).cancelUniqueWork("mpa-local-index")
        indexLock.withLock {
            cache.lock.withLock {
                db.withTransaction { db.library().deleteFaces(); db.library().deleteScans(); db.library().deletePeople() }
                File(context.filesDir,"faces").deleteRecursively()
            }
        }
        if(settings.state.value.objectIndexing) schedule()
    }
}
class PeopleWorker(context: Context,params: WorkerParameters): CoroutineWorker(context,params) {
    override suspend fun doWork(): Result {
        val app=applicationContext as MpaApplication
        return try { app.people.index(); Result.success() }
        catch(e: CancellationException) { throw e }
        catch(_: Exception) { Result.failure() }
    }
}

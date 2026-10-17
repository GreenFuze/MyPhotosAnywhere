package com.myphotosanywhere.mpa.people

import androidx.core.content.edit
import androidx.core.graphics.scale
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
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.tasks.await
import java.io.File
import java.util.UUID

class PeopleRepository(private val context: Context,private val db: PhotoDatabase,
    private val cache: MediaCache,private val settings: LibrarySettings) {
    private val indexLock=Mutex()
    private val prefs=context.getSharedPreferences("index-progress",Context.MODE_PRIVATE)
    val progress=kotlinx.coroutines.flow.MutableStateFlow(IndexProgress("Ready",prefs.getInt("done",0),prefs.getInt("total",0),prefs.getInt("errors",0)))
    private fun report(status: String,done: Int,total: Int,errors: Int) {
        progress.value=IndexProgress(status,done,total,errors)
        prefs.edit { putInt("done",done);putInt("total",total);putInt("errors",errors) }
    }
    fun pause() {
        settings.update(settings.state.value.copy(indexingPaused=true))
        WorkManager.getInstance(context).cancelUniqueWork("mpa-local-index")
        WorkManager.getInstance(context).cancelUniqueWork("mpa-night-index")
        report("Paused",progress.value.done,progress.value.total,progress.value.errors)
    }
    suspend fun scanNow() {
        settings.update(settings.state.value.copy(indexingPaused=false))
        db.library().retryScans()
        report("Scan queued",0,0,0)
        schedule(true)
    }
    /** Invalidate successful checkpoints as well, retaining stable regions and all corrections. */
    internal suspend fun resetFaceScans() = indexLock.withLock {
        require(settings.state.value.faceIndexing) { "Enable on-device face indexing first." }
        db.withTransaction { db.library().deleteScans() }
        settings.update(settings.state.value.copy(indexingPaused=false))
    }
    suspend fun rescanFaces() {
        resetFaceScans()
        report("Face rescan queued",0,0,0)
        schedule(true)
    }
    fun schedule(manual: Boolean=false) {
        val value=settings.state.value; val manager=WorkManager.getInstance(context)
        if((!value.faceIndexing && !value.objectIndexing) || value.indexingPaused) {
            manager.cancelUniqueWork("mpa-local-index"); manager.cancelUniqueWork("mpa-night-index"); return
        }
        manager.enqueueUniquePeriodicWork("mpa-night-index",ExistingPeriodicWorkPolicy.UPDATE,
            PeriodicWorkRequestBuilder<PeopleWorker>(15,java.util.concurrent.TimeUnit.MINUTES)
                .setConstraints(Constraints.Builder().setRequiresCharging(value.overnightIndexing).setRequiresBatteryNotLow(true).build()).build())
        val scheduled=value.overnightIndexing && !manual
        val delay=if(scheduled) IndexWindow.delay(java.time.ZonedDateTime.now(),value.nightStart,value.nightEnd).toMillis() else 0L
        manager.enqueueUniqueWork("mpa-local-index",if(manual) ExistingWorkPolicy.REPLACE else ExistingWorkPolicy.KEEP,
            OneTimeWorkRequestBuilder<PeopleWorker>().setInputData(workDataOf("manual" to manual))
                .setInitialDelay(delay,java.util.concurrent.TimeUnit.MILLISECONDS)
                .setConstraints(Constraints.Builder().setRequiresCharging(scheduled).setRequiresBatteryNotLow(true).build()).build())
    }
    private fun allowed(manual: Boolean): Boolean {
        val value=settings.state.value
        if(value.indexingPaused || (!value.faceIndexing && !value.objectIndexing)) return false
        if(value.overnightIndexing && !manual) {
            val now=java.time.LocalTime.now()
            val battery=context.registerReceiver(null,android.content.IntentFilter(android.content.Intent.ACTION_BATTERY_CHANGED))
            val status=battery?.getIntExtra(android.os.BatteryManager.EXTRA_STATUS,-1)
            if(status!=android.os.BatteryManager.BATTERY_STATUS_CHARGING && status!=android.os.BatteryManager.BATTERY_STATUS_FULL) return false
            if(!IndexWindow.contains(now.hour*60+now.minute,value.nightStart,value.nightEnd)) return false
        }
        val power=context.getSystemService(Context.POWER_SERVICE) as android.os.PowerManager
        return android.os.Build.VERSION.SDK_INT<29 || power.currentThermalStatus<android.os.PowerManager.THERMAL_STATUS_MODERATE
    }
    private suspend fun needed(photo: Photo): Boolean {
        val value=settings.state.value; val scan=db.library().scan(photo.id)
        val version=(context.applicationContext as MpaApplication).deviceMedia.recognitionVersion(photo)
        return photo.mimeType.startsWith("image/") &&
            (value.faceIndexing && (scan==null || scan.sha256!=photo.sha256 || scan.modelVersion!=version) && scan?.error==null ||
                value.objectIndexing && photo.labels.isBlank() && scan?.error==null)
    }
    /** At most eight cloud originals in flight, taking one source at a time in rotating rounds. */
    private suspend fun queueCloud(manual: Boolean): Int {
        if(!settings.state.value.indexCloud || !allowed(manual)) return 0
        val app=context.applicationContext as MpaApplication
        val sources=db.drive().sources(); if(sources.isEmpty()) return 0
        val downloads=db.cloud().observeDownloads().first().associateBy { it.key }
        var remaining=(8-downloads.values.count { it.state in listOf("PENDING","DOWNLOADING","RETRYING") && sources.any { source -> source.key==it.bindingKey } }).coerceAtLeast(0)
        val cursor=prefs.getInt("sourceCursor",0).mod(sources.size)
        val ordered=sources.drop(cursor)+sources.take(cursor)
        val queues=ordered.map { source -> db.cloud().photos(source.key).filter { row ->
            if(row.removed || !row.mimeType.startsWith("image/") || row.bytes<=0 || row.bytes>PhotoRepository.MAX_IMPORT_BYTES || row.md5==null) false
            else {
                val photo=row.photoId?.let { db.photos().find(it) }
                val task=downloads[row.key]
                (photo==null || needed(photo) && (photo.originalPath.isBlank() || !File(photo.originalPath).isFile)) &&
                    (task==null || task.state=="COMPLETE")
            }
        }.toMutableList() }
        var queued=0
        while(remaining>0 && queues.any { it.isNotEmpty() } && allowed(manual)) {
            queues.forEach { rows -> if(rows.isNotEmpty() && remaining>0 && allowed(manual)) {
                app.downloads.enqueue(rows.removeAt(0)); remaining--; queued++
            } }
        }
        prefs.edit { putInt("sourceCursor",(cursor+1).mod(sources.size)) }
        return queued
    }
    private suspend fun availableBatch(limit: Int): List<Photo> {
        val app=context.applicationContext as MpaApplication
        var after=prefs.getString("photoCursor","").orEmpty()
        var wrapped=false
        val result=ArrayList<Photo>()
        while(result.size<limit) {
            currentCoroutineContext().ensureActive()
            val rows=db.photos().recognitionCandidates(after,120)
            if(rows.isEmpty()) {
                if(result.isNotEmpty() || wrapped || after.isEmpty()) break
                after="";wrapped=true;continue
            }
            for(photo in rows) {
                currentCoroutineContext().ensureActive();after=photo.id
                if(needed(photo) && (photo.originalPath.isNotBlank() && File(photo.originalPath).isFile || app.deviceMedia.readable(photo.id))) result+=photo
                if(result.size==limit) break
            }
            if(rows.size<120) break
        }
        return result
    }
    private suspend fun withRecognitionPreview(photo: Photo,block: suspend (Photo,Bitmap)->Unit) {
        if(photo.originalPath.isNotBlank() && File(photo.originalPath).isFile) cache.withOriginal(photo.id) { current,file ->
            val image=PhotoRepository.preview(file,1600)
            try { block(current,image) } finally { image.recycle() }
        } else (context.applicationContext as MpaApplication).deviceMedia.withPreview(photo.id,block)
    }
    /** A per-photo checksum/model checkpoint survives process death; batches cap runtime and heat. */
    suspend fun index(manual: Boolean=false): Boolean = indexLock.withLock { withContext(Dispatchers.IO) {
        if(!allowed(manual)) { report("Waiting for schedule, charging or phone to cool",0,0,0); return@withContext !settings.state.value.indexingPaused && (settings.state.value.faceIndexing || settings.state.value.objectIndexing) }
        val detector=FaceDetection.getClient(FaceDetectorOptions.Builder().setPerformanceMode(FaceDetectorOptions.PERFORMANCE_MODE_ACCURATE)
            .setLandmarkMode(FaceDetectorOptions.LANDMARK_MODE_ALL).setMinFaceSize(.03f).build())
        val labeler=ImageLabeling.getClient(ImageLabelerOptions.Builder().setConfidenceThreshold(.7f).build())
        var model: FaceEmbedding?=null
        var done=0; var errors=0; var changed=false
        val available=availableBatch(25)
        val started=android.os.SystemClock.elapsedRealtime()
        try {
            if(settings.state.value.faceIndexing) model=FaceEmbedding(context)
            for(photo in available.take(24)) {
                currentCoroutineContext().ensureActive()
                if(!allowed(manual) || android.os.SystemClock.elapsedRealtime()-started>3*60*1000) break
                val scanned=db.library().scan(photo.id)
                val version=(context.applicationContext as MpaApplication).deviceMedia.recognitionVersion(photo)
                val facesNeeded=settings.state.value.faceIndexing && (scanned==null || scanned.modelVersion!=version || scanned.sha256!=photo.sha256)
                val objectsNeeded=settings.state.value.objectIndexing && photo.labels.isBlank()
                report("Scanning on phone",done,available.size,errors)
                try {
                    withRecognitionPreview(photo) { current,image ->
                            val input=InputImage.fromBitmap(image,0)
                            if(objectsNeeded) {
                                val labels=labeler.process(input).await().map { it.text }.distinct().joinToString(", ")
                                if(settings.state.value.objectIndexing) db.photos().find(photo.id)?.let { db.photos().update(it.copy(labels=labels.ifBlank { "Unclassified" })) }
                            }
                            if(facesNeeded && settings.state.value.faceIndexing) {
                                val found=detector.process(input).await()
                                val bounds=found.map { face ->
                                    val rect=face.boundingBox
                                    val x=rect.left.coerceIn(0,image.width-1); val y=rect.top.coerceIn(0,image.height-1)
                                    val r=rect.right.coerceIn(x+1,image.width); val b=rect.bottom.coerceIn(y+1,image.height)
                                    FaceRegion(x.toFloat()/image.width,y.toFloat()/image.height,r.toFloat()/image.width,b.toFloat()/image.height)
                                }
                                val previous=db.library().photoFaces(photo.id)
                                val matches=FaceRegions.matches(bounds,previous)
                                val saved=mutableListOf<FaceRecord>(); val generated=mutableListOf<File>()
                                try {
                                    for((index,face) in found.withIndex()) {
                                        if(!settings.state.value.faceIndexing) break
                                        val old=matches[index]; val id=old?.id ?: UUID.randomUUID().toString()
                                        val region=bounds[index]
                                        val x=(region.left*image.width).toInt().coerceIn(0,image.width-1); val y=(region.top*image.height).toInt().coerceIn(0,image.height-1)
                                        val w=((region.right-region.left)*image.width).toInt().coerceIn(1,image.width-x)
                                        val h=((region.bottom-region.top)*image.height).toInt().coerceIn(1,image.height-y)
                                        val crop=Bitmap.createBitmap(image,x,y,w,h); val small=crop.scale(160,160,true)
                                        // A new filename avoids destroying a previous crop if this batch is cancelled.
                                        val path=File(context.filesDir,"faces/${UUID.randomUUID()}.jpg"); path.parentFile?.mkdirs(); generated.add(path)
                                        try { path.outputStream().use { check(small.compress(Bitmap.CompressFormat.JPEG,85,it)) } }
                                        finally { if(small!==crop) small.recycle(); if(crop!==image) crop.recycle() }
                                        val aligned=model?.aligned(image,face)
                                        val embedding=if(aligned==null) null else try { model.embed(aligned) } finally { aligned.recycle() }
                                        val base=old ?: FaceRecord(id,photo.id,"",null)
                                        saved.add(base.copy(cropPath=path.absolutePath,embedding=embedding,left=region.left,top=region.top,
                                            right=region.right,bottom=region.bottom,imageWidth=image.width,imageHeight=image.height,modelVersion=MODEL_VERSION))
                                    }
                                    if(settings.state.value.faceIndexing && allowed(manual)) db.withTransaction {
                                        val latestPhoto=db.photos().find(photo.id) ?: throw java.io.IOException("This photo was removed during recognition.")
                                        if(latestPhoto.sha256!=current.sha256 || (context.applicationContext as MpaApplication).deviceMedia.recognitionVersion(latestPhoto)!=version)
                                            throw java.io.IOException("This photo changed during recognition. Refresh and retry.")
                                        saved.forEach { record ->
                                            val latest=db.library().face(record.id)
                                            db.library().saveFace(if(latest==null) record else record.copy(personId=latest.personId,
                                                corrected=latest.corrected,suggestedPersonId=null,similarity=null))
                                        }
                                        // Ambiguous or legacy regions stay available for manual review; assignments are never transferred by list order.
                                        db.library().saveScan(FaceScan(photo.id,current.sha256,modelVersion=version))
                                        previous.filter { old -> saved.any { it.id==old.id } }.forEach { if(it.cropPath.isNotBlank()) File(it.cropPath).delete() }
                                    } else { generated.forEach { it.delete() }; throw CancellationException("Indexing paused.") }
                                } catch(e: Exception) { generated.forEach { it.delete() }; throw e }
                            }
                            changed=true
                    }
                } catch(e: CancellationException) { throw e }
                catch(e: java.io.IOException) { errors++; db.library().saveScan(FaceScan(photo.id,photo.sha256,e.message ?: "Recognition needs retry.",version)) }
                catch(e: com.google.mlkit.common.MlKitException) { errors++; db.library().saveScan(FaceScan(photo.id,photo.sha256,e.message ?: "Recognition needs retry.",version)) }
                done++
                prefs.edit { putString("photoCursor",photo.id) }
            }
            if(settings.state.value.faceIndexing) refreshSuggestions()
            if(changed) (context.applicationContext as MpaApplication).metadata.changed()
            val queued=queueCloud(manual)
            val more=available.size>done
            if(!more) prefs.edit { putString("photoCursor","") }
            report(if(more) "More photos queued" else if(queued>0) "Waiting for $queued verified downloads" else "Available photos indexed",done,available.size,errors)
            more && !settings.state.value.indexingPaused
        } finally { detector.close(); labeler.close(); model?.close() }
    } }
    suspend fun refreshSuggestions() {
        val faces=db.library().allFaces()
        val members=faces.map { it.photoId }.distinct().chunked(500).flatMap { db.personLabels().membersForPhotos(it) }
        val suggestions=LabelFaceSuggestions().suggest(faces,members,MODEL_VERSION)
        db.withTransaction { faces.filter { it.personId==null }.forEach { f ->
            val suggestion=suggestions[f.id]
            val latest=db.library().face(f.id)
            if(latest!=null && latest.personId==null && !latest.corrected && latest.modelVersion==f.modelVersion &&
                latest.embedding.contentEquals(f.embedding)) if(latest.suggestedPersonId!=suggestion?.personId || latest.similarity!=suggestion?.similarity)
                    db.library().saveFace(latest.copy(suggestedPersonId=suggestion?.personId,similarity=suggestion?.similarity))
        } }
    }
    suspend fun assign(faceIds: List<String>,person: Person,allowCreate: Boolean=true) {
        require(faceIds.isNotEmpty() && faceIds.distinct().size==faceIds.size)
        db.withTransaction {
            val existing=db.personLabels().person(person.id)
            if(existing==null && !allowCreate) throw java.io.IOException("This person is no longer available. Choose a person again.")
            val selected=faceIds.map { id -> db.library().face(id) ?: throw java.io.IOException("A selected face is no longer available. Close the chooser and refresh People.") }
            db.library().savePerson(existing ?: person)
            selected.forEach {
                db.library().saveFace(it.copy(personId=person.id,suggestedPersonId=null,similarity=null,corrected=true))
            }
        }
        refreshSuggestions()
    }
    suspend fun unassign(id: String) { db.library().face(id)?.let {
        db.library().saveFace(it.copy(personId=null,suggestedPersonId=null,similarity=null,corrected=true))
    } }
    suspend fun merge(source: String,target: String) = db.withTransaction {
        require(source!=target)
        if(db.personLabels().person(source)==null || db.personLabels().person(target)==null) throw java.io.IOException("This person is no longer available. Close the menu and choose again.")
        db.personLabels().mergeAliases(source,target);db.personLabels().mergeMembers(source,target)
        db.library().mergePeople(source,target)
        db.library().clearSuggestions(source); db.library().deletePerson(source)
    }
    suspend fun deletePerson(id: String) = db.withTransaction {
        db.personLabels().ignorePerson(id);db.personLabels().clearPerson(id)
        db.library().unassignPerson(id); db.library().clearSuggestions(id); db.library().deletePerson(id)
    }
    suspend fun deleteIndex() {
        settings.update(settings.state.value.copy(faceIndexing=false))
        WorkManager.getInstance(context).cancelUniqueWork("mpa-local-index")
        WorkManager.getInstance(context).cancelUniqueWork("mpa-night-index")
        indexLock.withLock {
            cache.lock.withLock {
                db.withTransaction { db.library().deleteFaces(); db.library().deleteScans() }
                File(context.filesDir,"faces").deleteRecursively()
            }
        }
        if(settings.state.value.objectIndexing) schedule()
    }
    companion object {
        const val MODEL_VERSION="mobilefacenet-2046ab77-align-v1"
        const val SCAN_VERSION="mlkit-16.1.7-upright1600-"+MODEL_VERSION
    }

}
class PeopleWorker(context: Context,params: WorkerParameters): CoroutineWorker(context,params) {
    override suspend fun doWork(): Result {
        val app=applicationContext as MpaApplication
        return try {
            val more=app.people.index(inputData.getBoolean("manual",false))
            app.cache.enforce()
            if(more) Result.retry() else Result.success()
        }
        catch(e: CancellationException) { throw e }
        catch(_: java.io.IOException) { Result.failure() }
        catch(_: android.database.sqlite.SQLiteException) { Result.failure() }
        catch(_: com.google.mlkit.common.MlKitException) { Result.failure() }
    }
}

data class IndexProgress(val status: String,val done: Int,val total: Int,val errors: Int)

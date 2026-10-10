package com.myphotosanywhere.mpa.metadata

import android.content.Context
import androidx.room.withTransaction
import androidx.work.*
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.people.FaceRegion
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.File
import java.util.concurrent.TimeUnit

data class MetadataPreview(val key: String,val remoteId: String,val etag: String,val xml: String,
    val matching: Int,val unavailable: Int,val people: Int,val albums: Int,val globalNameChanges: Int,
    val localRevision: String,val nameChanges: List<String>)

class MetadataRepository(private val context: Context,private val database: PhotoDatabase?=null) {
    private val app get()=context.applicationContext as MpaApplication
    private val db get()=database ?: app.database
    private val lock=Mutex()
    suspend fun changed() {
        db.withTransaction { db.drive().sources().forEach {
            db.metadata().insert(MetadataState(it.key)); db.metadata().dirty(it.key)
        } }
        schedule()
    }
    suspend fun schedule() {
        val manager=WorkManager.getInstance(context)
        if(!app.settings.state.value.sidecarBackup) { manager.cancelAllWorkByTag(TAG); return }
        for(source in db.drive().sources()) {
            val constraints=Constraints.Builder().setRequiredNetworkType(if(source.unmeteredOnly) NetworkType.UNMETERED else NetworkType.CONNECTED).build()
            val data=workDataOf("binding" to source.key)
            manager.enqueueUniquePeriodicWork("mpa-metadata-periodic:${source.key}",ExistingPeriodicWorkPolicy.UPDATE,
                PeriodicWorkRequestBuilder<MetadataWorker>(15,TimeUnit.MINUTES).setConstraints(constraints).setInputData(data).addTag(TAG).build())
            manager.enqueueUniqueWork("mpa-metadata:${source.key}",ExistingWorkPolicy.KEEP,
                OneTimeWorkRequestBuilder<MetadataWorker>().setConstraints(constraints).setInputData(data).addTag(TAG).build())
        }
    }
    private fun base(key: String)=File(app.filesDir,"metadata/$key.xmp")
    private fun saveBase(key: String,xml: String) {
        val file=base(key); file.parentFile?.mkdirs(); val stage=File(file.parentFile,"$key.part")
        stage.outputStream().use { it.write(xml.toByteArray(Charsets.UTF_8)); it.fd.sync() }
        check(stage.renameTo(file)) { "Cannot checkpoint XMP metadata." }
    }
    /** Only verified links in this source are projected; overlapping source aliases share the local identity pool. */
    suspend fun snapshot(source: DriveSource): XmpCatalog = db.withTransaction {
        val photos=db.photos().all().associateBy { it.id }
        val rows=db.cloud().photos(source.key).filter { !it.removed && it.photoId!=null }.associateBy { it.remoteId }
        val links=rows.values.mapNotNull { row -> photos[row.photoId]?.takeIf { it.bytes==row.bytes }?.let { row.remoteId to it } }.toMap().toMutableMap()
        db.drive().backups(source.key).filter { it.state=="COMPLETE" && it.remoteId!=null }.forEach { task ->
            // An external revision or deletion must not put stale annotations back into the live source.
            val photo=photos[task.photoId]
            val indexed=db.cloud().photo("${source.key}:${task.remoteId}")
            if(photo!=null && photo.sourceRemoteId==null && indexed==null) links[requireNotNull(task.remoteId)]=photo
        }
        val faces=db.library().allFaces(); val people=db.library().allPeople()
        val albums=db.library().allAlbums(); val memberships=db.library().allMemberships()
        val assets=links.map { (remote,photo) -> PortableAsset(photo.metadataAssetId ?: photo.id,remote,photo.sha256,photo.bytes,
            photo.favorite,photo.archived,photo.placeLabel,photo.latitude,photo.longitude,photo.capturedAt,
            photo.parentId?.let { photos[it]?.metadataAssetId ?: it },photo.editProvider,photo.editInstructions,photo.labels,
            db.library().draft(photo.id)?.recipe,memberships.filter { it.photoId==photo.id }.map { it.albumId },
            faces.filter { it.photoId==photo.id }.map { PortableFace(it.id,it.personId,it.corrected,FaceRegion.of(it),it.imageWidth,it.imageHeight,it.modelVersion) }) }
        val personIds=assets.flatMap { it.faces }.mapNotNull { it.personId }.toSet()
        val albumIds=assets.flatMap { it.albums }.toSet()
        XmpCatalog(source.accountId,source.folderId,assets,people.filter { it.id in personIds }.map { PortablePerson(it.id,it.name) },
            albums.filter { it.id in albumIds }.map { PortableAlbum(it.id,it.name) })
    }
    private fun checkCatalog(catalog: XmpCatalog,source: DriveSource) {
        require(catalog.accountId==source.accountId && catalog.folderId==source.folderId) { "This XMP catalog belongs to another Drive source." }
    }
    private suspend fun remote(api: DriveGateway,source: DriveSource,id: String): DriveFile {
        val file=api.file(id)
        require(!file.trashed && file.name==".mpa-catalog.xmp" && file.mimeType=="application/rdf+xml" && source.folderId in file.parents) {
            "The XMP catalog was renamed, moved or replaced. Metadata is preserved; review it in Drive."
        }
        return file
    }
    suspend fun upload(key: String) = lock.withLock { withContext(Dispatchers.IO) {
        if(!app.settings.state.value.sidecarBackup) return@withContext
        val source=db.drive().source(key) ?: return@withContext
        db.metadata().insert(MetadataState(key))
        val state=requireNotNull(db.metadata().state(key))
        if(state.generation==state.uploadedGeneration) return@withContext
        val api=app.sync.api(source.connection())
        val root=api.file(source.folderId)
        require(root.canAddChildren && !root.trashed) { "This source is read-only. Annotations remain on this phone; choose a writable folder for backups." }
        val files=api.metadataFiles(source.folderId)
        var oldXml: String?=null; var tag: String?=null
        val id=if(files.isEmpty()) {
            require(state.etag==null) { "The previously backed-up XMP is missing. Restore it in Drive or connect a new folder; local annotations are preserved." }
            state.remoteId ?: api.generateId().also { db.metadata().reserve(key,it) }
        } else {
            val file=files.single(); remote(api,source,file.id)
            require(state.remoteId==file.id && state.etag!=null && base(key).isFile) {
                "An XMP catalog already exists. Preview and recover its annotations before enabling backup on this phone."
            }
            val read=api.readMetadata(file.id)
            require(read.second==state.etag && read.first==base(key).readText()) {
                "Another device changed this XMP catalog. Preview recovery to review those changes before backing up."
            }
            oldXml=read.first; tag=read.second; file.id
        }
        var catalog=snapshot(source)
        // Keep annotations for media that has not been downloaded on this phone yet.
        if(oldXml!=null) {
            val previous=XmpCodec.decode(requireNotNull(oldXml)); checkCatalog(previous,source)
            val untouched=previous.assets.filter { old -> catalog.assets.none { it.remoteId==old.remoteId } }
            val assets=catalog.assets+untouched
            val personIds=assets.flatMap { it.faces }.mapNotNull { it.personId }.toSet(); val albumIds=assets.flatMap { it.albums }.toSet()
            catalog=catalog.copy(assets=assets,people=(catalog.people+previous.people).distinctBy { it.id }.filter { it.id in personIds },
                albums=(catalog.albums+previous.albums).distinctBy { it.id }.filter { it.id in albumIds })
        }
        val xml=XmpCodec.encode(catalog,oldXml)
        currentCoroutineContext().ensureActive()
        require(app.settings.state.value.sidecarBackup && db.drive().source(key)!=null) { "Annotation backup paused." }
        if(tag!=null) remote(api,source,id)
        if(xml==oldXml && tag!=null) { db.metadata().uploaded(key,id,tag,state.generation); return@withContext }
        api.writeMetadata(id,source.folderId,xml,tag)
        // Read the authoritative revision back. If a concurrent writer won afterwards, retain our dirty outbox.
        remote(api,source,id)
        val written=api.readMetadata(id)
        require(written.first==xml) { "The XMP changed after upload. Local annotations are preserved; preview recovery." }
        saveBase(key,xml)
        db.metadata().uploaded(key,id,written.second,state.generation)
    } }
    private suspend fun matching(source: DriveSource,catalog: XmpCatalog): Map<String,Photo> {
        val local=snapshot(source)
        val photos=db.photos().all().associateBy { it.metadataAssetId ?: it.id }
        return catalog.assets.mapNotNull { asset ->
            val current=local.assets.find { it.remoteId==asset.remoteId && it.sha256==asset.sha256 && it.bytes==asset.bytes }
            current?.let { photos[it.id] }?.let { asset.id to it }
        }.toMap()
    }
    private fun fingerprint(catalog: XmpCatalog): String = java.security.MessageDigest.getInstance("SHA-256")
        .digest(XmpCodec.encode(catalog).toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
    suspend fun preview(key: String): MetadataPreview = lock.withLock { withContext(Dispatchers.IO) {
        val source=db.drive().source(key) ?: error("Reconnect this Drive source.")
        val api=app.sync.api(source.connection()); val file=api.metadataFiles(source.folderId).singleOrNull() ?: error("No MPA XMP catalog found in this folder.")
        remote(api,source,file.id); val read=api.readMetadata(file.id); val catalog=XmpCodec.decode(read.first); checkCatalog(catalog,source)
        val matches=matching(source,catalog)
        val referenced=catalog.assets.filter { it.id in matches }.flatMap { it.faces }.mapNotNull { it.personId }.toSet()
        val names=db.library().allPeople().associateBy { it.id }
        MetadataPreview(key,file.id,read.second,read.first,matches.size,catalog.assets.size-matches.size,
            referenced.size,catalog.assets.filter { it.id in matches }.flatMap { it.albums }.distinct().size,
            catalog.people.count { it.id in referenced && names[it.id]!=null && names[it.id]?.name!=it.name },
            fingerprint(snapshot(source)),catalog.people.filter { it.id in referenced && names[it.id]!=null && names[it.id]?.name!=it.name }
                .map { "${names[it.id]?.name} → ${it.name}" })
    } }
    suspend fun recover(preview: MetadataPreview) = lock.withLock { withContext(Dispatchers.IO) {
        val source=db.drive().source(preview.key) ?: error("Reconnect this Drive source.")
        val api=app.sync.api(source.connection()); remote(api,source,preview.remoteId)
        val read=api.readMetadata(preview.remoteId)
        require(read.second==preview.etag && read.first==preview.xml) { "The XMP changed since the preview. Preview recovery again." }
        val catalog=XmpCodec.decode(read.first); checkCatalog(catalog,source)
        val acceptedGeneration=app.cache.lock.withLock { applyCatalog(source,catalog,preview.matching,preview.localRevision) }
        saveBase(preview.key,read.first)
        // Recovery explicitly accepts this remote baseline; subsequent local edits can be uploaded conditionally.
        db.metadata().uploaded(preview.key,preview.remoteId,read.second,acceptedGeneration)
        app.people.schedule(); app.people.refreshSuggestions()
    } }
    internal suspend fun applyCatalog(source: DriveSource,catalog: XmpCatalog,expectedMatching: Int,expectedRevision: String?=null): Long = db.withTransaction {
            checkCatalog(catalog,source)
            if(expectedRevision!=null) require(fingerprint(snapshot(source))==expectedRevision) { "Local annotations changed since the preview. Preview recovery again." }
            require(db.drive().source(source.key)!=null)
            val matches=matching(source,catalog)
            require(matches.size==expectedMatching) { "The local library changed. Preview recovery again." }
            val assets=catalog.assets.filter { it.id in matches }
            val personIds=assets.flatMap { it.faces }.mapNotNull { it.personId }.toSet(); val albumIds=assets.flatMap { it.albums }.toSet()
            catalog.people.filter { it.id in personIds }.forEach { db.library().savePerson(Person(it.id,it.name)) }
            val existingAlbums=db.library().allAlbums().associateBy { it.id }
            catalog.albums.filter { it.id in albumIds }.forEach { a ->
                db.library().saveAlbum(existingAlbums[a.id]?.copy(name=a.name) ?: Album(a.id,a.name,System.currentTimeMillis()))
            }
            val allMemberships=db.library().allMemberships()
            for(asset in assets) {
                val photo=requireNotNull(matches[asset.id])
                val duplicate=db.photos().all().any { it.id!=photo.id && (it.metadataAssetId ?: it.id)==asset.id }
                require(!duplicate) { "Two local assets claim the same portable identity. Resolve the duplicate before recovery." }
                db.photos().update(photo.copy(metadataAssetId=asset.id,favorite=asset.favorite,archived=asset.archived,
                    placeLabel=asset.place,latitude=asset.latitude,longitude=asset.longitude,capturedAt=asset.capturedAt,
                    parentId=asset.parentId?.let { parent -> matches[parent]?.id ?: db.photos().all().find { (it.metadataAssetId ?: it.id)==parent }?.id },editProvider=asset.provider,editInstructions=asset.instructions,labels=asset.labels))
                allMemberships.filter { it.photoId==photo.id }.forEach { db.library().removeFromAlbum(it.albumId,photo.id) }
                asset.albums.forEach { db.library().addToAlbum(AlbumPhoto(it,photo.id)) }
                if(asset.recipe==null) db.library().deleteDraft(photo.id)
                else db.library().saveDraft(EditDraft(photo.id,asset.recipe,System.currentTimeMillis()))
                val oldFaces=db.library().allFaces().filter { it.photoId==photo.id }.associateBy { it.id }
                db.library().deletePhotoFaces(photo.id)
                for(face in asset.faces) {
                    val old=oldFaces[face.id] ?: db.library().face(face.id)
                    require(old==null || old.photoId==photo.id) { "Face identity collision; no annotations were changed." }
                    // Preserve embeddings only for the same face. Portable files never supply biometric vectors.
                    val region=face.region
                    db.library().saveFace(FaceRecord(face.id,photo.id,old?.cropPath ?: "",old?.embedding?.takeIf { old.modelVersion==face.model && FaceRegion.of(old)==face.region },face.personId,
                        corrected=face.corrected,left=region?.left,top=region?.top,right=region?.right,bottom=region?.bottom,
                        imageWidth=face.width,imageHeight=face.height,modelVersion=face.model))
                }
                // Regenerate crops and embeddings using geometric matches on a later phone scan.
                db.library().saveScan(FaceScan(photo.id,photo.sha256,modelVersion="restored"))
            }
            db.metadata().insert(MetadataState(source.key)); db.metadata().dirty(source.key)
            requireNotNull(db.metadata().state(source.key)).generation
    }

    companion object { const val TAG="mpa-xmp-backup" }
}
class MetadataWorker(context: Context,params: WorkerParameters): CoroutineWorker(context,params) {
    override suspend fun doWork(): Result {
        val app=applicationContext as MpaApplication; val key=inputData.getString("binding") ?: return Result.failure()
        return try { app.metadata.upload(key); Result.success() }
        catch(e: CancellationException) { throw e }
        catch(e: Exception) {
            app.database.metadata().error(key,if(e is DriveHttpException && e.status==412)
                "Another device changed the XMP. Preview recovery before backing up." else e.message ?: "XMP backup failed. Local annotations are preserved.")
            if(e is java.io.IOException && (e !is DriveHttpException || e.retryable)) Result.retry() else Result.success()
        }
    }
}

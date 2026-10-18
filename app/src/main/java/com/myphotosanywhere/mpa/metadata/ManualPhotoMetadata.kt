package com.myphotosanywhere.mpa.metadata

import androidx.room.withTransaction
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.people.FaceRegion
import java.io.IOException
import java.security.MessageDigest

/** Explicit per-photo backup includes portable annotations without enabling library-wide backup. */
internal class ManualPhotoMetadata(private val db: PhotoDatabase) {
    internal suspend fun snapshot(task: DriveBackup): XmpCatalog = db.withTransaction {
        val photo=checkNotNull(db.photos().find(task.photoId)) { "A leased backup photo disappeared." }
        val faces=db.library().photoFaces(photo.id)
        val albums=db.library().photoAlbums(photo.id)
        val members=db.personLabels().members(photo.id)
        val origin=db.personLabels().origin(photo.id)
        val retainedNames=try { com.myphotosanywhere.mpa.people.ImportedPeopleCodec().names(photo.importedMetadata) }
        catch(error: com.google.gson.JsonParseException) { throw DriveBackupBlocked("The original is verified in Drive, but its retained people metadata needs repair: ${error.message}") }
        catch(error: IllegalArgumentException) { throw DriveBackupBlocked("The original is verified in Drive, but its retained people metadata is unsupported: ${error.message}") }
        val ignored=retainedNames.mapNotNull { name ->
            db.personLabels().alias(com.myphotosanywhere.mpa.people.ImportedPersonLabel(name,if(origin!=null) origin.binding else photo.sourceBindingKey).key)?.takeIf { it.ignored }
        }
        val aliases=(members.map { checkNotNull(db.personLabels().alias(it.aliasKey)) { "Photo association has no provider alias." } }+ignored).distinctBy { it.key }.sortedBy { it.key }
        val ids=(faces.mapNotNull { it.personId }+members.map { it.personId }+aliases.mapNotNull { it.personId }).toSortedSet()
        val people=ids.map { id -> checkNotNull(db.personLabels().person(id)) { "A photo association references a missing person." } }
        val parent=photo.parentId?.let { db.photos().find(it)?.metadataAssetId ?: it }
        val asset=PortableAsset(photo.metadataAssetId ?: photo.id,checkNotNull(task.remoteId),photo.sha256,photo.bytes,
            photo.favorite,photo.archived,photo.placeLabel,photo.latitude,photo.longitude,photo.capturedAt,
            parent,photo.editProvider,photo.editInstructions,photo.labels,db.library().draft(photo.id)?.recipe,
            albums.map { it.id },faces.map { PortableFace(it.id,it.personId,it.corrected,FaceRegion.of(it),it.imageWidth,it.imageHeight,it.modelVersion) },
            photo.importedMetadata,if(origin==null && members.isEmpty()) null else PortablePhotoPeople(origin?.binding,members.map { PortableMembership(it.aliasKey,it.personId,it.excluded) }))
        XmpCatalog(task.accountId,task.folderId,listOf(asset),people.map { PortablePerson(it.id,it.name) },albums.map { PortableAlbum(it.id,it.name) },
            aliases.map { PortableAlias(it.key,it.name,it.binding,it.personId,it.ignored) })
    }

    /** Reserve the sidecar ID and expected payload before writing. A lost response never duplicates it. */
    suspend fun backup(api: DriveGateway,task: DriveBackup) {
        var receipt=checkNotNull(db.deviceMedia().authorization(task.id))
        check(receipt.metadataRequested)
        val xml=try { XmpCodec.encode(snapshot(task)) }
        catch(error: IllegalArgumentException) { throw DriveBackupBlocked("The original is verified in Drive, but its annotations could not be encoded: ${error.message}") }
        val sha=hash(xml)
        val id=receipt.metadataId ?: api.generateId().also {
            receipt=receipt.copy(metadataId=it,metadataSha=sha)
            db.deviceMedia().updateAuthorization(receipt)
        }
        val name="${checkNotNull(task.remoteId)}.mpa.xmp"
        val remote=try { api.file(id) } catch(error: DriveHttpException) { if(error.status==404) null else throw error }
        var tag: String?=null
        if(remote!=null) {
            if(remote.trashed || task.folderId !in remote.parents || remote.name!=name || remote.mimeType!="application/rdf+xml")
                throw DriveBackupBlocked("The backup annotation file was moved or replaced. Original and annotations are kept; review it in Drive.")
            val previous=read(api,id)
            if(hash(previous.first)!=receipt.metadataSha && previous.second!=receipt.metadataEtag)
                throw DriveBackupBlocked("Another device changed this photo's annotations. Original and local annotations are kept; review them before retrying.")
            tag=previous.second
            if(previous.first==xml) {
                db.deviceMedia().updateAuthorization(receipt.copy(metadataSha=sha,metadataEtag=tag))
                return
            }
        } else if(receipt.metadataEtag!=null) throw DriveBackupBlocked("The backed-up annotations are missing. Restore them in Drive before retrying; the original is kept.")
        // Keep both the last accepted ETag and intended digest. A lost write response can then
        // acknowledge the intended bytes or retry against the unchanged prior revision.
        if(receipt.metadataSha!=sha) {
            receipt=receipt.copy(metadataSha=sha);db.deviceMedia().updateAuthorization(receipt)
        }
        api.writeMetadata(id,task.folderId,xml,tag,name)
        val written=api.file(id)
        if(written.trashed || task.folderId !in written.parents || written.name!=name || written.mimeType!="application/rdf+xml")
            throw IOException("The annotation destination changed after upload. The verified original is kept.")
        val verified=read(api,id)
        if(verified.first!=xml) throw DriveBackupBlocked("Drive annotation verification failed. The original and local annotations are kept.")
        db.deviceMedia().updateAuthorization(receipt.copy(metadataSha=sha,metadataEtag=verified.second))
    }

    private suspend fun read(api: DriveGateway,id: String): Pair<String,String> = try { api.readMetadata(id) }
    catch(error: IllegalArgumentException) { throw DriveBackupBlocked("Drive returned unsupported annotation data. The original and local annotations are kept. ${error.message.orEmpty()}") }
    catch(error: IllegalStateException) { throw DriveBackupBlocked("Drive did not provide a verifiable annotation revision. The original and local annotations are kept. ${error.message.orEmpty()}") }

    private fun hash(xml: String)=MessageDigest.getInstance("SHA-256").digest(xml.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
}

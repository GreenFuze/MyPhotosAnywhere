package com.myphotosanywhere.mpa.device

import androidx.core.net.toUri
import android.Manifest
import android.content.ContentResolver
import android.content.ContentUris
import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.CancellationSignal
import android.provider.MediaStore
import androidx.core.content.ContextCompat
import androidx.room.withTransaction
import com.myphotosanywhere.mpa.data.*
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.IOException
import java.util.UUID

internal data class DevicePhotoAccess(val images: Boolean,val videos: Boolean,val selected: Boolean) {
    val granted get()=images || videos || selected
    val full get()=images && videos
}

/** Permission truth comes from Android every time; it is never persisted as a grant. */
internal class DevicePhotoPermissions(private val context: Context) {
    fun current(): DevicePhotoAccess {
        fun granted(permission: String)=ContextCompat.checkSelfPermission(context,permission)==PackageManager.PERMISSION_GRANTED
        if(Build.VERSION.SDK_INT<33) {
            val readable=granted(Manifest.permission.READ_EXTERNAL_STORAGE)
            return DevicePhotoAccess(readable,readable,false)
        }
        return DevicePhotoAccess(granted(Manifest.permission.READ_MEDIA_IMAGES),granted(Manifest.permission.READ_MEDIA_VIDEO),
            Build.VERSION.SDK_INT>=34 && granted(Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED))
    }
    fun request(): Array<String> = when {
        Build.VERSION.SDK_INT>=34 -> arrayOf(Manifest.permission.READ_MEDIA_IMAGES,Manifest.permission.READ_MEDIA_VIDEO,Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED)
        Build.VERSION.SDK_INT>=33 -> arrayOf(Manifest.permission.READ_MEDIA_IMAGES,Manifest.permission.READ_MEDIA_VIDEO)
        else -> arrayOf(Manifest.permission.READ_EXTERNAL_STORAGE)
    }
}

/** Metadata only, in bounded keyset batches. No camera originals are copied for gallery discovery. */
internal class DeviceMediaLibrary(private val context: Context,private val database: PhotoDatabase) {
    val permissions=DevicePhotoPermissions(context)
    private val indexing=Mutex()
    private val materializing=Mutex()
    private val staging=DeviceOriginalStaging(java.io.File(context.filesDir,"device-original-staging"))
    private val resolver=context.contentResolver
    private data class Row(val id: Long,val name: String,val mime: String,val bytes: Long,val added: Long,
        val captured: Long?,val modified: Long,val generation: Long,val width: Int,val height: Int,val orientation: Int)
    suspend fun reconcile(): Int=withContext(Dispatchers.IO) { indexing.withLock {
        val access=permissions.current()
        if(!access.granted) { database.deviceMedia().revoke();return@withLock 0 }
        val seen=UUID.randomUUID().toString()
        val volumes=if(Build.VERSION.SDK_INT>=29) MediaStore.getExternalVolumeNames(context).sorted() else listOf("external")
        var count=0
        for(volume in volumes) {
            currentCoroutineContext().ensureActive()
            val collection=MediaStore.Files.getContentUri(volume)
            val version=version(volume)
            var after=-1L
            while(true) {
                val rows=query(collection,after,access)
                if(rows.isEmpty()) break
                val prepared=rows.map { row -> prepare(collection,version,volume,row) }
                database.withTransaction {
                    for(item in prepared) {
                        currentCoroutineContext().ensureActive()
                        val row=item.row;val reference=item.reference
                        val latest=database.photos().find(reference.photoId)
                        if(latest!=null && latest.sha256!=item.previousSha) throw DeviceIndexChanged()
                        var changed=false
                        if(latest==null) { database.photos().insert(Photo(reference.photoId,row.name,"",reference.uri,row.mime,row.bytes,row.added,"",capturedAt=row.captured));changed=true }
                        else {
                            val previousCapture=item.previousReference?.let { captured(it.revision) }
                            val capture=if(latest.capturedAt==previousCapture) row.captured else latest.capturedAt
                            val updated=latest.copy(displayName=row.name,capturedAt=capture)
                            if(updated!=latest) { database.photos().update(updated);changed=true }
                        }
                        database.deviceMedia().supersede(reference.uri,reference.photoId)
                        if(database.deviceMedia().find(reference.photoId)!=reference) { database.deviceMedia().save(reference);changed=true }
                        database.deviceMedia().seen(DeviceScanSeen(reference.photoId,seen))
                        if(changed) count++
                    }
                }
                after=rows.last().id
                if(rows.size<BATCH) break
            }
            if(version(volume)!=version) throw IOException("Android refreshed its media library. Retry device-photo discovery.")
        }
        if(permissions.current()!=access) throw IOException("Photo access changed. Refresh the device library.")
        database.deviceMedia().finish(seen)
        count
    } }
    private fun version(volume: String): String = when {
        Build.VERSION.SDK_INT>=30 -> MediaStore.getVersion(context,volume)
        Build.VERSION.SDK_INT>=29 -> MediaStore.getVersion(context)
        else -> "legacy-media-provider"
    }
    private suspend fun query(collection: Uri,after: Long,access: DevicePhotoAccess): List<Row> = coroutineScope {
        val generation=Build.VERSION.SDK_INT>=30
        val columns=mutableListOf("_id","_display_name","mime_type","_size","date_added","datetaken","date_modified","width","height","orientation")
        if(generation) columns+="generation_modified"
        val kinds=buildList { if(access.images || access.selected) add(MediaStore.Files.FileColumns.MEDIA_TYPE_IMAGE);if(access.videos || access.selected) add(MediaStore.Files.FileColumns.MEDIA_TYPE_VIDEO) }
        val selection=buildString {
            append("_id>? AND media_type IN (${kinds.joinToString(",")}) AND _size>0")
            if(Build.VERSION.SDK_INT>=29) append(" AND is_pending=0")
            if(Build.VERSION.SDK_INT>=30) append(" AND is_trashed=0")
        }
        val args=Bundle().apply {
            putString(ContentResolver.QUERY_ARG_SQL_SELECTION,selection)
            putStringArray(ContentResolver.QUERY_ARG_SQL_SELECTION_ARGS,arrayOf(after.toString()))
            putString(ContentResolver.QUERY_ARG_SQL_SORT_ORDER,"_id ASC")
            putInt(ContentResolver.QUERY_ARG_LIMIT,BATCH)
        }
        val signal=CancellationSignal()
        val cancellation=launch(start=CoroutineStart.UNDISPATCHED) {
            suspendCancellableCoroutine<Nothing> { continuation -> continuation.invokeOnCancellation { signal.cancel() } }
        }
        try {
            resolver.query(collection,columns.toTypedArray(),args,signal)?.use { cursor ->
                buildList {
                    while(cursor.moveToNext() && size<BATCH) {
                        currentCoroutineContext().ensureActive()
                        val added=cursor.getLong(4).coerceIn(0,Long.MAX_VALUE/1000)*1000
                        add(Row(cursor.getLong(0),cursor.getString(1) ?: "Photo",cursor.getString(2) ?: "application/octet-stream",
                            cursor.getLong(3),added,cursor.getLong(5).takeIf { it>0 },cursor.getLong(6),if(generation) cursor.getLong(10) else 0,
                            cursor.getInt(7).coerceAtLeast(0),cursor.getInt(8).coerceAtLeast(0),cursor.getInt(9)))
                    }
                }
            } ?: throw IOException("Android did not return its device-photo library. Retry.")
        } catch(error: android.os.OperationCanceledException) {
            currentCoroutineContext().ensureActive();throw IOException("Android cancelled device-photo discovery. Retry.",error)
        } catch(error: SecurityException) { throw IOException("Photo access is no longer available. Manage Android photo access.",error) }
        finally { cancellation.cancel() }
    }
    fun observe(id: String)=database.deviceMedia().observe(id)
    suspend fun record(id: String)=withContext(Dispatchers.IO) { database.deviceMedia().find(id) }
    private class Prepared(val row: Row,val reference: DeviceMediaRecord,val previousReference: DeviceMediaRecord?,val previousSha: String?)
    private suspend fun prepare(collection: Uri,version: String,volume: String,row: Row): Prepared {
        val uri=ContentUris.withAppendedId(collection,row.id).toString();val fingerprint=revision(row)
        val matches=database.deviceMedia().byUri(uri)
        val exact=matches.firstOrNull { it.providerVersion==version && it.revision==fingerprint }
        val content=tuple(row.bytes,row.modified,row.added,row.mime)
        var id=exact?.photoId ?: "device-"+UUID.nameUUIDFromBytes("$uri|$version|$content".toByteArray(Charsets.UTF_8))
        var previous=database.photos().find(id);var old=database.deviceMedia().find(id)
        var reference=DeviceMediaRecord(id,uri,volume,version,fingerprint,row.width,row.height,row.orientation,true)
        if(previous!=null && previous.sha256.isNotBlank() && old?.revision!=fingerprint) {
            // Hash only a changed, previously verified device reference. Initial gallery
            // discovery and ordinary metadata-only media never hash the camera roll.
            val digest=java.security.MessageDigest.getInstance("SHA-256")
            android.os.ParcelFileDescriptor.AutoCloseInputStream(openDescriptor(reference)).use { input ->
                val buffer=ByteArray(64*1024);var total=0L
                while(true) {
                    currentCoroutineContext().ensureActive();val size=input.read(buffer);if(size<0) break
                    total+=size;if(total>row.bytes) throw IOException("The device original changed during verification. Refresh and retry.")
                    digest.update(buffer,0,size)
                }
                if(total!=row.bytes) throw IOException("The changed device original was incomplete. Refresh and retry.")
            }
            current(reference)
            val sha=digest.digest().joinToString("") { "%02x".format(it) }
            if(sha!=previous.sha256) {
                id="device-"+UUID.nameUUIDFromBytes("$uri|$version|$content|$sha".toByteArray(Charsets.UTF_8))
                previous=database.photos().find(id);old=database.deviceMedia().find(id)
                reference=reference.copy(photoId=id)
                check(previous==null || previous.sha256.isBlank() || previous.sha256==sha) { "A device content identity references different verified bytes." }
            }
        }
        return Prepared(row,reference,old,previous?.sha256)
    }
    private fun tuple(vararg fields: Any?)=com.google.gson.JsonArray().apply {
        fields.forEach { value -> when(value) {
            null -> add(com.google.gson.JsonNull.INSTANCE)
            is Number -> add(value)
            is String -> add(value)
            else -> error("Unsupported device fingerprint field.")
        } }
    }.toString()
    private fun revision(row: Row)=tuple(row.bytes,row.modified,row.generation,row.added,row.captured,row.mime,row.width,row.height,row.orientation)
    private fun captured(revision: String): Long? {
        val fields=com.google.gson.JsonParser.parseString(revision).asJsonArray
        check(fields.size()==9)
        return fields[4].takeUnless { it.isJsonNull }?.asLong
    }
    private fun current(record: DeviceMediaRecord): Row {
        if(!record.available || !permissions.current().granted) throw IOException("Manage Android photo access to open this device original.")
        if(version(record.volume)!=record.providerVersion) throw IOException("Android refreshed this media source. Refresh device photos before opening it.")
        val generation=Build.VERSION.SDK_INT>=30
        val columns=mutableListOf("_id","_display_name","mime_type","_size","date_added","datetaken","date_modified","width","height","orientation")
        if(generation) columns+="generation_modified"
        val row=resolver.query(record.uri.toUri(),columns.toTypedArray(),null,null,null)?.use { cursor ->
            if(!cursor.moveToFirst()) null else Row(cursor.getLong(0),cursor.getString(1) ?: "Photo",cursor.getString(2) ?: "application/octet-stream",
                cursor.getLong(3),cursor.getLong(4).coerceIn(0,Long.MAX_VALUE/1000)*1000,cursor.getLong(5).takeIf { it>0 },cursor.getLong(6),
                if(generation) cursor.getLong(10) else 0,cursor.getInt(7).coerceAtLeast(0),cursor.getInt(8).coerceAtLeast(0),cursor.getInt(9))
        } ?: throw IOException("This device original is no longer available. Refresh device photos.")
        if(revision(row)!=record.revision) throw IOException("This device photo changed. Refresh the gallery and open its current version.")
        return row
    }
    /** Called only by an IO operation or the single bounded image-decoder thread. */
    fun openDescriptor(record: DeviceMediaRecord): android.os.ParcelFileDescriptor {
        check(android.os.Looper.myLooper()!=android.os.Looper.getMainLooper()) { "Device original IO must stay off the main thread." }
        check(record.uri.toUri().scheme=="content" && record.uri.toUri().authority==MediaStore.AUTHORITY)
        try {
            val row=current(record)
            val descriptor=resolver.openFileDescriptor(record.uri.toUri(),"r") ?: throw IOException("Android could not open this device original.")
            try {
                if(descriptor.statSize>=0 && descriptor.statSize!=row.bytes) throw IOException("The device file changed. Refresh device photos.")
                current(record)
                return descriptor
            } catch(failure: Throwable) { descriptor.close();throw failure }
        } catch(error: SecurityException) { throw IOException("Android photo access changed. Manage device photo access and retry.",error) }
    }
    suspend fun readable(id: String): Boolean=withContext(Dispatchers.IO) {
        val record=database.deviceMedia().find(id) ?: return@withContext false
        try { openDescriptor(record).use { true } }
        catch(_: IOException) { false }
    }
    /** MediaStore may not have image dimensions yet. Read only original bounds, never pixels. */
    internal suspend fun imageDimensions(id: String): Pair<Int,Int>?=withContext(Dispatchers.IO) {
        val reference=database.deviceMedia().find(id) ?: return@withContext null
        val photo=database.photos().find(id) ?: return@withContext null
        if(!photo.mimeType.startsWith("image/")) return@withContext null
        android.os.ParcelFileDescriptor.AutoCloseInputStream(openDescriptor(reference)).use { input ->
            val bounds=android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds=true }
            android.graphics.BitmapFactory.decodeFileDescriptor(input.fd,null,bounds)
            if(bounds.outWidth<=0 || bounds.outHeight<=0) return@use null
            input.channel.position(0)
            val orientation=try {
                androidx.exifinterface.media.ExifInterface(input.fd).getAttributeInt(androidx.exifinterface.media.ExifInterface.TAG_ORIENTATION,1)
            } catch(_: IOException) { 1 }
            currentCoroutineContext().ensureActive();current(reference)
            if(database.deviceMedia().find(id)!=reference) throw IOException("Device photo access changed. Refresh and retry details.")
            if(orientation in 5..8) bounds.outHeight to bounds.outWidth else bounds.outWidth to bounds.outHeight
        }
    }
    /** Recognition owns one bounded bitmap, without materializing every camera original. */
    internal suspend fun withPreview(id: String,block: suspend (Photo,android.graphics.Bitmap)->Unit)=withContext(Dispatchers.IO) {
        val reference=database.deviceMedia().find(id) ?: throw IOException("This device original is no longer indexed.")
        val photo=database.photos().find(id) ?: throw IOException("This device photo is no longer available.")
        openDescriptor(reference).use { descriptor ->
            val image=DevicePhotoPreview().decode(descriptor.fileDescriptor,1600)
            try {
                currentCoroutineContext().ensureActive();current(reference)
                if(database.deviceMedia().find(id)!=reference) throw IOException("Device photo access changed. Refresh and retry recognition.")
                block(photo,image)
            } finally { image.recycle() }
        }
    }
    internal suspend fun videoPreview(id: String): android.graphics.Bitmap=withContext(Dispatchers.IO) {
        val reference=database.deviceMedia().find(id) ?: throw IOException("This device video is no longer indexed.")
        openDescriptor(reference).use { descriptor ->
            val retriever=android.media.MediaMetadataRetriever()
            try {
                retriever.setDataSource(descriptor.fileDescriptor)
                val image=try { PhotoRepository.videoPreview(retriever,480) }
                catch(error: IllegalStateException) { throw IOException("This video has no usable poster. Its original is preserved.",error) }
                try {
                    currentCoroutineContext().ensureActive();current(reference)
                    if(database.deviceMedia().find(id)!=reference) throw IOException("Device video access changed. Refresh and retry.")
                    image
                } catch(failure: Throwable) { image.recycle();throw failure }
            } catch(error: IllegalArgumentException) { throw IOException("This device video cannot provide a preview. Its original is preserved.",error) }
            finally { retriever.release() }
        }
    }
    internal suspend fun recognitionVersion(photo: Photo): String {
        val reference=database.deviceMedia().find(photo.id)
        val base=com.myphotosanywhere.mpa.people.PeopleRepository.SCAN_VERSION
        if(reference==null || photo.originalPath.isNotBlank() && java.io.File(photo.originalPath).isFile) return base
        return base+":device:"+UUID.nameUUIDFromBytes((reference.uri+"|"+reference.providerVersion+"|"+reference.revision).toByteArray(Charsets.UTF_8))
    }
    /** Explicit edit/share/backup work may require a private, verified, non-disposable copy. */
    suspend fun materialize(id: String): Photo=withContext(Dispatchers.IO) { materializing.withLock {
        val app=context.applicationContext as com.myphotosanywhere.mpa.MpaApplication
        val first=database.photos().find(id) ?: throw IOException("This device photo is no longer in the library.")
        if(first.originalPath.isNotBlank() && java.io.File(first.originalPath).isFile) return@withLock first
        val reference=database.deviceMedia().find(id) ?: throw IOException("No device original is available for this photo.")
        val directory=java.io.File(context.filesDir,"originals")
        if(!directory.isDirectory && !directory.mkdirs()) throw IOException("Cannot retain a working original. The camera file is unchanged.")
        val reserve=try { Math.addExact(first.bytes,64L*1024*1024) } catch(error: ArithmeticException) { throw IOException("This device file is too large to prepare safely.",error) }
        try { app.storage.requireBytes(directory,reserve,"More free space is needed for this original. The camera file is unchanged.") }
        catch(error: IllegalArgumentException) { throw IOException(error.message,error) }
        val stage=staging.create()
        val target=java.io.File(directory,"device-"+UUID.nameUUIDFromBytes(id.toByteArray(Charsets.UTF_8)))
        var failure: Throwable?=null
        try {
            val digest=java.security.MessageDigest.getInstance("SHA-256");var total=0L
            android.os.ParcelFileDescriptor.AutoCloseInputStream(openDescriptor(reference)).use { input ->
                stage.outputStream().use { output ->
                    val buffer=ByteArray(64*1024)
                    while(true) {
                        currentCoroutineContext().ensureActive()
                        val size=input.read(buffer);if(size<0) break
                        total+=size
                        if(total>first.bytes) throw IOException("The device original changed during preparation. Refresh and retry.")
                        output.write(buffer,0,size);digest.update(buffer,0,size)
                    }
                    output.fd.sync()
                }
            }
            if(total!=first.bytes) throw IOException("The device original was incomplete. Its camera file is unchanged; retry.")
            current(reference)
            val sha=digest.digest().joinToString("") { "%02x".format(it) }
            app.cache.lock.withLock {
                database.withTransaction {
                    val latest=database.photos().find(id) ?: throw IOException("This photo was removed while its original was prepared.")
                    if(database.deviceMedia().find(id)?.let { it.available && it.uri==reference.uri && it.revision==reference.revision && it.providerVersion==reference.providerVersion }!=true || latest.bytes!=first.bytes) throw IOException("The device photo changed. Refresh and retry.")
                    if(latest.originalPath.isNotBlank() && java.io.File(latest.originalPath).isFile) return@withTransaction latest
                    if(latest.sha256.isNotBlank() && latest.sha256!=sha) throw IOException("The device bytes no longer match this saved identity. The existing record was preserved.")
                    if(target.exists()) {
                        if(target.length()!=total || com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(target,"SHA-256")!=sha)
                            throw IOException("A retained original differs from the device file. Both were preserved for recovery.")
                    } else if(!stage.renameTo(target)) throw IOException("Cannot retain the verified original. The camera file is unchanged.")
                    latest.copy(originalPath=target.path,sha256=sha).also { database.photos().update(it) }
                }
            }
        } catch(error: Throwable) { failure=error;throw error }
        finally { try { staging.remove(stage) } catch(cleanup: IOException) { if(failure!=null) failure.addSuppressed(cleanup) else throw cleanup } }
    } }
    internal class DeviceIndexChanged: IOException("A device original was prepared while discovery was running. Refresh device photos to reconcile the new snapshot.")
    companion object { const val BATCH=120 }
}

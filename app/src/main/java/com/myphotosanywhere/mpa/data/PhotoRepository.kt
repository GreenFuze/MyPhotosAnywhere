package com.myphotosanywhere.mpa.data

import androidx.core.graphics.scale
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.ImageDecoder
import android.graphics.Matrix
import android.net.Uri
import android.os.Build
import android.provider.OpenableColumns
import androidx.exifinterface.media.ExifInterface
import androidx.room.withTransaction
import java.io.File
import java.security.MessageDigest
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

/** Original imports are durable files, never evicted as cache until a backup is verified. */
class PhotoRepository(private val context: Context, private val database: PhotoDatabase) {
    private val dao = database.photos()
    private val importLock = Mutex()
    val photos = dao.observeAll()
    val handoffs = dao.observeHandoffs()

    suspend fun importPhoto(uri: Uri, handoff: Handoff? = null, nameOverride: String? = null,
        mimeOverride: String? = null, onSaved: suspend (Photo) -> Unit = {}): Photo = withContext(Dispatchers.IO) {
        importLock.withLock {
            if (handoff != null) check(dao.find(handoff.photoId) != null) { "The original photo is missing." }
            val resolver = context.contentResolver
            val id = UUID.randomUUID().toString()
            val dir = File(context.filesDir, "originals").apply { mkdirs() }
            val staged = File(dir, "$id.part")
            val original = File(dir, id)
            val thumb = File(context.filesDir, "thumbnails/$id.jpg")
            val name = nameOverride ?: resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
                if (it.moveToFirst()) it.getString(0) else null
            } ?: "Photo"
            try {
                val digest = MessageDigest.getInstance("SHA-256")
                var total = 0L
                resolver.openInputStream(uri)?.use { input ->
                    staged.outputStream().use { output ->
                        val buffer = ByteArray(64 * 1024)
                        while (true) {
                            val count = input.read(buffer)
                            if (count < 0) break
                            total += count
                            require(total <= MAX_IMPORT_BYTES) { "This image exceeds the 250 MiB import limit." }
                            output.write(buffer, 0, count)
                            digest.update(buffer, 0, count)
                        }
                        output.fd.sync()
                    }
                } ?: error("The selected photo cannot be opened.")
                require(total > 0) { "The selected file is empty." }
                check(staged.renameTo(original)) { "Could not store the photo." }
                // Validate and render all supported formats through Android's decoder. It handles EXIF rotation.
                val mime = mimeOverride ?: resolver.getType(uri) ?: "image/*"
                require(mime.startsWith("image/") || mime.startsWith("video/")) { "Choose a photo or video." }
                val bitmap = mediaPreview(original, mime, 480)
                try {
                    thumb.parentFile?.mkdirs()
                    thumb.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.JPEG, 85, it)) }
                } finally { bitmap.recycle() }
                val gps = runCatching { ExifInterface(original).latLong }.getOrNull()
                val photo = Photo(
                    id = id, displayName = name, originalPath = original.absolutePath,
                    thumbnailPath = thumb.absolutePath, mimeType = mime,
                    bytes = total, importedAt = System.currentTimeMillis(),
                    sha256 = digest.digest().joinToString("") { "%02x".format(it) },
                    latitude = gps?.get(0), longitude = gps?.get(1), parentId = handoff?.photoId,
                    editProvider = handoff?.provider, editInstructions = handoff?.instructions,
                    capturedAt = captureTime(original), lastAccess = System.currentTimeMillis(),
                )
                database.withTransaction {
                    dao.insert(photo)
                    if (handoff != null) dao.deleteHandoff(handoff.id)
                    onSaved(photo)
                }
                photo
            } catch (failure: Exception) {
                staged.delete()
                original.delete()
                thumb.delete()
                throw failure
            }
        }
    }

    suspend fun recordHandoff(photo: Photo, provider: String, instructions: String): Handoff {
        val handoff = Handoff(UUID.randomUUID().toString(), photo.id, provider, instructions, System.currentTimeMillis())
        dao.insertHandoff(handoff)
        return handoff
    }

    suspend fun dismissHandoff(id: String) = dao.deleteHandoff(id)

    suspend fun findHandoff(id: String) = dao.findHandoff(id)

    companion object {
        const val MAX_IMPORT_BYTES = 250L * 1024 * 1024

        fun captureTime(file: File): Long? = runCatching {
            val exif = ExifInterface(file)
            val date = exif.getAttribute(ExifInterface.TAG_DATETIME_ORIGINAL) ?: return@runCatching null
            val format = java.text.SimpleDateFormat("yyyy:MM:dd HH:mm:ss", java.util.Locale.US).apply { isLenient=false }
            val offset = exif.getAttribute(ExifInterface.TAG_OFFSET_TIME_ORIGINAL)
            if(offset!=null) format.timeZone=java.util.TimeZone.getTimeZone("GMT$offset")
            format.parse(date)?.time
        }.getOrNull()

        fun mediaPreview(file: File, mime: String, maximum: Int): Bitmap {
            if(!mime.startsWith("video/")) return preview(file,maximum)
            val retriever=android.media.MediaMetadataRetriever()
            try {
                retriever.setDataSource(file.absolutePath)
                return videoPreview(retriever,maximum)
            } finally { retriever.release() }
        }

        /** Shared bounded frame decoder for private files and readable device descriptors. */
        internal fun videoPreview(retriever: android.media.MediaMetadataRetriever,maximum: Int): Bitmap {
            require(maximum in 1..2048)
            return if(Build.VERSION.SDK_INT >= 27) {
                    val width=retriever.extractMetadata(android.media.MediaMetadataRetriever.METADATA_KEY_VIDEO_WIDTH)?.toIntOrNull() ?: maximum
                    val height=retriever.extractMetadata(android.media.MediaMetadataRetriever.METADATA_KEY_VIDEO_HEIGHT)?.toIntOrNull() ?: maximum
                    val scale=minOf(1.0,maximum.toDouble()/maxOf(width,height))
                    retriever.getScaledFrameAtTime(0,android.media.MediaMetadataRetriever.OPTION_CLOSEST_SYNC,
                        maxOf(1,(width*scale).toInt()),maxOf(1,(height*scale).toInt())) ?: error("No video preview.")
                } else {
                    val width=retriever.extractMetadata(android.media.MediaMetadataRetriever.METADATA_KEY_VIDEO_WIDTH)?.toLongOrNull() ?: 0
                    val height=retriever.extractMetadata(android.media.MediaMetadataRetriever.METADATA_KEY_VIDEO_HEIGHT)?.toLongOrNull() ?: 0
                    require(width>0 && height>0 && width*height<=Runtime.getRuntime().maxMemory()/16) {
                        "This video resolution is too large to preview on Android 8. Use a newer device or a smaller clip."
                    }
                    val image=retriever.getFrameAtTime(0) ?: error("No video preview.")
                    val scale=minOf(1.0,maximum.toDouble()/maxOf(image.width,image.height))
                    val small=image.scale(maxOf(1,(image.width*scale).toInt()),maxOf(1,(image.height*scale).toInt()),true)
                    if(image!==small) image.recycle()
                    small
                }
        }

        fun preview(file: File, requestedDimension: Int): Bitmap {
            require(requestedDimension > 0)
            // Rendering can hold several bitmaps. Adapt the upper bound to smaller phone heaps.
            val maxDimension=minOf(requestedDimension,kotlin.math.sqrt(Runtime.getRuntime().maxMemory()/32.0).toInt().coerceAtLeast(480))
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                return ImageDecoder.decodeBitmap(ImageDecoder.createSource(file)) { decoder, info, _ ->
                    val scale = minOf(1.0, maxDimension.toDouble() / maxOf(info.size.width, info.size.height))
                    decoder.setTargetSize(maxOf(1, (info.size.width * scale).toInt()), maxOf(1, (info.size.height * scale).toInt()))
                    decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
                }
            }
            // Android 8 has no ImageDecoder. Sample before allocating, then apply EXIF orientation.
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeFile(file.absolutePath, bounds)
            require(bounds.outWidth > 0 && bounds.outHeight > 0) { "Unsupported or corrupt image." }
            var sample = 1
            while (maxOf(bounds.outWidth, bounds.outHeight) / sample > maxDimension * 2L) sample *= 2
            val decoded = BitmapFactory.decodeFile(file.absolutePath, BitmapFactory.Options().apply { inSampleSize = sample })
                ?: error("Unsupported or corrupt image.")
            val orientation = runCatching { ExifInterface(file).getAttributeInt(ExifInterface.TAG_ORIENTATION, 1) }.getOrDefault(1)
            val matrix = Matrix().apply {
                when (orientation) {
                    2 -> setScale(-1f, 1f)
                    3 -> setRotate(180f)
                    4 -> setScale(1f, -1f)
                    5 -> { setRotate(90f); postScale(-1f, 1f) }
                    6 -> setRotate(90f)
                    7 -> { setRotate(-90f); postScale(-1f, 1f) }
                    8 -> setRotate(-90f)
                }
                val scale = minOf(1f, maxDimension.toFloat() / maxOf(decoded.width, decoded.height))
                postScale(scale, scale)
            }
            return try {
                val result = Bitmap.createBitmap(decoded, 0, 0, decoded.width, decoded.height, matrix, true)
                if (result !== decoded) decoded.recycle()
                result
            } catch (failure: Exception) { decoded.recycle(); throw failure }
        }
    }
}

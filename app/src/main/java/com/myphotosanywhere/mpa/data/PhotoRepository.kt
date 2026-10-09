package com.myphotosanywhere.mpa.data

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

    suspend fun import(uri: Uri, handoff: Handoff? = null): Photo = withContext(Dispatchers.IO) {
        importLock.withLock {
            if (handoff != null) check(dao.find(handoff.photoId) != null) { "The original photo is missing." }
            val resolver = context.contentResolver
            val id = UUID.randomUUID().toString()
            val dir = File(context.filesDir, "originals").apply { mkdirs() }
            val staged = File(dir, "$id.part")
            val original = File(dir, id)
            val thumb = File(context.filesDir, "thumbnails/$id.jpg")
            val name = resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
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
                val bitmap = preview(original, 480)
                try {
                    thumb.parentFile?.mkdirs()
                    thumb.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.JPEG, 85, it)) }
                } finally { bitmap.recycle() }
                val gps = runCatching { ExifInterface(original).latLong }.getOrNull()
                val photo = Photo(
                    id = id, displayName = name, originalPath = original.absolutePath,
                    thumbnailPath = thumb.absolutePath, mimeType = resolver.getType(uri) ?: "image/*",
                    bytes = total, importedAt = System.currentTimeMillis(),
                    sha256 = digest.digest().joinToString("") { "%02x".format(it) },
                    latitude = gps?.get(0), longitude = gps?.get(1), parentId = handoff?.photoId,
                    editProvider = handoff?.provider, editInstructions = handoff?.instructions,
                )
                database.withTransaction {
                    dao.insert(photo)
                    if (handoff != null) dao.deleteHandoff(handoff.id)
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

        fun preview(file: File, maxDimension: Int): Bitmap {
            require(maxDimension > 0)
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

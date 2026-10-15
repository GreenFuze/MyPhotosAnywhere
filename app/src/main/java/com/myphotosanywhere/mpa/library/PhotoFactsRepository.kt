package com.myphotosanywhere.mpa.library

import android.graphics.BitmapFactory
import androidx.exifinterface.media.ExifInterface
import com.myphotosanywhere.mpa.data.MediaCache
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.timeline.TimelineQuery
import com.myphotosanywhere.mpa.timeline.TimelineRepository
import com.myphotosanywhere.mpa.timeline.TimestampProvenance
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.IOException

internal data class PhotoFacts(val millis: Long,val provenance: TimestampProvenance,val dimensions: Pair<Int,Int>?)

/** Read-only facts: one identity lookup and bounds-only decode under an original-file lease. */
internal class PhotoFactsRepository(private val timeline: TimelineRepository,private val cache: MediaCache) {
    suspend fun read(photo: Photo): PhotoFacts=withContext(Dispatchers.IO) {
        val entry=timeline.find(TimelineQuery(if(photo.archived) "Archive" else "All"),"photo:${photo.id}")
        val dimensions=if(!photo.mimeType.startsWith("image/")) null else cache.openOriginal(photo.id)?.use { original ->
            if(original.photo.sha256!=photo.sha256 || original.photo.bytes!=photo.bytes) null else run {
                val options=BitmapFactory.Options().apply { inJustDecodeBounds=true }
                BitmapFactory.decodeFile(original.file.path,options)
                if(options.outWidth<=0 || options.outHeight<=0) null else {
                    val orientation=try { ExifInterface(original.file).getAttributeInt(ExifInterface.TAG_ORIENTATION,ExifInterface.ORIENTATION_NORMAL) }
                    catch(_: IOException) { ExifInterface.ORIENTATION_NORMAL }
                    if(orientation in listOf(ExifInterface.ORIENTATION_TRANSPOSE,ExifInterface.ORIENTATION_ROTATE_90,
                        ExifInterface.ORIENTATION_TRANSVERSE,ExifInterface.ORIENTATION_ROTATE_270)) options.outHeight to options.outWidth
                    else options.outWidth to options.outHeight
                }
            }
        }
        PhotoFacts(entry?.millis ?: photo.capturedAt ?: photo.importedAt,
            entry?.provenance ?: if(photo.capturedAt!=null) TimestampProvenance.CAPTURE else TimestampProvenance.IMPORT,dimensions)
    }
}

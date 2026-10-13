package com.myphotosanywhere.mpa.timeline

import android.graphics.BitmapFactory
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import java.io.File

/** MPA previews are orientation-normalized. Read their headers, never originals or pixel buffers. */
internal class ThumbnailGeometry(private val capacity: Int=600,private val read: (String)->Float=::readPreview) {
    init { require(capacity in 300..1200) }
    private val shapes=LinkedHashMap<String,Float>(capacity,0.75f,true)
    val retainedCount get()=synchronized(shapes) { shapes.size }
    suspend fun aspect(key: String,thumbnailPath: String): Float=withContext(Dispatchers.IO) {
        synchronized(shapes) { shapes[key] }?.let { return@withContext it }
        currentCoroutineContext().ensureActive()
        val ratio=read(thumbnailPath)
        check(ratio.isFinite() && ratio>0) { "Invalid thumbnail geometry" }
        currentCoroutineContext().ensureActive()
        synchronized(shapes) {
            // Freeze geometry, including missing previews, while this identity remains retained.
            shapes[key] ?: ratio.also {
                shapes[key]=it
                while(shapes.size>capacity) shapes.remove(shapes.keys.first())
            }
        }
    }
    companion object {
        private fun readPreview(path: String): Float {
            if(path.isBlank()) return 1f
            val file=File(path)
            if(!file.isFile || file.length()>8L*1024*1024) return 1f
            val bounds=BitmapFactory.Options().apply { inJustDecodeBounds=true }
            try { file.inputStream().use { BitmapFactory.decodeStream(it,null,bounds) } }
            catch(_: java.io.IOException) { return 1f }
            return if(bounds.outWidth>0 && bounds.outHeight>0) bounds.outWidth.toFloat()/bounds.outHeight else 1f
        }
    }
}

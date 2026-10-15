package com.myphotosanywhere.mpa.thumbnails

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.exifinterface.media.ExifInterface
import com.myphotosanywhere.mpa.data.MediaCache
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.data.PhotoRepository
import com.myphotosanywhere.mpa.timeline.TimelineEntry
import com.myphotosanywhere.mpa.core.LibrarySettings
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException
import java.util.UUID

/** One decoder at a time, demand-driven variants, no retained bitmaps or original downloads. */
internal class AdaptiveThumbnailStore(private val directory: File,private val cache: MediaCache,
    private val storage: StorageBudget,private val settings: LibrarySettings,val policy: ThumbnailPolicy=ThumbnailPolicy()) {
    private val decoding=Mutex()
    private val providerSlots=Semaphore(2)
    private var cleaned=false
    private val partialName=Regex("(?:[0-9a-f]{64}\\.png\\.[0-9a-f-]{36}\\.part|provider-[0-9-]+\\.part(?:\\.[0-9a-f-]{36}\\.part)?)")
    private fun prepareDirectory() {
        if(!directory.isDirectory && !directory.mkdirs()) throw IOException("Cannot create thumbnail cache")
        if(!cleaned) {
            val files=directory.listFiles() ?: throw IOException("Cannot inspect thumbnail cache")
            for(file in files) if(file.isFile && partialName.matches(file.name) && !file.delete()) throw IOException("Cannot recover thumbnail cache")
            cleaned=true
        }
    }
    suspend fun <T> withProviderTemp(action: suspend (File)->T): T=withContext(Dispatchers.IO) {
        providerSlots.withPermit {
            val temporary=decoding.withLock { prepareDirectory();File.createTempFile("provider-",".part",directory) }
            try { action(temporary) } finally { temporary.delete() }
        }
    }
    suspend fun variant(entry: TimelineEntry,width: Int,height: Int,aspect: Float): File?=withContext(Dispatchers.IO) {
        if(entry.photoId==null || !entry.mimeType.startsWith("image/")) return@withContext null
        require(width>0 && height>0)
        val widthBucket=((width.toLong()+63)/64*64).coerceAtMost(2048).toInt()
        val heightBucket=((height.toLong()+63)/64*64).coerceAtMost(2048).toInt()
        val requested=policy.dimension(widthBucket,heightBucket,aspect)
        decoding.withLock {
            currentCoroutineContext().ensureActive()
            prepareDirectory()
            val revision="${entry.sha256}:${entry.bytes}:${entry.version}:$widthBucket:$heightBucket"
            val target=File(directory,policy.key(entry.key,revision,requested)+".png")
            val limit=policy.diskBudget(settings.state.value.thumbnailBudget)
            trim(limit,target)
            if(limit==0L) return@withLock null
            if(valid(target)) { target.setLastModified(System.currentTimeMillis());return@withLock target }
            if(storage.available(directory)<RESERVE+policy.maximum.toLong()*policy.maximum*4) return@withLock null
            cache.openThumbnailOriginal(entry.photoId)?.use { original ->
                if(original.photo.sha256!=entry.sha256 || original.photo.bytes!=entry.bytes) return@withLock null
                currentCoroutineContext().ensureActive()
                val bounds=BitmapFactory.Options().apply { inJustDecodeBounds=true }
                BitmapFactory.decodeFile(original.file.path,bounds)
                if(bounds.outWidth<=0 || bounds.outHeight<=0) throw IOException("Unsupported thumbnail image")
                val orientation=try { ExifInterface(original.file).getAttributeInt(ExifInterface.TAG_ORIENTATION,1) }
                    catch(_: IOException) { 1 }
                val raw=bounds.outWidth.toFloat()/bounds.outHeight
                val actualAspect=if(orientation in 5..8) 1f/raw else raw
                val dimension=policy.dimension(widthBucket,heightBucket,actualAspect)
                if(dimension<=480 || maxOf(bounds.outWidth,bounds.outHeight)<=480) return@withLock null
                val bitmap=decode(original.file,dimension)
                try {
                    currentCoroutineContext().ensureActive()
                    write(bitmap,target)
                    currentCoroutineContext().ensureActive()
                    trim(limit,target)
                    target.takeIf { it.isFile }
                } finally { bitmap.recycle() }
            }
        }
    }

    /** Commit derived provider bytes before callers acquire the media-cache lock. */
    suspend fun commitProvider(source: File,identity: String,revision: String): File?=withContext(Dispatchers.IO) {
        decoding.withLock {
            prepareDirectory()
            check(source.parentFile?.canonicalFile==directory.canonicalFile && source.name.startsWith("provider-") && source.extension=="part")
            val budget=policy.diskBudget(settings.state.value.thumbnailBudget)
            if(source.length()>budget || budget==0L) return@withLock null
            val target=File(directory,policy.key("provider:$identity",revision,policy.maximum)+".thumb")
            if(!source.renameTo(target)) throw IOException("Cannot commit provider preview")
            trim(budget,target)
            target.takeIf { it.isFile }
        }
    }

    /** Provider bytes are already bounded by DriveGateway; preserve their detail without upscaling. */
    suspend fun normalizeProvider(source: File): Unit=withContext(Dispatchers.IO) {
        decoding.withLock {
            currentCoroutineContext().ensureActive()
            val parent=requireNotNull(source.parentFile)
            check(parent.canonicalFile==directory.canonicalFile && source.name.startsWith("provider-") && source.extension=="part") { "Provider normalization requires an owned temporary file" }
            if(storage.available(parent)<RESERVE+policy.maximum.toLong()*policy.maximum*4) throw IOException("Not enough space for thumbnail preview")
            val bitmap=decode(source,policy.maximum)
            try { currentCoroutineContext().ensureActive();write(bitmap,source,provider=true) }
            finally { bitmap.recycle() }
        }
    }

    private fun decode(source: File,maximum: Int): Bitmap {
        val bounds=BitmapFactory.Options().apply { inJustDecodeBounds=true }
        BitmapFactory.decodeFile(source.path,bounds)
        if(bounds.outWidth<=0 || bounds.outHeight<=0) throw IOException("Unsupported thumbnail image")
        return PhotoRepository.preview(source,maximum)
    }
    private fun valid(file: File): Boolean {
        if(!file.isFile || file.length()==0L) return false
        val bounds=BitmapFactory.Options().apply { inJustDecodeBounds=true }
        BitmapFactory.decodeFile(file.path,bounds)
        return bounds.outWidth>0 && bounds.outHeight>0 && maxOf(bounds.outWidth,bounds.outHeight)<=policy.maximum
    }
    private fun write(bitmap: Bitmap,target: File,provider: Boolean=false) {
        val partial=File(target.parentFile,"${target.name}.${UUID.randomUUID()}.part")
        try {
            partial.outputStream().use {
                // Local variants preserve fine text; provider images retain alpha when present.
                val format=if(provider && !bitmap.hasAlpha()) Bitmap.CompressFormat.JPEG else Bitmap.CompressFormat.PNG
                if(!bitmap.compress(format,if(format==Bitmap.CompressFormat.PNG) 100 else 92,it)) throw IOException("Cannot encode thumbnail")
            }
            if(!partial.renameTo(target)) throw IOException("Cannot commit thumbnail cache")
        } finally { partial.delete() }
    }
    private fun trim(budget: Long,keep: File) {
        val files=directory.listFiles()?.filter { it.extension in setOf("png","thumb") && it.nameWithoutExtension.matches(Regex("[0-9a-f]{64}")) }
            ?: throw IOException("Cannot inspect thumbnail cache")
        var bytes=files.sumOf { it.length() }
        var count=files.size
        for(file in files.sortedBy { it.lastModified() }) {
            if(bytes<=budget && count<=1024) break
            if(file==keep) continue
            val size=file.length()
            if(file.delete()) { bytes-=size;count-- }
        }
        // If one image exceeds the configured budget, retain the original small preview.
        if(bytes>budget || count>1024) {
            val size=keep.length()
            if(keep.delete()) { bytes-=size;count-- }
        }
        if(bytes>budget || count>1024) throw IOException("Thumbnail cache is full")
    }
    companion object { private const val RESERVE=32L*1024*1024 }
}

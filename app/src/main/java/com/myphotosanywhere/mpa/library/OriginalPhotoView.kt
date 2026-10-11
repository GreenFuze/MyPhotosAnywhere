package com.myphotosanywhere.mpa.library

import android.content.Context
import android.graphics.*
import android.view.View
import androidx.exifinterface.media.ExifInterface
import java.io.File
import java.util.concurrent.Executors
import kotlin.math.ceil
import kotlin.math.floor
import kotlin.math.min

/** A single decode queue prevents adjacent pager pages from allocating originals concurrently. */
private object OriginalImageDecoding {
    val worker=Executors.newSingleThreadExecutor { task -> Thread(task,"MPA-image-decoder").apply { isDaemon=true } }
}

internal interface OriginalImageReader: AutoCloseable {
    val width: Int
    val height: Int
    val orientation: Int
    val supportsRegions: Boolean
    fun preview(): Bitmap
    fun region(rect: Rect,sample: Int): Bitmap?
}

/** Pixel bounds account for decoder rounding and smaller Android heaps. */
internal class OriginalImageBudget(heapBytes: Long=Runtime.getRuntime().maxMemory()) {
    val previewPixels=(heapBytes/128).coerceIn(65_536,1_048_576)
    val tilePixels=(heapBytes/64).coerceIn(65_536,4_000_000)
    fun previewSample(width: Int,height: Int): Int {
        require(width>0 && height>0)
        var sample=1
        while(maxOf(width,height).toLong()/sample>2048 || pixels(width,height,sample)>previewPixels) sample*=2
        return sample
    }
    companion object {
        fun pixels(width: Int,height: Int,sample: Int): Long =
            ((width.toLong()+sample-1)/sample)*((height.toLong()+sample-1)/sample)
    }
}

/** Decode visible regions from original bytes, avoiding a full 50/200 MP bitmap in memory. */
@Suppress("DEPRECATION")
internal class OriginalImageSource(file: File): OriginalImageReader {
    private val path=file.absolutePath
    private val bounds=BitmapFactory.Options().apply { inJustDecodeBounds=true; BitmapFactory.decodeFile(path,this) }
    override val width=bounds.outWidth
    override val height=bounds.outHeight
    override val orientation=runCatching { ExifInterface(path).getAttributeInt(ExifInterface.TAG_ORIENTATION,1) }.getOrDefault(1)
    private val decoder=runCatching { BitmapRegionDecoder.newInstance(path,false) }.getOrNull()
    override val supportsRegions get()=decoder!=null
    init { require(width>0 && height>0) { "This image format cannot be displayed on this phone." } }
    override fun preview(): Bitmap {
        val sample=OriginalImageBudget().previewSample(width,height)
        return requireNotNull(BitmapFactory.decodeFile(path,BitmapFactory.Options().apply { inSampleSize=sample; inPreferredConfig=Bitmap.Config.ARGB_8888 }))
    }
    override fun region(rect: Rect,sample: Int): Bitmap? = decoder?.decodeRegion(rect,
        BitmapFactory.Options().apply { inSampleSize=sample; inPreferredConfig=Bitmap.Config.ARGB_8888 })
    override fun close() { decoder?.recycle() }
}

internal object OriginalImageGeometry {
    fun matrix(width: Int,height: Int,orientation: Int,viewWidth: Int,viewHeight: Int,zoom: Float,x: Float,y: Float): Matrix {
        val w=width.toFloat(); val h=height.toFloat()
        val values=when(orientation) {
            2 -> floatArrayOf(-1f,0f,w,0f,1f,0f,0f,0f,1f)
            3 -> floatArrayOf(-1f,0f,w,0f,-1f,h,0f,0f,1f)
            4 -> floatArrayOf(1f,0f,0f,0f,-1f,h,0f,0f,1f)
            5 -> floatArrayOf(0f,1f,0f,1f,0f,0f,0f,0f,1f)
            6 -> floatArrayOf(0f,-1f,h,1f,0f,0f,0f,0f,1f)
            7 -> floatArrayOf(0f,-1f,h,-1f,0f,w,0f,0f,1f)
            8 -> floatArrayOf(0f,1f,0f,-1f,0f,w,0f,0f,1f)
            else -> floatArrayOf(1f,0f,0f,0f,1f,0f,0f,0f,1f)
        }
        val rotated=orientation in 5..8
        val uprightWidth=if(rotated) h else w; val uprightHeight=if(rotated) w else h
        val scale=min(viewWidth/uprightWidth,viewHeight/uprightHeight)*zoom
        return Matrix().apply {
            setValues(values);postScale(scale,scale)
            postTranslate((viewWidth-uprightWidth*scale)/2+x,(viewHeight-uprightHeight*scale)/2+y)
        }
    }
    fun sample(scale: Float,region: Rect,maxPixels: Long=OriginalImageBudget().tilePixels): Int {
        require(scale.isFinite() && scale>0 && !region.isEmpty && maxPixels>0)
        var sample=1
        while(sample<(1 shl 30) && sample.toLong()*2<=1.0/scale) sample*=2
        while(OriginalImageBudget.pixels(region.width(),region.height(),sample)>maxPixels) sample*=2
        return sample
    }
}

/** Input gestures belong to Compose so a one-finger swipe can still page between photos. */
internal class OriginalPhotoView(context: Context,private val openSource: (File)->OriginalImageReader = { OriginalImageSource(it) }): View(context),AutoCloseable {
    private val worker=OriginalImageDecoding.worker
    private val paint=Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG)
    private var source: OriginalImageReader?=null
    private var ownedSource: OriginalImageReader?=null
    private var preview: Bitmap?=null
    private var tile: Bitmap?=null
    private var tileRect=Rect()
    private var requested: Pair<Rect,Int>?=null
    @Volatile private var generation=0
    @Volatile private var closed=false
    private var started=false
    private var active=true
    private var tilesEnabled=true
    private var zoom=1f; private var offsetX=0f; private var offsetY=0f
    var onQuality: (String)->Unit = {}
    fun load(file: File) {
        if(started) return; started=true
        worker.execute {
            var opened: OriginalImageReader?=null
            try {
                if(closed) return@execute
                opened=openSource(file);ownedSource=opened
                val image=opened.preview();val ready=opened
                post {
                    if(closed) { image.recycle(); return@post }
                    source=ready;preview=image
                    onQuality("Original · ${ready.width} × ${ready.height}" + if(!ready.supportsRegions) " · Preview only for this format" else "")
                    invalidate();requestTile()
                }
            } catch(_: OutOfMemoryError) {
                opened?.close();ownedSource=null
                post { if(!closed) onQuality("Not enough memory to display this photo. Close it and try again after the import.") }
            } catch(e: Exception) {
                opened?.close();ownedSource=null;post { if(!closed) onQuality(e.message ?: "Cannot display original") }
            }
        }
    }
    fun setActive(value: Boolean) {
        if(active==value) return
        active=value;generation++;requested=null
        if(!active) { tile=null;invalidate() } else requestTile()
    }
    fun transform(scale: Float,x: Float,y: Float) {
        if(zoom==scale && offsetX==x && offsetY==y) return
        zoom=scale;offsetX=x;offsetY=y;invalidate();requestTile()
    }
    private fun imageMatrix(s: OriginalImageReader)=OriginalImageGeometry.matrix(s.width,s.height,s.orientation,width,height,zoom,offsetX,offsetY)
    override fun onSizeChanged(w: Int,h: Int,oldw: Int,oldh: Int) { super.onSizeChanged(w,h,oldw,oldh);requestTile() }
    private fun requestTile() {
        val s=source ?: return
        if(closed || !active || !tilesEnabled || !s.supportsRegions || width==0 || height==0) return
        val matrix=imageMatrix(s);val inverse=Matrix()
        if(!matrix.invert(inverse)) return
        val visible=RectF(0f,0f,width.toFloat(),height.toFloat());inverse.mapRect(visible)
        if(!visible.intersect(0f,0f,s.width.toFloat(),s.height.toFloat())) return
        val region=Rect(floor(visible.left).toInt(),floor(visible.top).toInt(),ceil(visible.right).toInt(),ceil(visible.bottom).toInt())
        val values=FloatArray(9);matrix.getValues(values)
        val scale=kotlin.math.hypot(values[0].toDouble(),values[3].toDouble()).toFloat()
        var sample=OriginalImageGeometry.sample(scale,region)
        // Expand to a sample-aligned boundary so pixels do not shimmer as a tile moves.
        val step=sample.toLong()*64
        region.left=(region.left/step*step).coerceAtLeast(0).toInt();region.top=(region.top/step*step).coerceAtLeast(0).toInt()
        region.right=((region.right+step-1)/step*step).coerceAtMost(s.width.toLong()).toInt()
        region.bottom=((region.bottom+step-1)/step*step).coerceAtMost(s.height.toLong()).toInt()
        // Alignment can enlarge a region: enforce the bound after rounding, too.
        while(OriginalImageBudget.pixels(region.width(),region.height(),sample)>OriginalImageBudget().tilePixels) sample*=2
        val key=region to sample
        if(requested==key) return;requested=key
        val ticket=++generation
        worker.execute {
            if(closed || ticket!=generation) return@execute
            val decoded=try { s.region(region,sample) } catch(_: OutOfMemoryError) {
                post { if(!closed && ticket==generation) {
                    tilesEnabled=false;tile=null;invalidate()
                    onQuality("Memory limit · Showing preview. Close and reopen after the import for full detail.")
                } }
                return@execute
            } catch(_: Exception) {
                post { if(!closed && ticket==generation) { tilesEnabled=false;onQuality("Cannot decode this region · Showing preview") } }
                return@execute
            } ?: return@execute
            post {
                if(closed || ticket!=generation) decoded.recycle()
                // RenderThread may still hold the previous frame. Drop our reference and let
                // Android release its bitmap once drawing has finished; never recycle it here.
                else { tile=decoded;tileRect=region;invalidate() }
            }
        }
    }
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val s=source ?: return
        val save=canvas.save();canvas.concat(imageMatrix(s))
        preview?.let { canvas.drawBitmap(it,null,RectF(0f,0f,s.width.toFloat(),s.height.toFloat()),paint) }
        tile?.let { canvas.drawBitmap(it,null,RectF(tileRect),paint) }
        canvas.restoreToCount(save)
    }
    override fun close() {
        if(closed) return;closed=true;generation++
        source=null;preview=null;tile=null;invalidate()
        // Release the decoder after its in-flight operation, never while decoding a region.
        worker.execute { ownedSource?.close();ownedSource=null }
    }
}

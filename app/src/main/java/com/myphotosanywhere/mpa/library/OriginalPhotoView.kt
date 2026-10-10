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

/** Decode visible regions from original bytes, avoiding a full 50/200 MP bitmap in memory. */
@Suppress("DEPRECATION")
internal class OriginalImageSource(file: File): AutoCloseable {
    private val path=file.absolutePath
    private val bounds=BitmapFactory.Options().apply { inJustDecodeBounds=true; BitmapFactory.decodeFile(path,this) }
    val width=bounds.outWidth
    val height=bounds.outHeight
    val orientation=runCatching { ExifInterface(path).getAttributeInt(ExifInterface.TAG_ORIENTATION,1) }.getOrDefault(1)
    private val decoder=runCatching { BitmapRegionDecoder.newInstance(path,false) }.getOrNull()
    val supportsRegions get()=decoder!=null
    init { require(width>0 && height>0) { "This image format cannot be displayed on this phone." } }
    fun preview(): Bitmap {
        var sample=1
        while(maxOf(width,height)/sample>2048) sample*=2
        return requireNotNull(BitmapFactory.decodeFile(path,BitmapFactory.Options().apply { inSampleSize=sample }))
    }
    fun region(rect: Rect,sample: Int): Bitmap? = decoder?.decodeRegion(rect,
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
    fun sample(scale: Float,region: Rect): Int {
        var sample=1
        while(sample*2<=1/scale) sample*=2
        while(region.width().toLong()*region.height()/(sample.toLong()*sample)>4_000_000) sample*=2
        return sample
    }
}

/** Input gestures belong to Compose so a one-finger swipe can still page between photos. */
internal class OriginalPhotoView(context: Context): View(context),AutoCloseable {
    private val worker=Executors.newSingleThreadExecutor()
    private val paint=Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG)
    private var source: OriginalImageSource?=null
    private var ownedSource: OriginalImageSource?=null
    private var preview: Bitmap?=null
    private var tile: Bitmap?=null
    private var tileRect=Rect()
    private var requested: Pair<Rect,Int>?=null
    @Volatile private var generation=0
    @Volatile private var closed=false
    private var started=false
    private var zoom=1f; private var offsetX=0f; private var offsetY=0f
    var onQuality: (String)->Unit = {}
    fun load(file: File) {
        if(started) return; started=true
        worker.execute {
            var opened: OriginalImageSource?=null
            try {
                opened=OriginalImageSource(file);ownedSource=opened
                val image=opened.preview();val ready=opened
                post {
                    if(closed) { image.recycle(); return@post }
                    source=ready;preview=image
                    onQuality("Original · ${ready.width} × ${ready.height}" + if(!ready.supportsRegions) " · Preview only for this format" else "")
                    invalidate();requestTile()
                }
            } catch(e: Exception) {
                opened?.close();ownedSource=null;post { if(!closed) onQuality(e.message ?: "Cannot display original") }
            }
        }
    }
    fun transform(scale: Float,x: Float,y: Float) {
        if(zoom==scale && offsetX==x && offsetY==y) return
        zoom=scale;offsetX=x;offsetY=y;invalidate();requestTile()
    }
    private fun imageMatrix(s: OriginalImageSource)=OriginalImageGeometry.matrix(s.width,s.height,s.orientation,width,height,zoom,offsetX,offsetY)
    override fun onSizeChanged(w: Int,h: Int,oldw: Int,oldh: Int) { super.onSizeChanged(w,h,oldw,oldh);requestTile() }
    private fun requestTile() {
        val s=source ?: return
        if(closed || !s.supportsRegions || width==0 || height==0) return
        val matrix=imageMatrix(s);val inverse=Matrix()
        if(!matrix.invert(inverse)) return
        val visible=RectF(0f,0f,width.toFloat(),height.toFloat());inverse.mapRect(visible)
        if(!visible.intersect(0f,0f,s.width.toFloat(),s.height.toFloat())) return
        val region=Rect(floor(visible.left).toInt(),floor(visible.top).toInt(),ceil(visible.right).toInt(),ceil(visible.bottom).toInt())
        val values=FloatArray(9);matrix.getValues(values)
        val scale=kotlin.math.hypot(values[0].toDouble(),values[3].toDouble()).toFloat()
        val sample=OriginalImageGeometry.sample(scale,region)
        // Expand to a sample-aligned boundary so pixels do not shimmer as a tile moves.
        val step=sample*64
        region.left=(region.left/step*step).coerceAtLeast(0);region.top=(region.top/step*step).coerceAtLeast(0)
        region.right=((region.right+step-1)/step*step).coerceAtMost(s.width)
        region.bottom=((region.bottom+step-1)/step*step).coerceAtMost(s.height)
        val key=region to sample
        if(requested==key) return;requested=key
        val ticket=++generation
        worker.execute {
            if(closed || ticket!=generation) return@execute
            val decoded=runCatching { s.region(region,sample) }.getOrNull() ?: return@execute
            post {
                if(closed || ticket!=generation) decoded.recycle()
                else { tile?.recycle();tile=decoded;tileRect=region;invalidate() }
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
        preview?.recycle();preview=null;tile?.recycle();tile=null
        // Release the decoder after its in-flight operation, never while decoding a region.
        worker.execute { ownedSource?.close();ownedSource=null };worker.shutdown()
    }
}

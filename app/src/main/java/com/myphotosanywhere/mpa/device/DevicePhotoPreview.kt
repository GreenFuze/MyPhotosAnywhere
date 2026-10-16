package com.myphotosanywhere.mpa.device

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Matrix
import androidx.exifinterface.media.ExifInterface
import java.io.FileDescriptor
import java.io.IOException

/** Bounded, upright bitmap from the granted original; never a copied camera-roll file. */
internal class DevicePhotoPreview {
    fun decode(descriptor: FileDescriptor,requested: Int): Bitmap {
        require(requested in 1..2048)
        val maximum=minOf(requested,kotlin.math.sqrt(Runtime.getRuntime().maxMemory()/32.0).toInt().coerceAtLeast(480))
        val bounds=BitmapFactory.Options().apply { inJustDecodeBounds=true }
        BitmapFactory.decodeFileDescriptor(descriptor,null,bounds)
        if(bounds.outWidth<=0 || bounds.outHeight<=0) throw IOException("This device image cannot be decoded. Its original is preserved.")
        var sample=1
        while(maxOf(bounds.outWidth,bounds.outHeight)/sample>maximum*2L) sample*=2
        val decoded=BitmapFactory.decodeFileDescriptor(descriptor,null,BitmapFactory.Options().apply { inSampleSize=sample;inPreferredConfig=Bitmap.Config.ARGB_8888 })
            ?: throw IOException("Android could not decode this device image. Retry.")
        try {
            val orientation=try { ExifInterface(descriptor).getAttributeInt(ExifInterface.TAG_ORIENTATION,1) } catch(_: IOException) { 1 }
            val transform=Matrix().apply {
                when(orientation) {
                    2->setScale(-1f,1f);3->setRotate(180f);4->setScale(1f,-1f)
                    5->{ setRotate(90f);postScale(-1f,1f) };6->setRotate(90f)
                    7->{ setRotate(-90f);postScale(-1f,1f) };8->setRotate(-90f)
                }
                val scale=minOf(1f,maximum.toFloat()/maxOf(decoded.width,decoded.height));postScale(scale,scale)
            }
            val upright=Bitmap.createBitmap(decoded,0,0,decoded.width,decoded.height,transform,true)
            if(upright!==decoded) decoded.recycle()
            return upright
        } catch(failure: Throwable) { decoded.recycle();throw failure }
    }
}

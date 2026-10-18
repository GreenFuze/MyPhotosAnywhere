package com.myphotosanywhere.mpa

import android.content.ContentValues
import android.content.Context
import android.graphics.Bitmap
import android.os.Build
import android.os.ParcelFileDescriptor
import android.provider.MediaStore
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.security.MessageDigest
import java.util.zip.CRC32

/** Test-only publication of the same captured frame; incomplete encoding fails at its callsite. */
internal class NativeScreenshotEvidence(private val context: Context) {
    fun write(name: String,frame: Bitmap) {
        check(Build.VERSION.SDK_INT>=29) { "Native screenshot evidence requires API29+." }
        require(name.matches(Regex("[a-z0-9-]+")))
        val pixels=checkNotNull(frame.copy(Bitmap.Config.ARGB_8888,false)) { "Screenshot pixels are unavailable." }
        val bytes=try {
            BoundedEncoding().use { output ->
                check(pixels.compress(Bitmap.CompressFormat.PNG,100,output)) { "Screenshot encoding failed." }
                output.toByteArray().also { validate(it,pixels.width,pixels.height) }
            }
        } finally { if(pixels!==frame) pixels.recycle() }
        val resolver=context.contentResolver
        val values=ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME,"$name.png")
            put(MediaStore.MediaColumns.MIME_TYPE,"image/png")
            put(MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
            put(MediaStore.MediaColumns.IS_PENDING,1)
        }
        val uri=checkNotNull(resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
        val expected=MessageDigest.getInstance("SHA-256").digest(bytes)
        fun verifyReadback() {
            checkNotNull(resolver.openInputStream(uri)).use { input ->
                val digest=MessageDigest.getInstance("SHA-256");val buffer=ByteArray(8192);var total=0
                while(true) {
                    val count=input.read(buffer);if(count<0) break
                    total+=count;check(total<=bytes.size) { "Screenshot size changed during publication." }
                    digest.update(buffer,0,count)
                }
                check(total==bytes.size && digest.digest().contentEquals(expected)) { "Screenshot bytes changed during publication." }
            }
        }
        try {
            val descriptor=checkNotNull(resolver.openFileDescriptor(uri,"w"))
            ParcelFileDescriptor.AutoCloseOutputStream(descriptor).use { output ->
                output.write(bytes);output.flush();output.fd.sync()
            }
            verifyReadback()
            check(resolver.update(uri,ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING,0) },null,null)==1)
            verifyReadback()
        } catch(failure: Throwable) {
            try { check(resolver.delete(uri,null,null)==1) { "Cannot remove rejected screenshot entry." } }
            catch(cleanup: Throwable) { if(cleanup!==failure) failure.addSuppressed(cleanup) }
            throw failure
        }
    }

    private class BoundedEncoding : ByteArrayOutputStream() {
        override fun write(value: Int) { if(count>=LIMIT) throw IOException("Screenshot exceeds encoding budget.");super.write(value) }
        override fun write(bytes: ByteArray,offset: Int,length: Int) {
            if(length>LIMIT-count) throw IOException("Screenshot exceeds encoding budget.")
            super.write(bytes,offset,length)
        }
    }

    private fun validate(bytes: ByteArray,width: Int,height: Int) {
        check(bytes.size in 45..LIMIT && bytes.copyOfRange(0,8).contentEquals(byteArrayOf(-119,80,78,71,13,10,26,10)))
        fun integer(at: Int)=java.nio.ByteBuffer.wrap(bytes,at,4).int
        var position=8;var header=false;var pixels=false;var ended=false
        while(position<bytes.size) {
            check(bytes.size-position>=12)
            val length=integer(position);check(length>=0 && length<=bytes.size-position-12)
            val kind=String(bytes,position+4,4,Charsets.US_ASCII);val end=position+12+length
            check(CRC32().apply { update(bytes,position+4,length+4) }.value==integer(end-4).toLong().and(0xffffffffL))
            if(!header) check(kind=="IHDR" && length==13 && integer(position+8)==width && integer(position+12)==height)
            when(kind) {
                "IHDR" -> { check(!header);header=true }
                "IDAT" -> if(length>0) pixels=true
                "IEND" -> { check(length==0 && end==bytes.size);ended=true }
            }
            position=end
        }
        check(header && pixels && ended) { "Screenshot PNG is incomplete; captured frame was not published." }
    }

    private companion object { const val LIMIT=16*1024*1024 }
}

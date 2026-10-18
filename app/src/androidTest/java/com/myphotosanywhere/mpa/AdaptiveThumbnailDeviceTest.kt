package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.core.graphics.scale
import androidx.room.Room
import androidx.exifinterface.media.ExifInterface
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.core.LibrarySettings
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.thumbnails.*
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.concurrent.atomic.AtomicInteger

@RunWith(AndroidJUnit4::class)
class AdaptiveThumbnailDeviceTest {
    private class Fixture: AutoCloseable {
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val directory=File.createTempFile("thumbnail-fixture-","",context.cacheDir).apply { check(delete());check(mkdir()) }
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        val settings=LibrarySettings(context)
        private val before=settings.state.value
        val cache=MediaCache(context,db,settings)
        var availableBytes=Long.MAX_VALUE
        val capacity=object: StorageBudget.Capacity {
            override fun available(directory: File)=availableBytes
            override fun allocatable(directory: File)=availableBytes
            override fun allocate(directory: File,bytes: Long) { error("Preview generation must not reserve original storage") }
        }
        val store=AdaptiveThumbnailStore(File(directory,"variants"),cache,StorageBudget(capacity),settings,ThumbnailPolicy(256L*1024*1024))
        init { settings.update(before.copy(thumbnailBudget=64L*1024*1024)) }
        suspend fun photo(name: String,width: Int=2048,height: Int=1024,alpha: Boolean=false): Photo {
            val file=File(directory,"$name.png")
            val bitmap=Bitmap.createBitmap(width,height,Bitmap.Config.ARGB_8888)
            try {
                bitmap.eraseColor(if(alpha) 0x604080c0 else android.graphics.Color.WHITE)
                val canvas=android.graphics.Canvas(bitmap)
                val ink=android.graphics.Paint().apply { color=android.graphics.Color.BLACK }
                for(x in 0 until width/2 step 8) canvas.drawRect(x.toFloat(),0f,(x+4).toFloat(),height.toFloat(),ink)
                ink.textSize=width/18f
                canvas.drawText("MPA preview",width*0.55f,height*0.5f,ink)
                file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) }
            } finally { bitmap.recycle() }
            val hash=java.security.MessageDigest.getInstance("SHA-256").digest(file.readBytes()).joinToString("") { "%02x".format(it) }
            return Photo("quality-$name","$name.png",file.path,File(directory,"$name-base.jpg").path,"image/png",file.length(),1,hash).also { db.photos().insert(it) }
        }
        fun entry(photo: Photo)=TimelineEntry("photo:${photo.id}",photo.id,null,photo.capturedAt ?: photo.importedAt,TimestampProvenance.IMPORT,
            photo.displayName,photo.mimeType,photo.originalPath,photo.thumbnailPath,0,photo.bytes,photo.sha256,null,null,null,null)
        override fun close() {
            settings.update(before);db.close()
            check(requireNotNull(directory.parentFile).canonicalFile==context.cacheDir.canonicalFile && directory.name.startsWith("thumbnail-fixture-"))
            check(directory.deleteRecursively())
        }
    }

    @Test fun largerTilesUseSharperBoundedPreviewsWithoutMutatingOriginals()=runBlocking<Unit> {
        Fixture().use { f ->
            val photo=f.photo("chart");val original=File(photo.originalPath);val bytes=original.readBytes();val stamp=original.lastModified()
            val small=requireNotNull(f.cache.thumbnail(photo))
            val base=BitmapFactory.decodeFile(small.path)
            val enlarged=try { assertEquals(480,base.width);base.scale(768,384) } finally { base.recycle() }
            val variant=requireNotNull(f.store.variant(f.entry(photo),768,384,2f))
            val detail=BitmapFactory.decodeFile(variant.path)
            try {
                assertEquals(768,detail.width);assertEquals(384,detail.height)
                fun contrast(image: Bitmap): Long=(1 until 350).sumOf { x ->
                    kotlin.math.abs(android.graphics.Color.red(image.getPixel(x,100))-android.graphics.Color.red(image.getPixel(x-1,100))).toLong()
                }
                assertTrue("Larger preview must retain more stripe contrast than enlarged base",contrast(detail)>contrast(enlarged))
                export(f.context,"thumbnail-quality-base",enlarged);export(f.context,"thumbnail-quality-sharp",detail)
            } finally { detail.recycle();enlarged.recycle() }
            assertArrayEquals(bytes,original.readBytes());assertEquals(stamp,original.lastModified())
            assertEquals(photo,f.db.photos().find(photo.id))
            assertTrue(original.delete())
            assertEquals(variant,f.store.variant(f.entry(photo),768,384,2f)) // The variant survives original eviction.
            assertEquals(photo,f.db.photos().find(photo.id))
        }
    }

    @Test fun alphaCorruptionAndRevisionChangesNeverSubstituteAnotherPreview()=runBlocking<Unit> {
        Fixture().use { f ->
            val photo=f.photo("alpha",alpha=true);val entry=f.entry(photo)
            val first=requireNotNull(f.store.variant(entry,768,384,2f))
            val decoded=BitmapFactory.decodeFile(first.path)
            try { assertTrue(decoded.hasAlpha());assertTrue(android.graphics.Color.alpha(decoded.getPixel(700,101))<255) } finally { decoded.recycle() }
            first.writeText("interrupted corrupt cache")
            val repaired=requireNotNull(f.store.variant(entry,768,384,2f))
            val bounds=BitmapFactory.Options().apply { inJustDecodeBounds=true };BitmapFactory.decodeFile(repaired.path,bounds)
            assertEquals(768,bounds.outWidth)
            assertNull(f.store.variant(entry.copy(sha256="different-content"),768,384,2f))
            assertNull(f.store.variant(entry.copy(bytes=entry.bytes+1),768,384,2f))
            assertEquals(photo,f.db.photos().find(photo.id))
        }
    }

    @Test fun providerNormalizationRetainsOrientationAndAlphaAndRejectsOriginalTargets()=runBlocking<Unit> {
        Fixture().use { f ->
            val photo=f.photo("provider",240,360,alpha=true);val bytes=File(photo.originalPath).readBytes()
            try { f.store.normalizeProvider(File(photo.originalPath));fail("Original target accepted") } catch(_: IllegalStateException) { }
            assertArrayEquals(bytes,File(photo.originalPath).readBytes())
            f.store.withProviderTemp { temporary ->
                temporary.writeBytes(bytes);f.store.normalizeProvider(temporary)
                val preview=BitmapFactory.decodeFile(temporary.path)
                try { assertEquals(240,preview.width);assertEquals(360,preview.height);assertTrue(preview.hasAlpha()) } finally { preview.recycle() }
                assertNotNull(f.store.commitProvider(temporary,"source:provider","1"))
            }
            val jpeg=File(f.directory,"rotated.jpg")
            val bitmap=Bitmap.createBitmap(240,360,Bitmap.Config.ARGB_8888)
            try { jpeg.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.JPEG,90,it)) } } finally { bitmap.recycle() }
            ExifInterface(jpeg).apply { setAttribute(ExifInterface.TAG_ORIENTATION,"6");saveAttributes() }
            f.store.withProviderTemp { temporary ->
                temporary.writeBytes(jpeg.readBytes());f.store.normalizeProvider(temporary)
                val preview=BitmapFactory.decodeFile(temporary.path)
                try { assertEquals(360,preview.width);assertEquals(240,preview.height) } finally { preview.recycle() }
            }
            assertFalse(requireNotNull(File(f.directory,"variants").listFiles()).any { it.extension=="part" })
        }
    }

    @Test fun lowSpaceBudgetsAndCancelledProviderWorkPreserveOriginalBytes()=runBlocking<Unit> {
        Fixture().use { f ->
            val photo=f.photo("budget");val bytes=File(photo.originalPath).readBytes()
            f.availableBytes=0
            assertNull(f.store.variant(f.entry(photo),768,384,2f))
            f.availableBytes=Long.MAX_VALUE
            f.settings.update(f.settings.state.value.copy(thumbnailBudget=0))
            assertNull(f.store.variant(f.entry(photo),768,384,2f))
            val active=AtomicInteger();val peak=AtomicInteger();val started=CompletableDeferred<Unit>();val release=CompletableDeferred<Unit>()
            val jobs=List(3) { launch {
                f.store.withProviderTemp { temporary ->
                    temporary.writeText("owned temporary")
                    val current=active.incrementAndGet();peak.updateAndGet { maxOf(it,current) }
                    if(current==2) started.complete(Unit)
                    try { release.await() } finally { active.decrementAndGet() }
                }
            } }
            withTimeout(5000) { started.await() }
            assertEquals(2,peak.get());jobs.first().cancelAndJoin();release.complete(Unit);jobs.joinAll()
            assertTrue(peak.get()<=2)
            assertFalse(requireNotNull(File(f.directory,"variants").listFiles()).any { it.extension=="part" })
            assertArrayEquals(bytes,File(photo.originalPath).readBytes());assertEquals(photo,f.db.photos().find(photo.id))
        }
    }
    private fun export(context: android.content.Context,name: String,bitmap: Bitmap) {
        com.myphotosanywhere.mpa.NativeScreenshotEvidence(context).write(name,bitmap)
    }
}

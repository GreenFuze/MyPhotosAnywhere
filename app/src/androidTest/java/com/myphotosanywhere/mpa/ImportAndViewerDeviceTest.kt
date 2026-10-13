package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Rect
import android.os.Build
import android.view.ViewGroup
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.importing.*
import com.myphotosanywhere.mpa.library.OriginalImageReader
import com.myphotosanywhere.mpa.library.OriginalPhotoView
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

@RunWith(AndroidJUnit4::class)
class ImportAndViewerDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private val app get()=instrumentation.targetContext.applicationContext as MpaApplication

    @Test fun completedCancelledAndFailedImportsDismissPersistAndCanBeRestored() {
        if(Build.VERSION.SDK_INT>=33) instrumentation.uiAutomation.grantRuntimePermission(app.packageName,android.Manifest.permission.POST_NOTIFICATIONS)
        val dao=app.database.imports();val history=ImportHistory(app);val notices=ImportNotifications(app)
        val manager=app.getSystemService(android.app.NotificationManager::class.java)
        val source=File(app.cacheDir,"history-source.zip").apply { writeBytes(byteArrayOf(1,2,3)) }
        val jobs=listOf("COMPLETE","CANCELLED","FAILED").mapIndexed { index,state ->
            ImportJob("dismiss-terminal-$state","original-source","TAKEOUT",state=state,created=System.currentTimeMillis()+index,error="kept") }
        val items=jobs.map { ImportItem("${it.id}-item",it.id,source.toURI().toString(),"clip.mp4","clip.mp4","video/mp4",100,
            state="COMPLETE",offset=100,remoteId="verified",sessionUri="session",staged="stage",photoId="photo") }
        try {
            runBlocking { jobs.forEach { dao.saveJob(it) };items.forEach { dao.insertItem(it) } }
            jobs.forEach { notices.finish(it.id,ImportProgress.from(it,items)) }
            compose.onNodeWithTag("shell:activity").performClick()
            compose.onNodeWithTag("management:Import history").performClick()
            for(job in jobs) {
                compose.waitUntil(10000) { compose.onAllNodesWithTag("dismiss-import:${job.id}").fetchSemanticsNodes().isNotEmpty() }
                compose.onNodeWithTag("dismiss-import:${job.id}").performScrollTo().performClick()
                compose.waitUntil(10000) { compose.onAllNodesWithTag("import-progress:${job.id}").fetchSemanticsNodes().isEmpty() }
                notices.finish(job.id,ImportProgress.from(job,items)) // Late worker completion must stay dismissed.
                compose.waitUntil(5000) { manager.activeNotifications.none { it.tag==job.id } }
            }
            compose.activityRule.scenario.recreate()
            compose.onNodeWithTag("shell:activity").performClick()
            compose.onNodeWithTag("management:Import history").performClick()
            compose.waitUntil(10000) { compose.onAllNodesWithTag("toggle-dismissed-imports").fetchSemanticsNodes().isNotEmpty() }
            jobs.forEach { compose.onNodeWithTag("import-progress:${it.id}").assertDoesNotExist() }
            compose.onNodeWithTag("toggle-dismissed-imports").performScrollTo().performClick()
            val cancelled=jobs.single { it.state=="CANCELLED" }
            compose.onNodeWithTag("restore-import:${cancelled.id}").performScrollTo().performClick()
            compose.waitUntil(10000) { compose.onAllNodesWithTag("dismiss-import:${cancelled.id}").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("dismiss-import:${cancelled.id}").assertExists()
            runBlocking {
                jobs.forEach { assertEquals(it,dao.job(it.id)) }
                items.forEach { assertEquals(it,dao.item(it.id)) }
            }
            assertArrayEquals(byteArrayOf(1,2,3),source.readBytes())
        } finally {
            runBlocking { jobs.forEach { history.restore(it.id);notices.clearSummary(it.id) } }
            jobs.forEach { job ->
                app.database.openHelper.writableDatabase.execSQL("DELETE FROM import_items WHERE job=?",arrayOf(job.id))
                app.database.openHelper.writableDatabase.execSQL("DELETE FROM import_jobs WHERE id=?",arrayOf(job.id))
            }
            source.delete()
        }
    }

    @Test fun downloadedImageRemainsOpenWhileImportAddsNewerPhotosAndCacheIsTrimmed() = runBlocking<Unit> {
        val id="download-viewer-device"
        val file=File(app.cacheDir,"downloaded-viewer.png")
        val bitmap=Bitmap.createBitmap(640,480,Bitmap.Config.ARGB_8888).apply { eraseColor(Color.GREEN) }
        try { file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { bitmap.recycle() }
        val source=com.myphotosanywhere.mpa.drive.DriveSource.from(com.myphotosanywhere.mpa.drive.DriveConnection(
            accountId=id,email="viewer@test",mode="EXISTING_LIBRARY",folderId=id,folderName="Viewer fixture"))
        val photo=Photo(id,"downloaded-viewer.png",file.absolutePath,file.absolutePath,"image/png",file.length(),System.currentTimeMillis(),"viewer-hash",
            sourceBindingKey=source.key,sourceRemoteId="remote",cachedVersion="1")
        val oldSettings=app.settings.state.value
        val added=(1..20).map { "$id-new-$it" }
        try {
            app.settings.update(oldSettings.copy(faceIndexing=false,objectIndexing=false))
            app.database.drive().saveSource(source)
            app.database.photos().insert(photo)
            app.database.cloud().savePhoto(com.myphotosanywhere.mpa.drive.CloudPhoto("${source.key}:remote",source.key,"remote",id,photo.displayName,
                photo.mimeType,photo.bytes,"md5","1",null,null,null,null,photoId=id))
            app.database.imports().saveJob(ImportJob(id,"synthetic-import-source","TAKEOUT",state="RUNNING"))
            compose.waitUntil(10000) { compose.onAllNodesWithTag("photo:$id").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("photo:$id").performClick()
            compose.waitUntil(10000) { compose.onAllNodes(hasText("Original · 640 × 480",substring=true)).fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithText(photo.displayName).assertIsDisplayed()
            fun footer()=compose.onNodeWithTag("viewer-position").fetchSemanticsNode().config[SemanticsProperties.Text].joinToString { it.text }
            val before=footer()
            for((index,newId) in added.withIndex()) {
                app.database.photos().insert(photo.copy(id=newId,displayName="$newId.png",importedAt=photo.importedAt+index+1,
                    sourceBindingKey=null,sourceRemoteId=null,cachedVersion=null))
            }
            compose.waitForIdle()
            withTimeout(10000) { app.cache.enforce(clear=true) }
            compose.onNodeWithTag("viewer-screen").assertIsDisplayed()
            compose.onNodeWithText(photo.displayName).assertIsDisplayed()
            assertEquals(before,footer());assertTrue(file.isFile)
            assertEquals(file.absolutePath,app.database.photos().find(id)?.originalPath)
            compose.onNodeWithTag("viewer-close").performClick()
            compose.onNodeWithTag("viewer-screen").assertDoesNotExist()
        } finally {
            app.database.imports().job(id)?.let { app.database.imports().saveJob(it.copy(state="CANCELLED")) }
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM import_jobs WHERE id=?",arrayOf(id))
            (added+id).forEach { app.database.openHelper.writableDatabase.execSQL("DELETE FROM photos WHERE id=?",arrayOf(it)) }
            app.database.drive().disconnectSource(source.key)
            app.settings.update(oldSettings);file.delete()
        }
    }

    private class FixtureReader(private val failPreview: Boolean=false,private val failTile: Boolean=false): OriginalImageReader {
        override val width=512;override val height=512;override val orientation=1;override val supportsRegions=true
        val closed=AtomicBoolean(false);val calls=AtomicInteger()
        val previewBitmap=Bitmap.createBitmap(64,64,Bitmap.Config.ARGB_8888).apply { eraseColor(Color.WHITE) }
        val tiles=java.util.concurrent.CopyOnWriteArrayList<Bitmap>()
        override fun preview(): Bitmap { if(failPreview) throw OutOfMemoryError("Injected decoder allocation failure");return previewBitmap }
        override fun region(rect: Rect,sample: Int): Bitmap {
            val call=calls.incrementAndGet()
            if(failTile) throw OutOfMemoryError("Injected tile allocation failure")
            return Bitmap.createBitmap(64,64,Bitmap.Config.ARGB_8888).apply {
                eraseColor(if(call==1) Color.RED else Color.BLUE);tiles.add(this)
            }
        }
        override fun close() { closed.set(true) }
    }
    private fun attach(reader: FixtureReader,onQuality: (String)->Unit={}): OriginalPhotoView {
        lateinit var view: OriginalPhotoView
        instrumentation.runOnMainSync {
            view=OriginalPhotoView(compose.activity) { reader }.apply { this.onQuality=onQuality }
            compose.activity.addContentView(view,ViewGroup.LayoutParams(128,128))
            view.load(File(app.cacheDir,"injected-image"))
        }
        return view
    }
    private fun remove(view: OriginalPhotoView) {
        instrumentation.runOnMainSync { view.close();(view.parent as? ViewGroup)?.removeView(view) }
    }
    private fun waitPixel(view: OriginalPhotoView,color: Int) {
        val deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(10)
        while(System.nanoTime()<deadline) {
            var actual=0
            instrumentation.runOnMainSync {
                val image=Bitmap.createBitmap(128,128,Bitmap.Config.ARGB_8888)
                try { view.draw(Canvas(image));actual=image.getPixel(64,64) } finally { image.recycle() }
            }
            if(actual==color) return
            Thread.sleep(20)
        }
        fail("Viewer did not display expected pixels")
    }
    @Test fun bitmapFramesStayDrawableAfterTileReplacementAndViewerClose() {
        val reader=FixtureReader();val view=attach(reader)
        try {
            waitPixel(view,Color.RED)
            val oldFrame=reader.tiles.first()
            instrumentation.runOnMainSync { view.transform(2f,0f,0f) }
            waitPixel(view,Color.BLUE)
            assertFalse(oldFrame.isRecycled)
            remove(view)
            compose.waitUntil(10000) { reader.closed.get() }
            assertFalse(reader.previewBitmap.isRecycled);assertFalse(oldFrame.isRecycled)
            val frame=Bitmap.createBitmap(64,64,Bitmap.Config.ARGB_8888)
            try { Canvas(frame).drawBitmap(oldFrame,0f,0f,null);assertEquals(Color.RED,frame.getPixel(32,32)) }
            finally { frame.recycle() }
        } finally { remove(view) }
    }
    @Test fun decoderMemoryFailuresLeaveAnActionableMessageAndUsablePreview() {
        val failed=FixtureReader(failPreview=true);val previewError=CountDownLatch(1)
        val first=attach(failed) { if(it.contains("Not enough memory")) previewError.countDown() }
        try {
            assertTrue(previewError.await(10,TimeUnit.SECONDS));assertTrue(failed.closed.get())
        } finally { remove(first) }
        val bounded=FixtureReader(failTile=true);val tileError=CountDownLatch(1)
        val second=attach(bounded) { if(it.contains("Memory limit")) tileError.countDown() }
        try {
            assertTrue(tileError.await(10,TimeUnit.SECONDS));waitPixel(second,Color.WHITE)
            instrumentation.runOnMainSync { second.transform(4f,0f,0f) }
            assertEquals(1,bounded.calls.get())
        } finally { remove(second) }
    }
}

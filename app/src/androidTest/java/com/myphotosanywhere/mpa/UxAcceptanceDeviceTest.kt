package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import android.os.Handler
import android.os.HandlerThread
import android.view.FrameMetrics
import android.view.Window
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.unit.*
import androidx.compose.ui.graphics.toPixelMap
import androidx.lifecycle.ViewModelProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.importing.ImportJob
import com.myphotosanywhere.mpa.library.*
import com.myphotosanywhere.mpa.timeline.GalleryLayout
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.withLock
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

@RunWith(AndroidJUnit4::class)
class UxAcceptanceDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private val app get()=instrumentation.targetContext.applicationContext as MpaApplication
    private fun waitTag(tag: String)=compose.waitUntil(30000) { compose.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty() }
    private fun largeContent(dark: Boolean,content: @androidx.compose.runtime.Composable ()->Unit) {
        compose.runOnUiThread { compose.activity.setContent {
            val density=LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density,2f),LocalLayoutDirection provides LayoutDirection.Rtl) {
                MaterialTheme(colorScheme=if(dark) darkColorScheme() else lightColorScheme()) {
                    Surface(Modifier.fillMaxSize().systemBarsPadding()) { content() }
                }
            }
        } }
    }

    @Test fun sourceDisclosureAndActionsRemainReachableAtLargeFontsInBothThemesAndRtl()=runBlocking<Unit> {
        val source=DriveSource("ux-audit-source","ux-audit-account","synthetic@test","APP_FILES","ux-audit-folder","Long connected source for accessibility")
        val job=ImportJob("ux-audit-job","captured-other-destination","TAKEOUT",state="PAUSED",error="Retained checkpoint")
        app.database.drive().saveSource(source);app.database.drive().saveConnection(source.connection());app.database.imports().saveJob(job)
        try {
            for(dark in listOf(false,true)) {
                largeContent(dark) { DriveStorageScreen(emptyList(),{},{},{}) }
                waitTag("source:transfer-settings")
                compose.onNodeWithTag("source:transfer-settings").performScrollTo().performClick()
                compose.onNodeWithTag("source:transfer-settings").assert(SemanticsMatcher.expectValue(SemanticsProperties.StateDescription,"Expanded"))
                compose.onNodeWithText("Transfer on unmetered networks only").performScrollTo().assertIsDisplayed()
                compose.onNodeWithTag("source:remove").performScrollTo().assertIsDisplayed()
                screenshot(if(dark) "sources-refined-dark-rtl" else "sources-refined-light-rtl")
                compose.onNodeWithTag("source:remove").performClick()
                compose.onNodeWithText("Keep source").performClick()
                compose.onNodeWithTag("source:help").performScrollTo().performClick()
                compose.onNodeWithText("Removing a source stops its transfers.",substring=true).performScrollTo().assertIsDisplayed()
                assertEquals(source,app.database.drive().source(source.key));assertEquals(job,app.database.imports().job(job.id))
                compose.onNodeWithTag("source:help").performScrollTo().performClick()
                compose.onNodeWithTag("source:transfer-settings").performScrollTo().performClick()
                compose.onNodeWithTag("source:transfer-settings").assert(SemanticsMatcher.expectValue(SemanticsProperties.StateDescription,"Collapsed"))
            }
        } finally {
            app.database.drive().disconnectSource(source.key);app.database.drive().disconnect()
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM import_jobs WHERE id=?",arrayOf(job.id))
        }
    }

    @Test fun localOnlyDetailsKeepCloseAndDateAccessibleAndNeverOfferDeviceRemoval()=runBlocking<Unit> {
        val photo=Photo("ux-import-date","Long local-only photo filename retained for accessibility.png","","","image/png",1,2_000_000_000_000L,"local-only",placeLabel="Reviewed place")
        app.database.photos().insert(photo)
        val model=compose.runOnUiThread { ViewModelProvider(compose.activity)[LibraryFeaturesModel::class.java] }
        try {
            for(dark in listOf(false,true)) {
                largeContent(dark) { PhotoDetails(photo,model,emptyList(),emptyList(),emptyList(),emptyList(),{},{},{},{},onRemoveCopy={ error("Local-only originals must never offer removal") }) }
                waitTag("photo-details-close")
                compose.waitUntil(10000) { compose.onAllNodesWithText("Import date · capture date unavailable").fetchSemanticsNodes().isNotEmpty() }
                val date=java.text.DateFormat.getDateInstance(java.text.DateFormat.FULL).format(java.util.Date(photo.importedAt))
                compose.onNodeWithText(date).performScrollTo().assertIsDisplayed()
                compose.onNodeWithTag("photo-details-close").assertIsDisplayed()
                val bounds=compose.onNodeWithTag("photo-details-close").fetchSemanticsNode().boundsInWindow
                val px=compose.activity.resources.displayMetrics.density
                assertTrue(bounds.width>=48*px-1 && bounds.height>=48*px-1)
                compose.onNodeWithTag("details:remove-copy").assertDoesNotExist()
                screenshot(if(dark) "details-audit-dark-rtl" else "details-audit-light-rtl")
                compose.onNodeWithText("Reviewed place").performScrollTo().assertIsDisplayed()
                assertEquals(photo,app.database.photos().find(photo.id))
            }
        } finally { app.database.photos().update(photo.copy(archived=true)) }
    }

    @Test fun scrollingFrameMetricsDuringIndexInsertionsAreMeasuredWithBoundedSamplesAndPreservedBytes()=runBlocking<Unit> {
        val file=File.createTempFile("ux-scroll-fixture",".png",app.filesDir)
        val bitmap=Bitmap.createBitmap(180,240,Bitmap.Config.ARGB_8888)
        try { bitmap.eraseColor(android.graphics.Color.rgb(32,130,164));file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } }
        finally { bitmap.recycle() }
        val hash=DriveBackupWorker.digest(file,"SHA-256")
        val photos=(0 until 320).map { index -> Photo("ux-frame-$index","Frame $index.png",file.path,file.path,"image/png",file.length(),5_000_000_000_000L-index*86400000L,hash,capturedAt=5_000_000_000_000L-index*86400000L) }
        val layout=app.galleryPresentation.layout.value
        val thread=HandlerThread("mpa-test-frame-observer").apply { start() }
        val handler=Handler(thread.looper);val durations=mutableListOf<Long>();val heap=mutableListOf<Long>();var dropped=0
        val listener=Window.OnFrameMetricsAvailableListener { _,metrics,lost ->
            val total=metrics.getMetric(FrameMetrics.TOTAL_DURATION)
            synchronized(durations) {
                dropped+=lost
                if(total>=0 && durations.size<1024) { durations+=total;val runtime=Runtime.getRuntime();heap+=runtime.totalMemory()-runtime.freeMemory() }
            }
        }
        var observedWindow: Window?=null
        try {
            app.galleryPresentation.updateLayout(GalleryLayout.MOSAIC)
            photos.forEach { app.database.photos().insert(it) }
            waitTag("photo:ux-frame-0");compose.waitForIdle()
            waitTag("timeline:date-rail")
            compose.onNodeWithTag("timeline:date-rail").assert(SemanticsMatcher.keyIsDefined(SemanticsProperties.Heading))
            compose.runOnUiThread { observedWindow=compose.activity.window;requireNotNull(observedWindow).addOnFrameMetricsAvailableListener(listener,handler) }
            coroutineScope {
                val insertion=launch(Dispatchers.IO) {
                    repeat(12) { index -> delay(120);app.database.photos().insert(photos[index].copy(id="ux-frame-new-$index",capturedAt=6_000_000_000_000L+index)) }
                }
                repeat(8) { compose.onNodeWithTag("photos:grid").performTouchInput { swipeUp(durationMillis=180) };compose.waitForIdle() }
                repeat(4) { compose.onNodeWithTag("photos:grid").performTouchInput { swipeDown(durationMillis=180) };compose.waitForIdle() }
                insertion.join()
            }
            compose.runOnUiThread { requireNotNull(observedWindow).removeOnFrameMetricsAvailableListener(listener) };observedWindow=null
            val drained=CountDownLatch(1);check(handler.post { drained.countDown() });assertTrue(drained.await(5,TimeUnit.SECONDS))
            val values=synchronized(durations) { durations.sorted() };assertTrue("Actual hardware-window frame evidence is required",values.size>=10)
            val report=JSONObject().put("api",android.os.Build.VERSION.SDK_INT).put("fixtureItems",320).put("insertedItems",12)
                .put("sampleLimit",1024).put("frames",values.size).put("droppedReports",dropped).put("frameMetric","FrameMetrics.TOTAL_DURATION nanoseconds")
                .put("medianMs",values[values.size/2]/1_000_000.0).put("p95Ms",values[((values.size-1)*.95).toInt()]/1_000_000.0)
                .put("maxMs",values.last()/1_000_000.0).put("framesOver16_67ms",values.count { it>16_670_000 })
                .put("heapMinBytes",heap.minOrNull()).put("heapMaxBytes",heap.maxOrNull()).put("pageBudget",300)
                .put("scope","Synthetic emulator scrolling during index inserts; diagnostic measurements, not S23 Ultra frame-rate acceptance. Query/snapshot 10k/100k and bounded-page assertions are separate native benchmarks.")
            export("ux-scroll-benchmark.json","application/json",report.toString(2).toByteArray())
            screenshot("ux-scroll-measured")
            assertTrue(file.isFile);assertEquals(hash,DriveBackupWorker.digest(file,"SHA-256"))
            for(photo in photos) assertEquals(photo,app.database.photos().find(photo.id)?.copy(lastAccess=photo.lastAccess))
        } finally {
            observedWindow?.let { window -> compose.runOnUiThread { window.removeOnFrameMetricsAvailableListener(listener) } }
            thread.quitSafely();thread.join(5000);check(!thread.isAlive) { "Frame observer did not stop" }
            for(id in photos.map { it.id }+(0 until 12).map { "ux-frame-new-$it" }) app.database.photos().find(id)?.let { app.database.photos().update(it.copy(archived=true,originalPath="")) }
            app.galleryPresentation.updateLayout(layout);file.delete()
        }
    }

    @Test fun basePreviewRegeneratedAtSamePathReplacesFailedPainterWithoutChangingPhoto()=runBlocking<Unit> {
        val file=File.createTempFile("ux-base-preview",".png",app.filesDir)
        val preview=File(app.filesDir,"thumbnails/ux-base-preview-recovered.jpg")
        check(!preview.exists() || preview.delete())
        val bitmap=Bitmap.createBitmap(240,360,Bitmap.Config.ARGB_8888)
        try { bitmap.eraseColor(android.graphics.Color.rgb(28,130,170));file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } }
        finally { bitmap.recycle() }
        val hash=DriveBackupWorker.digest(file,"SHA-256")
        val photo=Photo("ux-base-preview-recovered","Recovered preview.png",file.path,preview.path,"image/png",file.length(),7_000_000_000_000L,hash,capturedAt=7_000_000_000_000L)
        val locked=CompletableDeferred<Unit>();val release=CompletableDeferred<Unit>()
        val held=launch(Dispatchers.IO) { app.cache.lock.withLock { locked.complete(Unit);release.await() } }
        val oldLayout=app.galleryPresentation.layout.value
        try {
            locked.await();app.galleryPresentation.updateLayout(GalleryLayout.SQUARE);app.database.photos().insert(photo)
            waitTag("photo:${photo.id}")
            val error=hasContentDescription("Thumbnail unavailable. Open photo to retry.") and hasAnyAncestor(hasTestTag("photo:${photo.id}"))
            compose.waitUntil(10000) { compose.onAllNodes(error,useUnmergedTree=true).fetchSemanticsNodes().isNotEmpty() }
            assertFalse(preview.exists());release.complete(Unit);held.join()
            compose.waitUntil(15000) {
                preview.isFile && compose.onAllNodes(error,useUnmergedTree=true).fetchSemanticsNodes().isEmpty()
            }
            val pixels=compose.onNodeWithTag("photo:${photo.id}").captureToImage().toPixelMap()
            val color=pixels[pixels.width/2,pixels.height/2]
            assertEquals(28/255f,color.red,.08f);assertEquals(130/255f,color.green,.08f);assertEquals(170/255f,color.blue,.08f)
            screenshot("base-preview-recovered")
            assertEquals(photo,app.database.photos().find(photo.id));assertEquals(hash,DriveBackupWorker.digest(file,"SHA-256"))
        } finally {
            release.complete(Unit);held.join()
            app.database.photos().find(photo.id)?.let { app.database.photos().update(it.copy(archived=true,originalPath="")) }
            app.galleryPresentation.updateLayout(oldLayout);file.delete();preview.delete()
        }
    }

    private fun export(name: String,mime: String,bytes: ByteArray) {
        val values=android.content.ContentValues().apply {
            put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,name);put(android.provider.MediaStore.MediaColumns.MIME_TYPE,mime)
            put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
        }
        val uri=requireNotNull(app.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
        requireNotNull(app.contentResolver.openOutputStream(uri)).use { it.write(bytes) }
    }
    private fun screenshot(name: String) {
        compose.waitForIdle();val bitmap=requireNotNull(instrumentation.uiAutomation.takeScreenshot())
        try { val bytes=java.io.ByteArrayOutputStream();check(bitmap.compress(Bitmap.CompressFormat.PNG,100,bytes));export("$name.png","image/png",bytes.toByteArray()) }
        finally { bitmap.recycle() }
    }
}

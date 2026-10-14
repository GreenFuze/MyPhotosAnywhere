package com.myphotosanywhere.mpa.timeline

import android.graphics.Bitmap
import android.graphics.Color
import androidx.compose.ui.test.*
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.MainActivity
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.drive.DriveSource
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class TimelineContinuityTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    private fun waitTag(tag: String)=compose.waitUntil(30000) { compose.onAllNodes(if(tag.startsWith("photo:") || tag.startsWith("cloud:")) hasTestTag(tag) and isEnabled() else hasTestTag(tag)).fetchSemanticsNodes().isNotEmpty() }

    @Test fun olderViewerKeepsSequenceDuringImportsAndReturnsToPixelAnchorAfterRotation()=runBlocking<Unit> {
        val source=DriveSource("timeline-ui","timeline-ui","timeline@test","APP_FILES","timeline-ui","Timeline fixture")
        val file=File.createTempFile("timeline-ui",".png",app.filesDir)
        val bitmap=Bitmap.createBitmap(160,100,Bitmap.Config.ARGB_8888)
        try { bitmap.eraseColor(Color.CYAN);file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } }
        finally { bitmap.recycle() }
        val hash=com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(file,"SHA-256")
        val base=System.currentTimeMillis()+20*86400000L
        val photos=(0 until 120).map { index ->
            Photo("timeline-ui-$index","timeline-ui-$index.png",file.path,file.path,"image/png",file.length(),base-index*86400000L,
                hash,capturedAt=base-index*86400000L,sourceBindingKey=source.key)
        }
        val added=photos[0].copy(id="timeline-ui-import-new",displayName="new-import.png",capturedAt=base+86400000L)
        val older=photos.last().copy(id="timeline-ui-import-old",displayName="old-import.png",capturedAt=base-200*86400000L)
        try {
            app.database.drive().saveSource(source)
            photos.forEach { app.database.photos().insert(it) }
            compose.onNodeWithTag("shell:filters").performClick()
            compose.onNodeWithText("Timeline fixture · timeline@test").performScrollTo().performClick()
            compose.onNodeWithText("Done").performScrollTo().performClick()
            waitTag("photo:timeline-ui-0")
            compose.onNodeWithTag("photos:grid").performScrollToNode(hasTestTag("photo:timeline-ui-70"))
            waitTag("photo:timeline-ui-70")
            // A mosaic landscape can fit at the viewport bottom while its center remains
            // covered by floating navigation. Reveal it before measuring and injecting a tap.
            val tile=compose.onNodeWithTag("photo:timeline-ui-70").fetchSemanticsNode().boundsInWindow
            val nav=compose.onNodeWithTag("shell:navigation").fetchSemanticsNode().boundsInWindow
            val reveal=tile.bottom-nav.top+8f
            if(reveal>0) compose.onNodeWithTag("photos:grid").performSemanticsAction(SemanticsActions.ScrollBy) { it(0f,reveal) }
            compose.waitUntil(10000) {
                compose.onNodeWithTag("photo:timeline-ui-70").fetchSemanticsNode().boundsInWindow.bottom<=compose.onNodeWithTag("shell:navigation").fetchSemanticsNode().boundsInWindow.top-4f
            }
            // Activity recreation can relocate the Compose root by a system inset.
            // Window coordinates describe the actual visible position across roots.
            val before=compose.onNodeWithTag("photo:timeline-ui-70").fetchSemanticsNode().boundsInWindow.top
            val beforeGridTop=compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInWindow.top
            recordViewport("before")
            compose.onNodeWithTag("photo:timeline-ui-70").performClick()
            waitTag("viewer-screen");waitTag("original-image")
            compose.waitUntil(30000) { compose.onAllNodesWithText("71 / 120",substring=true).fetchSemanticsNodes().isNotEmpty() }
            app.database.photos().insert(added);app.database.photos().insert(older)
            compose.onNodeWithContentDescription("Next photo").performClick()
            compose.waitUntil(10000) { compose.onAllNodesWithText("timeline-ui-71.png").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithText("72 / 120",substring=true).assertExists()
            compose.activityRule.scenario.recreate()
            waitTag("viewer-screen")
            compose.waitUntil(10000) { compose.onAllNodesWithText("timeline-ui-71.png").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithText("timeline-ui-71.png").assertExists()
            compose.onNodeWithText("72 / 120",substring=true).assertExists()
            compose.onNodeWithContentDescription("Previous photo").performClick()
            compose.waitUntil(10000) { compose.onAllNodesWithText("timeline-ui-70.png").fetchSemanticsNodes().isNotEmpty() }
            recordDiagnostics(source,"before-close")
            compose.onNodeWithTag("viewer-close").performClick()
            // A retained pre-refresh tile can appear before the new anchored generation
            // finishes its asynchronous seek. Measure only the completed restoration.
            try {
                compose.waitUntil(30000) {
                compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty() &&
                    compose.onAllNodes(hasTestTag("photo:timeline-ui-70") and isEnabled()).fetchSemanticsNodes().size==1
            }
            } catch(error: Throwable) {
                recordDiagnostics(source,"failed-return");screenshotViewport("timeline-failed-return");throw error
            }
            compose.waitForIdle()
            val after=compose.onNodeWithTag("photo:timeline-ui-70").fetchSemanticsNode().boundsInWindow.top
            val afterGridTop=compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInWindow.top
            recordViewport("after")
            assertEquals("Viewer return must restore the same pixel offset (grid origin $beforeGridTop -> $afterGridTop)",before,after,3f)
            assertEquals("Viewer return must preserve the offset within the grid",before-beforeGridTop,after-afterGridTop,3f)
            compose.onNodeWithTag("nav:Collections").performClick()
            compose.onNodeWithTag("nav:Photos").performClick()
            // Source filtering and its anchor survive a primary-destination round trip.
            compose.onNodeWithTag("shell:filters").performClick()
            compose.onNodeWithText("Timeline fixture · timeline@test").performScrollTo().performClick()
            compose.onNodeWithText("Done").performScrollTo().performClick()
            waitTag("photo:timeline-ui-70")
            compose.onNodeWithTag("shell:filters").performClick()
            compose.onNodeWithTag("timeline:jump").assertExists().performClick()
            compose.onNodeWithText("Cancel").performClick()
            photos.forEach { assertEquals(it,app.database.photos().find(it.id)?.copy(lastAccess=it.lastAccess)) }
            assertTrue(file.isFile)
            assertEquals(hash,com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(file,"SHA-256"))
        } finally {
            (photos+added+older).forEach { photo -> app.database.photos().find(photo.id)?.let { app.database.photos().update(it.copy(archived=true)) } }
            app.database.drive().disconnectSource(source.key)
            file.delete()
        }
    }

    private fun recordViewport(name: String) {
        val photo=compose.onNodeWithTag("photo:timeline-ui-70").fetchSemanticsNode()
        val grid=compose.onNodeWithTag("photos:grid").fetchSemanticsNode()
        android.util.Log.i("TimelineViewport","$name photo root=${photo.boundsInRoot} window=${photo.boundsInWindow}; grid root=${grid.boundsInRoot} window=${grid.boundsInWindow}")
        screenshotViewport("timeline-$name")
    }

    private fun recordDiagnostics(source: DriveSource,label: String) {
        val model=androidx.lifecycle.ViewModelProvider(compose.activity)[TimelineViewModel::class.java]
        android.util.Log.i("TimelineDiagnostics","$label anchor=${model.anchor(TimelineQuery(source=source.key))}; generation=${model.restoration.value}; refreshed=${model.refreshedGeneration.value}; viewer=${model.viewer.value}")
        if(label=="failed-return") {
            val roots=compose.onAllNodes(isRoot(),useUnmergedTree=true)
            for(index in roots.fetchSemanticsNodes().indices) roots[index].printToLog("TimelineDiagnostics")
        }
    }

    private fun screenshotViewport(name: String) {
        if(android.os.Build.VERSION.SDK_INT<29) return
        val bitmap=requireNotNull(InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot())
        try {
            val values=android.content.ContentValues().apply {
                put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"$name.png")
                put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png")
                put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
            }
            val uri=requireNotNull(app.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
            requireNotNull(app.contentResolver.openOutputStream(uri)).use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) }
        } finally { bitmap.recycle() }
    }
}

package com.myphotosanywhere.mpa.timeline

import android.graphics.Bitmap
import android.graphics.Color
import androidx.compose.ui.test.*
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
    private fun waitTag(tag: String)=compose.waitUntil(30000) { compose.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty() }

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
            val before=compose.onNodeWithTag("photo:timeline-ui-70").fetchSemanticsNode().boundsInRoot.top
            compose.onNodeWithTag("photo:timeline-ui-70").performClick()
            waitTag("viewer-screen");waitTag("original-image")
            compose.waitUntil(30000) { compose.onAllNodesWithText("71 / 120",substring=true).fetchSemanticsNodes().isNotEmpty() }
            app.database.photos().insert(added);app.database.photos().insert(older)
            compose.onNodeWithText("Next").performClick()
            compose.waitUntil(10000) { compose.onAllNodesWithText("timeline-ui-71.png").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithText("72 / 120",substring=true).assertExists()
            compose.activityRule.scenario.recreate()
            waitTag("viewer-screen")
            compose.waitUntil(10000) { compose.onAllNodesWithText("timeline-ui-71.png").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithText("timeline-ui-71.png").assertExists()
            compose.onNodeWithText("72 / 120",substring=true).assertExists()
            compose.onNodeWithText("Previous").performClick()
            compose.waitUntil(10000) { compose.onAllNodesWithText("timeline-ui-70.png").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("viewer-close").performClick()
            waitTag("photo:timeline-ui-70")
            compose.waitForIdle()
            val after=compose.onNodeWithTag("photo:timeline-ui-70").fetchSemanticsNode().boundsInRoot.top
            assertEquals("Viewer return must restore the same pixel offset",before,after,3f)
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
}

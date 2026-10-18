package com.myphotosanywhere.mpa.timeline

import android.graphics.Bitmap
import android.graphics.Color
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.semantics.getOrNull
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.lifecycle.ViewModelProvider
import androidx.room.withTransaction
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.MainActivity
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.drive.DriveBackupWorker
import com.myphotosanywhere.mpa.drive.DriveSource
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.time.Instant

@RunWith(AndroidJUnit4::class)
class GalleryArrivalDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    private fun waitPhoto(tag: String)=compose.waitUntil(30000) { compose.onAllNodes(hasTestTag(tag) and isEnabled()).fetchSemanticsNodes().isNotEmpty() }
    @Test fun mosaicKeepsVisiblePixelsDuringArrivalsAndLatePreviewThenRevealsNewest()=exercise(GalleryLayout.MOSAIC)
    @Test fun rowsKeepVisiblePixelsDuringArrivalsAndLatePreviewThenRevealNewest()=exercise(GalleryLayout.ROWS)
    @Test fun squaresKeepVisiblePixelsDuringArrivalsAndLatePreviewThenRevealNewest()=exercise(GalleryLayout.SQUARE)

    private fun exercise(layout: GalleryLayout)=runBlocking<Unit> {
        val prefix="arrival-${layout.name.lowercase()}-${java.util.UUID.randomUUID()}"
        val source=DriveSource(prefix,prefix,"gallery@test","APP_FILES",prefix,"Arrival fixture")
        val previousLayout=app.galleryPresentation.layout.value;val previousDensity=app.galleryPresentation.density.value
        val files=mutableListOf<File>();val photos=mutableListOf<Photo>()
        fun picture(width: Int,height: Int,color: Int): File {
            val file=File.createTempFile("arrival-",".png",app.filesDir);val image=Bitmap.createBitmap(width,height,Bitmap.Config.ARGB_8888)
            try { image.eraseColor(color);file.outputStream().use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) } }
            finally { image.recycle() }
            files+=file;return file
        }
        val portrait=picture(80,128,Color.BLUE);val landscape=picture(128,80,Color.GREEN)
        val missing=File(app.filesDir,"$prefix-late.png");files+=missing
        val base=Instant.parse("2200-06-15T12:00:00Z").toEpochMilli()
        try {
            app.galleryPresentation.update(GridDensity.DEFAULT);app.galleryPresentation.updateLayout(layout)
            app.database.drive().saveSource(source)
            app.database.withTransaction {
                for(index in 0 until 180) {
                    val file=if(index%4==0) portrait else landscape
                    val id="$prefix-${index.toString().padStart(3,'0')}"
                    val photo=Photo(id,"$id.png",if(index==70) "" else file.path,if(index==70) missing.path else file.path,
                        "image/png",file.length(),base,DriveBackupWorker.digest(file,"SHA-256"),capturedAt=base-index*60000L,sourceBindingKey=source.key)
                    photos+=photo;app.database.photos().insert(photo)
                }
            }
            compose.onNodeWithTag("shell:filters").performClick()
            compose.onNodeWithText("Arrival fixture · gallery@test").performScrollTo().performClick()
            compose.onNodeWithText("Done").performScrollTo().performClick();waitPhoto("photo:${photos[0].id}")
            compose.onNodeWithTag("photos:grid").performScrollToNode(hasTestTag("photo:${photos[70].id}"));waitPhoto("photo:${photos[70].id}")
            val target=compose.onNodeWithTag("photo:${photos[70].id}").fetchSemanticsNode().boundsInWindow
            val nav=compose.onNodeWithTag("shell:navigation").fetchSemanticsNode().boundsInWindow
            if(target.bottom>nav.top-8) compose.onNodeWithTag("photos:grid").performSemanticsAction(SemanticsActions.ScrollBy) { it(0f,target.bottom-nav.top+16) }
            compose.waitForIdle()
            fun visible(): Map<String,Rect> {
                val top=compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInWindow.top
                val bottom=compose.onNodeWithTag("shell:navigation").fetchSemanticsNode().boundsInWindow.top
                return compose.onAllNodes(SemanticsMatcher("fixture photo") { it.config.getOrNull(SemanticsProperties.TestTag)?.startsWith("photo:$prefix-")==true })
                    .fetchSemanticsNodes().filter { it.boundsInWindow.top>=top+4 && it.boundsInWindow.bottom<=bottom-4 && it.boundsInWindow.width>0 }
                    .associate { it.config[SemanticsProperties.TestTag] to it.boundsInWindow }
            }
            val before=visible();assertTrue("Need several fully visible reference tiles",before.size>=2)
            val newer=photos[0].copy(id="$prefix-new",displayName="new.png",capturedAt=base+60000L)
            val middle=photos[0].copy(id="$prefix-middle",displayName="middle.png",capturedAt=base-69*60000L+1000)
            val older=photos[0].copy(id="$prefix-old",displayName="old.png",capturedAt=base-1000*60000L)
            app.database.withTransaction { for(photo in listOf(newer,older)) { photos+=photo;app.database.photos().insert(photo) } }
            compose.waitUntil(30000) { compose.onAllNodesWithTag("timeline:new-photos").fetchSemanticsNodes().isNotEmpty() && compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty() }
            fun assertUnmoved() {
                for((key,bounds) in before) {
                    val actual=compose.onNodeWithTag(key).fetchSemanticsNode().boundsInWindow
                    assertEquals("$layout $key left",bounds.left,actual.left,3f);assertEquals("$layout $key top",bounds.top,actual.top,3f)
                    assertEquals("$layout $key width",bounds.width,actual.width,3f);assertEquals("$layout $key height",bounds.height,actual.height,3f)
                }
            }
            assertUnmoved()
            photos+=middle;app.database.photos().insert(middle)
            compose.waitUntil(30000) { compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty() }
            val preview=picture(256,64,Color.MAGENTA);val late=photos[70]
            app.database.photos().update(late.copy(thumbnailPath=preview.path,favorite=true))
            compose.waitUntil(30000) { compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty() && compose.onAllNodesWithTag("timeline:new-photos").fetchSemanticsNodes().isNotEmpty() }
            compose.waitForIdle();assertUnmoved();screenshot("arrival-${layout.name.lowercase()}-stable")
            compose.onNodeWithTag("timeline:new-photos").performClick();waitPhoto("photo:${newer.id}")
            compose.waitUntil(30000) { compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty() }
            compose.onNodeWithTag("timeline:new-photos").assertDoesNotExist()
            compose.onNodeWithTag("photo:${newer.id}").assertIsDisplayed();screenshot("arrival-${layout.name.lowercase()}-newest")
            assertEquals(DriveBackupWorker.digest(portrait,"SHA-256"),photos[0].sha256)
            assertTrue(portrait.isFile);assertTrue(landscape.isFile);assertTrue(preview.isFile)
            for(photo in photos) {
                val actual=requireNotNull(app.database.photos().find(photo.id))
                val expected=if(photo.id==late.id) photo.copy(thumbnailPath=preview.path,favorite=true) else photo
                assertEquals(expected,actual.copy(lastAccess=expected.lastAccess))
            }
        } finally {
            app.cache.lock.lock()
            try { app.database.withTransaction {
                photos.forEach { app.database.openHelper.writableDatabase.execSQL("DELETE FROM photos WHERE id=?",arrayOf(it.id)) }
                app.database.drive().disconnectSource(source.key)
            } } finally { app.cache.lock.unlock() }
            files.forEach { it.delete() }
            app.galleryPresentation.updateLayout(previousLayout);app.galleryPresentation.update(previousDensity)
        }
    }
    private fun screenshot(name: String) {
        compose.waitForIdle();val instrumentation=InstrumentationRegistry.getInstrumentation();instrumentation.waitForIdleSync()
        val bitmap=requireNotNull(instrumentation.uiAutomation.takeScreenshot())
        try {
            com.myphotosanywhere.mpa.NativeScreenshotEvidence(app).write(name,bitmap)
        } finally { bitmap.recycle() }
    }
}

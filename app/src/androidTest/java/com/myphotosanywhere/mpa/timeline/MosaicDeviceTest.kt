package com.myphotosanywhere.mpa.timeline

import androidx.room.withTransaction
import android.graphics.Bitmap
import android.graphics.Color
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.lifecycle.ViewModelProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.MainActivity
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.Photo
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.time.Instant

@RunWith(AndroidJUnit4::class)
class MosaicDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    private fun waitPhoto(key: String)=compose.waitUntil(30000) { compose.onAllNodes(hasTestTag(key) and isEnabled()).fetchSemanticsNodes().isNotEmpty() }
    private fun timeline()=compose.runOnUiThread { ViewModelProvider(compose.activity)[TimelineViewModel::class.java] }
    private fun picture(width: Int,height: Int,color: Int): File {
        val file=File.createTempFile("mosaic-",".png",app.filesDir);val image=Bitmap.createBitmap(width,height,Bitmap.Config.ARGB_8888)
        try { image.eraseColor(color);file.outputStream().use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { image.recycle() }
        return file
    }
    private suspend fun fixture(files: List<File>,prefix: String="mosaic"): List<Photo> {
        val base=Instant.parse("2190-05-15T12:00:00Z").toEpochMilli()
        return (0 until 90).map { i ->
            val file=files[i%files.size];val id="$prefix-${i.toString().padStart(3,'0')}"
            Photo(id,"$id.png",file.path,file.path,"image/png",file.length(),base,"sha-$i",capturedAt=base-i*60000).also { app.database.photos().insert(it) }
        }
    }
    @Test fun mixedShapesSelectionLayoutRotationAndViewerKeepIdentity()=runBlocking<Unit> {
        val previous=app.galleryPresentation.layout.value;val density=app.galleryPresentation.density.value
        val files=listOf(picture(160,240,Color.rgb(42,120,165)),picture(240,240,Color.rgb(70,145,90)),picture(360,240,Color.rgb(195,130,60)))
        var photos=emptyList<Photo>()
        try {
            app.galleryPresentation.update(GridDensity.DEFAULT);app.galleryPresentation.updateLayout(GalleryLayout.MOSAIC);photos=fixture(files)
            waitPhoto("photo:mosaic-002");compose.waitForIdle()
            val cells=(0..2).map { compose.onNodeWithTag("photo:mosaic-00$it").fetchSemanticsNode() }
            assertTrue(cells[0].boundsInWindow.width<cells[1].boundsInWindow.width && cells[1].boundsInWindow.width<cells[2].boundsInWindow.width)
            assertEquals(cells[0].boundsInWindow.top,cells[2].boundsInWindow.top,2f)
            assertEquals(cells[0].boundsInWindow.height,cells[2].boundsInWindow.height,2f)
            assertEquals(3,compose.onNodeWithTag("photos:grid").fetchSemanticsNode().config[SemanticsProperties.CollectionInfo].columnCount)
            assertEquals(2,cells[2].config[SemanticsProperties.CollectionItemInfo].columnIndex)
            screenshot("mosaic-gallery")
            android.util.Log.i("MosaicTransitions","Enter selection")
            compose.onNodeWithTag("photo:mosaic-000").performTouchInput { longClick() };compose.onNodeWithTag("photo:mosaic-000").assertIsSelected()
            compose.onNodeWithTag("timeline:date-rail").assertDoesNotExist();compose.onNodeWithTag("selection:close").performClick()
            val model=timeline();val anchor=compose.runOnUiThread { requireNotNull(model.anchor()) }
            val before=compose.onNodeWithTag(anchor.cursor.key).fetchSemanticsNode().boundsInWindow.top
            android.util.Log.i("MosaicTransitions","Switch to Square")
            compose.runOnUiThread { model.changeLayout(GalleryLayout.SQUARE) }
            compose.waitUntil(30000) {
                val target=compose.onAllNodesWithTag(anchor.cursor.key).fetchSemanticsNodes().singleOrNull()
                app.galleryPresentation.layout.value==GalleryLayout.SQUARE &&
                    compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty() && target!=null &&
                    kotlin.math.abs(target.boundsInWindow.top-before)<4f
            }
            compose.waitForIdle();val square=(0..2).map { compose.onNodeWithTag("photo:mosaic-00$it").fetchSemanticsNode().boundsInWindow.width }
            assertEquals(square[0],square[2],2f);screenshot("mosaic-square")
            // Repeated physical slot changes must dispose the preceding grid's layout
            // nodes while retaining the same canonical photo and its measured offset.
            repeat(3) {
                for(layout in listOf(GalleryLayout.MOSAIC,GalleryLayout.SQUARE)) {
                    android.util.Log.i("MosaicTransitions","Repeat $it switch to $layout")
                    compose.runOnUiThread { model.changeLayout(layout) }
                    compose.waitUntil(30000) {
                        app.galleryPresentation.layout.value==layout &&
                            compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty() &&
                            compose.onAllNodes(hasTestTag(anchor.cursor.key) and isEnabled()).fetchSemanticsNodes().size==1
                    }
                    compose.waitForIdle();assertEquals(before,compose.onNodeWithTag(anchor.cursor.key).fetchSemanticsNode().boundsInWindow.top,4f)
                    assertEquals(anchor.cursor.key,compose.runOnUiThread { model.anchor()?.cursor?.key })
                }
            }
            android.util.Log.i("MosaicTransitions","Final Mosaic before recreation")
            compose.runOnUiThread { model.changeLayout(GalleryLayout.MOSAIC) }
            compose.waitUntil(30000) { app.galleryPresentation.layout.value==GalleryLayout.MOSAIC && compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty() }
            android.util.Log.i("MosaicTransitions","Recreate activity")
            compose.activityRule.scenario.recreate();waitPhoto(anchor.cursor.key)
            android.util.Log.i("MosaicTransitions","Open viewer")
            compose.onNodeWithTag("photo:mosaic-000").performClick();compose.waitUntil(30000) { compose.onAllNodesWithTag("viewer-screen").fetchSemanticsNodes().isNotEmpty() }
            android.util.Log.i("MosaicTransitions","Close viewer")
            compose.onNodeWithTag("viewer-close").performClick();waitPhoto(anchor.cursor.key)
            assertEquals(before,compose.onNodeWithTag(anchor.cursor.key).fetchSemanticsNode().boundsInWindow.top,4f)
            photos.forEach { assertEquals(it,app.database.photos().find(it.id)?.copy(lastAccess=it.lastAccess)) }
        } finally { photos.forEach { app.database.photos().find(it.id)?.let { row -> app.database.photos().update(row.copy(archived=true)) } };files.forEach { it.delete() };app.galleryPresentation.updateLayout(previous);app.galleryPresentation.update(density) }
    }
    @Test fun importInvalidationKeepsVisibleMosaicPhotoAndPixelOffset()=runBlocking<Unit> {
        val previous=app.galleryPresentation.layout.value;val density=app.galleryPresentation.density.value
        val files=listOf(picture(160,240,Color.BLUE),picture(240,240,Color.GREEN),picture(360,240,Color.RED))
        var photos=emptyList<Photo>();val additions=mutableListOf<Photo>()
        try {
            app.galleryPresentation.updateLayout(GalleryLayout.MOSAIC);photos=fixture(files,"mosaic-continuity");waitPhoto("photo:mosaic-continuity-000")
            val model=timeline();val focused=requireNotNull(app.timeline.find(TimelineQuery(),"photo:mosaic-continuity-045"))
            compose.runOnUiThread { model.rememberAnchor(focused,0);model.restoreAnchor() }
            waitPhoto(focused.key)
            compose.waitUntil(30000) { compose.onAllNodesWithTag("timeline:date-rail").fetchSemanticsNodes().isNotEmpty() && model.refreshedGeneration.value==model.restoration.value }
            compose.runOnUiThread { model.changeDensity(GridDensity.LARGE) }
            compose.waitUntil(30000) { compose.onNodeWithTag("photos:grid").fetchSemanticsNode().config[SemanticsProperties.StateDescription]=="2 columns" }
            compose.waitForIdle();assertEquals(focused.key,compose.runOnUiThread { model.anchor()?.cursor?.key })
            compose.runOnUiThread { model.changeDensity(GridDensity.DEFAULT) }
            compose.waitUntil(30000) { compose.onNodeWithTag("photos:grid").fetchSemanticsNode().config[SemanticsProperties.StateDescription]=="3 columns" }
            compose.waitForIdle();assertEquals(focused.key,compose.runOnUiThread { model.anchor()?.cursor?.key })
            val anchor=compose.runOnUiThread { requireNotNull(model.anchor()) }
            val before=compose.onNodeWithTag(anchor.cursor.key).fetchSemanticsNode().boundsInWindow.top
            additions+=photos.first().copy(id="mosaic-continuity-newer",capturedAt=requireNotNull(photos.first().capturedAt)+60000)
            additions+=photos.last().copy(id="mosaic-continuity-older",capturedAt=requireNotNull(photos.last().capturedAt)-60000)
            additions.forEach { app.database.photos().insert(it) }
            val anchorPhoto=requireNotNull(app.database.photos().find(requireNotNull(anchor.cursor.key.removePrefix("photo:"))))
            app.database.photos().update(anchorPhoto.copy(displayName="Reloaded mosaic anchor"))
            compose.waitUntil(30000) { compose.onAllNodes(hasTestTag(anchor.cursor.key) and hasContentDescription("Reloaded mosaic anchor",substring=true)).fetchSemanticsNodes().isNotEmpty() }
            compose.waitForIdle();assertEquals(before,compose.onNodeWithTag(anchor.cursor.key).fetchSemanticsNode().boundsInWindow.top,4f)
            assertEquals(photos.size+2,photos.count { app.database.photos().find(it.id)!=null }+additions.count { app.database.photos().find(it.id)!=null })
        } finally { (photos+additions).forEach { app.database.photos().find(it.id)?.let { row -> app.database.photos().update(row.copy(archived=true)) } };files.forEach { it.delete() };app.galleryPresentation.updateLayout(previous);app.galleryPresentation.update(density) }
    }
    @Test fun calendarBoundariesShareRowsAndDateSeekRetainsViewerAnchor()=runBlocking<Unit> {
        val previous=app.galleryPresentation.layout.value;val density=app.galleryPresentation.density.value
        val files=listOf(picture(160,240,Color.BLUE),picture(240,240,Color.GREEN),picture(360,240,Color.RED))
        val base=Instant.parse("2999-01-02T12:00:00Z").toEpochMilli()
        val photos=(0 until 90).map { i ->
            val file=files[i%files.size];val id="continuous-${i.toString().padStart(3,'0')}"
            Photo(id,"$id.png",file.path,file.path,"image/png",file.length(),base,"continuous-$i",capturedAt=base-i*86_400_000L)
        }
        val hashes=files.associateWith { com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(it,"SHA-256") }
        try {
            app.galleryPresentation.update(GridDensity.DEFAULT);app.galleryPresentation.updateLayout(GalleryLayout.MOSAIC)
            app.database.withTransaction { photos.forEach { app.database.photos().insert(it) } }
            waitPhoto("photo:continuous-002");val model=timeline()
            val zone=java.time.ZoneId.systemDefault();val locale=compose.activity.resources.configuration.locales[0]
            for(layout in listOf(GalleryLayout.MOSAIC,GalleryLayout.SQUARE)) {
                compose.runOnUiThread { model.changeLayout(layout) }
                compose.waitUntil(30000) { app.galleryPresentation.layout.value==layout && compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty() }
                compose.waitForIdle()
                val cells=(0..2).map { compose.onNodeWithTag("photo:continuous-00$it").assertIsDisplayed().fetchSemanticsNode() }
                cells.forEachIndexed { index,node ->
                    assertEquals(cells[0].boundsInWindow.top,node.boundsInWindow.top,2f)
                    assertEquals(0,node.config[SemanticsProperties.CollectionItemInfo].rowIndex)
                    assertEquals(index,node.config[SemanticsProperties.CollectionItemInfo].columnIndex)
                    val date=Instant.ofEpochMilli(requireNotNull(photos[index].capturedAt)).atZone(zone).toLocalDate().toString()
                    compose.onNodeWithTag("photo:continuous-00$index").assert(hasContentDescription(date,substring=true))
                }
                compose.onAllNodes(hasAnyAncestor(hasTestTag("photos:grid")) and SemanticsMatcher.keyIsDefined(SemanticsProperties.Heading)).assertCountEquals(0)
                compose.onNodeWithTag("timeline:date-rail").assert(SemanticsMatcher.expectValue(SemanticsProperties.StateDescription,TimelineDateIndicator.from(base,zone).description(locale)))
                screenshot(if(layout==GalleryLayout.MOSAIC) "continuous-mosaic" else "continuous-square")
            }
            val selected=photos[2];val key="photo:${selected.id}"
            val day=Instant.ofEpochMilli(requireNotNull(selected.capturedAt)).atZone(zone).toLocalDate()
            compose.runOnUiThread { model.jump(day,zone) }
            compose.waitUntil(30000) {
                model.refreshedGeneration.value==model.restoration.value && compose.runOnUiThread { model.anchor()?.cursor?.key }==key &&
                    compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty() &&
                    compose.onAllNodesWithTag(key).fetchSemanticsNodes().singleOrNull()!=null
            }
            compose.waitForIdle()
            compose.onNodeWithTag("timeline:date-rail").assert(SemanticsMatcher.expectValue(SemanticsProperties.StateDescription,TimelineDateIndicator.from(selected.capturedAt,zone).description(locale)))
            val before=compose.onNodeWithTag(key).fetchSemanticsNode().boundsInWindow.top
            compose.onNodeWithTag(key).performClick();compose.waitUntil(30000) { compose.onAllNodesWithTag("viewer-screen").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("viewer-close").performClick();waitPhoto(key);compose.waitForIdle()
            assertEquals(before,compose.onNodeWithTag(key).fetchSemanticsNode().boundsInWindow.top,4f)
            compose.activityRule.scenario.recreate();waitPhoto(key)
            compose.waitUntil(30000) { compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty() }
            compose.waitForIdle();assertEquals(before,compose.onNodeWithTag(key).fetchSemanticsNode().boundsInWindow.top,4f)
            assertEquals(key,compose.runOnUiThread { timeline().anchor()?.cursor?.key })
            photos.forEach { assertEquals(it,app.database.photos().find(it.id)?.copy(lastAccess=it.lastAccess)) }
            hashes.forEach { (file,hash) -> assertEquals(hash,com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(file,"SHA-256")) }
        } finally {
            app.database.withTransaction { photos.forEach { app.database.photos().find(it.id)?.let { row -> app.database.photos().update(row.copy(archived=true)) } } }
            files.forEach { it.delete() };app.galleryPresentation.updateLayout(previous);app.galleryPresentation.update(density)
        }
    }

    private fun screenshot(name: String) {
        val instrumentation=InstrumentationRegistry.getInstrumentation();val image=requireNotNull(instrumentation.uiAutomation.takeScreenshot())
        try {
            val values=android.content.ContentValues().apply { put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"$name.png");put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png");put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA") }
            val uri=requireNotNull(app.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values));requireNotNull(app.contentResolver.openOutputStream(uri)).use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) }
        } finally { image.recycle() }
    }
}

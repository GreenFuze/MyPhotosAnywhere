package com.myphotosanywhere.mpa.timeline

import androidx.room.withTransaction
import android.graphics.Bitmap
import android.graphics.Color
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
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
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
class BlockMosaicDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    private fun waitPhoto(key: String)=compose.waitUntil(30000) { compose.onAllNodes(hasTestTag(key) and isEnabled()).fetchSemanticsNodes().isNotEmpty() }
    private fun timeline()=compose.runOnUiThread { ViewModelProvider(compose.activity)[TimelineViewModel::class.java] }
    private fun picture(width: Int,height: Int,color: Int): File {
        val file=File.createTempFile("blocks-",".png",app.filesDir);val image=Bitmap.createBitmap(width,height,Bitmap.Config.ARGB_8888)
        try { image.eraseColor(color);file.outputStream().use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { image.recycle() }
        return file
    }
    private suspend fun fixture(files: List<File>,prefix: String="blocks"): List<Photo> {
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
            waitPhoto("photo:blocks-002");compose.waitForIdle()
            val cells=(0..2).map { compose.onNodeWithTag("photo:blocks-00$it").fetchSemanticsNode() }
            assertEquals(cells[0].boundsInWindow.width,cells[1].boundsInWindow.width,2f)
            assertEquals(cells[1].boundsInWindow.width,cells[2].boundsInWindow.width,2f)
            assertEquals(cells[0].boundsInWindow.top,cells[2].boundsInWindow.top,2f)
            assertTrue(cells[0].boundsInWindow.height>cells[2].boundsInWindow.height*1.9f)
            assertTrue(cells[0].config[SemanticsProperties.CollectionItemInfo].rowSpan>=2)
            assertEquals(3,compose.onNodeWithTag("photos:grid").fetchSemanticsNode().config[SemanticsProperties.CollectionInfo].columnCount)
            assertEquals(1,cells[0].config[SemanticsProperties.CollectionItemInfo].columnSpan)
            assertTrue(cells.map { it.config[SemanticsProperties.CollectionItemInfo].columnIndex }.toSet()==setOf(0,1,2))
            screenshot("blocks-gallery")
            android.util.Log.i("MosaicTransitions","Enter selection")
            compose.onNodeWithTag("photo:blocks-000").performTouchInput { longClick() };compose.onNodeWithTag("photo:blocks-000").assertIsSelected()
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
            compose.waitForIdle();val square=(0..2).map { compose.onNodeWithTag("photo:blocks-00$it").fetchSemanticsNode().boundsInWindow.width }
            assertEquals(square[0],square[2],2f);screenshot("blocks-square")
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
            compose.onNodeWithTag("photo:blocks-000").performClick();compose.waitUntil(30000) { compose.onAllNodesWithTag("viewer-screen").fetchSemanticsNodes().isNotEmpty() }
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
            app.galleryPresentation.updateLayout(GalleryLayout.MOSAIC);photos=fixture(files,"blocks-continuity");waitPhoto("photo:blocks-continuity-000")
            val model=timeline();val focused=requireNotNull(app.timeline.find(TimelineQuery(),"photo:blocks-continuity-045"))
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
            additions+=photos.first().copy(id="blocks-continuity-newer",capturedAt=requireNotNull(photos.first().capturedAt)+60000)
            additions+=photos.last().copy(id="blocks-continuity-older",capturedAt=requireNotNull(photos.last().capturedAt)-60000)
            additions.forEach { app.database.photos().insert(it) }
            val anchorPhoto=requireNotNull(app.database.photos().find(requireNotNull(anchor.cursor.key.removePrefix("photo:"))))
            app.database.photos().update(anchorPhoto.copy(displayName="Reloaded blocks anchor"))
            compose.waitUntil(30000) { compose.onAllNodes(hasTestTag(anchor.cursor.key) and hasContentDescription("Reloaded blocks anchor",substring=true)).fetchSemanticsNodes().isNotEmpty() }
            compose.waitForIdle();assertEquals(before,compose.onNodeWithTag(anchor.cursor.key).fetchSemanticsNode().boundsInWindow.top,4f)
            assertEquals(photos.size+2,photos.count { app.database.photos().find(it.id)!=null }+additions.count { app.database.photos().find(it.id)!=null })
        } finally { (photos+additions).forEach { app.database.photos().find(it.id)?.let { row -> app.database.photos().update(row.copy(archived=true)) } };files.forEach { it.delete() };app.galleryPresentation.updateLayout(previous);app.galleryPresentation.update(density) }
    }
    @Test fun compactMosaicUsesBalancedSpansAndMirrorsCorrectlyAtLargeRtlFontScale()=runBlocking<Unit> {
        val previous=app.galleryPresentation.layout.value;val density=app.galleryPresentation.density.value
        val files=listOf(picture(160,240,Color.BLUE),picture(160,240,Color.GREEN),picture(360,240,Color.RED),picture(240,240,Color.MAGENTA))
        var photos=emptyList<Photo>()
        try {
            app.galleryPresentation.update(GridDensity.COMPACT);app.galleryPresentation.updateLayout(GalleryLayout.MOSAIC)
            photos=fixture(files,"blocks-balanced");val model=timeline()
            val features=compose.runOnUiThread { ViewModelProvider(compose.activity)[com.myphotosanywhere.mpa.library.LibraryFeaturesModel::class.java] }
            compose.runOnUiThread { compose.activity.setContent {
                val pixels=LocalDensity.current.density
                CompositionLocalProvider(LocalDensity provides Density(pixels,2f),LocalLayoutDirection provides LayoutDirection.Rtl) {
                    MaterialTheme(colorScheme=darkColorScheme()) { Surface(Modifier.fillMaxSize()) {
                        TimelineGrid(TimelineQuery(),model,features,80.dp,Modifier.fillMaxSize().safeDrawingPadding(),emptyList(),{},false,true,{}, {}, {})
                    } }
                }
            } }
            waitPhoto("photo:blocks-balanced-002");compose.waitForIdle()
            val cells=(0..2).map { compose.onNodeWithTag("photo:blocks-balanced-00$it").fetchSemanticsNode() }
            val grid=compose.onNodeWithTag("photos:grid").fetchSemanticsNode()
            val hero=cells[0];val small=cells[1];val info=hero.config[SemanticsProperties.CollectionItemInfo]
            assertEquals(4,grid.config[SemanticsProperties.CollectionInfo].columnCount)
            assertEquals(2,info.columnSpan);assertTrue(info.rowSpan in 2..4)
            assertEquals(small.boundsInWindow.width,cells[2].boundsInWindow.width,2f)
            assertEquals(2*small.boundsInWindow.width+2*compose.density.density,hero.boundsInWindow.width,2f)
            val expectedLeft=grid.boundsInWindow.right-2*compose.density.density-hero.boundsInWindow.width-info.columnIndex*(small.boundsInWindow.width+2*compose.density.density)
            assertEquals(expectedLeft,hero.boundsInWindow.left,3f)
            cells.forEachIndexed { index,node ->
                assertTrue(node.boundsInWindow.width>=48*compose.density.density-1)
                assertEquals(index.toFloat(),node.config[SemanticsProperties.TraversalIndex],0f)
            }
            compose.onNodeWithTag("timeline:date-scrubber").assertDoesNotExist()
            screenshot("blocks-balanced-rtl-large")
            photos.forEach { assertEquals(it,app.database.photos().find(it.id)?.copy(lastAccess=it.lastAccess)) }
        } finally {
            photos.forEach { app.database.photos().find(it.id)?.let { row -> app.database.photos().update(row.copy(archived=true)) } }
            files.forEach { it.delete() };app.galleryPresentation.updateLayout(previous);app.galleryPresentation.update(density)
        }
    }
    private fun screenshot(name: String) {
        val instrumentation=InstrumentationRegistry.getInstrumentation();val image=requireNotNull(instrumentation.uiAutomation.takeScreenshot())
        try {
            com.myphotosanywhere.mpa.NativeScreenshotEvidence(app).write(name,image)
        } finally { image.recycle() }
    }
}

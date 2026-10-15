package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import androidx.activity.compose.setContent
import androidx.room.withTransaction
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.unit.*
import androidx.lifecycle.ViewModelProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.library.LibraryFeaturesModel
import com.myphotosanywhere.mpa.selection.*
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.time.Instant

@RunWith(AndroidJUnit4::class)
class DragSelectionDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    private fun timeline()=compose.runOnUiThread { ViewModelProvider(compose.activity)[TimelineViewModel::class.java] }
    private fun selection()=compose.runOnUiThread { ViewModelProvider(compose.activity)[SelectionViewModel::class.java] }
    private class Fixture(val app: MpaApplication,val prefix: String,val count: Int=150) {
        val file=File.createTempFile("drag-fixture-",".png",app.filesDir)
        val layout=app.galleryPresentation.layout.value
        val density=app.galleryPresentation.density.value
        val photos=mutableListOf<Photo>()
        suspend fun create() {
            val bitmap=Bitmap.createBitmap(160,240,Bitmap.Config.ARGB_8888)
            try { bitmap.eraseColor(android.graphics.Color.rgb(45,135,170));file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { bitmap.recycle() }
            val base=Instant.parse("8990-05-15T12:00:00Z").toEpochMilli()
            app.database.withTransaction { repeat(count) { index ->
                val id="$prefix-${index.toString().padStart(3,'0')}"
                val photo=Photo(id,"$id.png",file.path,file.path,"image/png",file.length(),base,"drag-hash",capturedAt=base-index*60000L)
                app.database.photos().insert(photo);photos.add(photo)
            } }
            app.galleryPresentation.update(GridDensity.DEFAULT)
        }
        suspend fun close() {
            app.database.withTransaction { photos.forEach { app.database.photos().find(it.id)?.let { row -> app.database.photos().update(row.copy(archived=true)) } } }
            check(file.delete());app.galleryPresentation.updateLayout(layout);app.galleryPresentation.update(density)
        }
    }
    private fun waitReady(key: String) {
        compose.waitUntil(30000) { compose.onAllNodes(hasTestTag(key) and isEnabled()).fetchSemanticsNodes().isNotEmpty() && compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty() }
    }
    private fun point(key: String): Offset {
        val root=compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInWindow
        return compose.onNodeWithTag(key).fetchSemanticsNode().boundsInWindow.center-root.topLeft
    }
    private fun begin(key: String) {
        val position=point(key)
        compose.onNodeWithTag("photos:grid").performTouchInput { down(position);advanceEventTime(750);moveTo(position) }
        compose.waitUntil(10000) { selection().targets.value.any { it.key==key } }
    }

    @Test fun draggingAndReversingAcrossAllLayoutsPreservesEarlierSelection()=runBlocking<Unit> {
        val fixture=Fixture(app,"drag-range");fixture.create();val selected=selection();val model=timeline()
        try {
            val first=requireNotNull(app.timeline.find(TimelineQuery(),"photo:${fixture.photos[0].id}"))
            for(layout in GalleryLayout.entries) {
                compose.runOnUiThread { selected.clear();model.changeLayout(layout);model.rememberAnchor(first,0);model.restoreAnchor() }
                waitReady(first.key)
                compose.runOnUiThread { selected.ensureSelected(SelectionTarget.from(fixture.photos[8])) }
                begin(first.key)
                val forward=point("photo:${fixture.photos[5].id}")
                compose.onNodeWithTag("photos:grid").performTouchInput { moveTo(forward);advanceEventTime(100) }
                try { compose.waitUntil(10000) { selected.targets.value.size==7 } }
                catch(error: ComposeTimeoutException) {
                    screenshot("drag-selection-failed-${layout.name.lowercase()}")
                    val bounds=(0..8).map { index ->
                        val key="photo:${fixture.photos[index].id}"
                        key to compose.onAllNodesWithTag(key).fetchSemanticsNodes().map { it.boundsInWindow }
                    }
                    throw AssertionError("Drag extension failed: layout=$layout forward=$forward selected=${selected.targets.value.map { it.key }} bounds=$bounds",error)
                }
                val reverse=point("photo:${fixture.photos[3].id}")
                compose.onNodeWithTag("photos:grid").performTouchInput { moveTo(reverse);up() }
                compose.waitForIdle()
                assertEquals((listOf(8)+listOf(0,1,2,3)).map { fixture.photos[it].id }.toSet(),selected.targets.value.map { it.photoId }.toSet())
                compose.waitUntil(10000) { compose.onAllNodesWithText("5 selected").fetchSemanticsNodes().size==1 }
                for(index in listOf(8,0,1,2,3)) compose.onNodeWithTag("photo:${fixture.photos[index].id}").assertIsSelected()
                compose.onNodeWithTag("photo:${fixture.photos[4].id}").assertIsNotSelected()
                compose.onNodeWithTag("photo:${fixture.photos[5].id}").assertIsNotSelected()
                compose.mainClock.advanceTimeByFrame();compose.waitForIdle()
                screenshot("drag-selection-${layout.name.lowercase()}")
                compose.onNodeWithTag("selection:close").performClick();assertTrue(selected.targets.value.isEmpty())
            }
            fixture.photos.forEach { assertEquals(it,app.database.photos().find(it.id)) }
        } finally { compose.runOnUiThread { selected.clear() };fixture.close() }
    }

    @Test fun edgeAutoscrollAddsContinuousCanonicalRangeAndBackLeavesOriginals()=runBlocking<Unit> {
        val fixture=Fixture(app,"drag-edge",450);fixture.create();val selected=selection();val model=timeline()
        try {
            compose.runOnUiThread { model.changeLayout(GalleryLayout.SQUARE) }
            val first=requireNotNull(app.timeline.find(TimelineQuery(),"photo:${fixture.photos[0].id}"))
            compose.runOnUiThread { model.rememberAnchor(first,0);model.restoreAnchor() };waitReady(first.key)
            begin(first.key)
            val root=compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInWindow
            compose.mainClock.autoAdvance=false
            compose.onNodeWithTag("photos:grid").performTouchInput { moveTo(Offset(root.width/2,root.height-8)) }
            compose.mainClock.advanceTimeBy(8000)
            compose.onNodeWithTag("photos:grid").performTouchInput { up() }
            compose.mainClock.autoAdvance=true;compose.waitForIdle()
            val ids=selected.targets.value.map { requireNotNull(it.photoId).removePrefix("drag-edge-").toInt() }.sorted()
            assertTrue(ids.size>15);assertTrue(ids.size<=MediaSelection.MAX_ITEMS);assertEquals((0..ids.last()).toList(),ids)
            assertTrue("Edge selection must cross the 90-item initial paging window",ids.size>90)
            androidx.test.espresso.Espresso.pressBack();compose.waitForIdle();assertTrue(selected.targets.value.isEmpty())
            fixture.photos.forEach { assertEquals(it,app.database.photos().find(it.id)) }
            assertTrue(fixture.file.isFile)
        } finally { compose.mainClock.autoAdvance=true;compose.runOnUiThread { selected.clear() };fixture.close() }
    }

    @Test fun rtlPortraitHitTestingAndImportCancellationNeverRetargetSelection()=runBlocking<Unit> {
        val fixture=Fixture(app,"drag-rtl");fixture.create();val selected=selection();val model=timeline()
        val features=compose.runOnUiThread { ViewModelProvider(compose.activity)[LibraryFeaturesModel::class.java] }
        try {
            compose.runOnUiThread { model.changeLayout(GalleryLayout.MOSAIC) }
            compose.runOnUiThread { compose.activity.setContent {
                val density=LocalDensity.current.density;val targets by selected.targets.collectAsState()
                CompositionLocalProvider(LocalDensity provides Density(density,2f),LocalLayoutDirection provides LayoutDirection.Rtl) {
                    MaterialTheme(colorScheme=darkColorScheme()) { Surface(Modifier.fillMaxSize()) {
                        TimelineGrid(TimelineQuery(),model,features,56.dp,Modifier.fillMaxSize().safeDrawingPadding(),targets,selected::toggle,false,true,{}, {}, {},
                            onRangeChange=selected::replaceDrag,onBeginSelection=selected::ensureSelected,onSelectionLimit=selected::dragLimit)
                    } }
                }
            } }
            val first=requireNotNull(app.timeline.find(TimelineQuery(),"photo:${fixture.photos[0].id}"))
            compose.runOnUiThread { model.rememberAnchor(first,0);model.restoreAnchor() };waitReady(first.key)
            val hero=compose.onNodeWithTag(first.key).fetchSemanticsNode()
            val neighbor=compose.onNodeWithTag("photo:${fixture.photos[1].id}").fetchSemanticsNode()
            assertTrue(hero.boundsInWindow.left>neighbor.boundsInWindow.left)
            assertTrue(hero.config[SemanticsProperties.CollectionItemInfo].rowSpan>1)
            begin(first.key)
            val added=fixture.photos.first().copy(id="drag-rtl-import",capturedAt=requireNotNull(first.millis)+60000)
            fixture.photos.add(added);app.database.photos().insert(added)
            waitReady(first.key)
            val other=point("photo:${fixture.photos[4].id}")
            compose.onNodeWithTag("photos:grid").performTouchInput { moveTo(other);up() }
            compose.waitForIdle();assertEquals(listOf(first.key),selected.targets.value.map { it.key })
            screenshot("drag-selection-rtl-large")
            fixture.photos.forEach { assertEquals(it,app.database.photos().find(it.id)) }
        } finally { compose.runOnUiThread { selected.clear() };fixture.close() }
    }

    private fun screenshot(name: String) {
        val bitmap=requireNotNull(InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot())
        try {
            val values=android.content.ContentValues().apply { put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"$name.png");put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png");put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA") }
            val uri=requireNotNull(app.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
            requireNotNull(app.contentResolver.openOutputStream(uri)).use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) }
        } finally { bitmap.recycle() }
    }
}

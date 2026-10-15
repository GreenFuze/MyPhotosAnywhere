package com.myphotosanywhere.mpa.timeline

import android.graphics.Bitmap
import androidx.activity.compose.setContent
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
import androidx.room.withTransaction
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.MainActivity
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.drive.CloudPhoto
import com.myphotosanywhere.mpa.drive.DriveSource
import com.myphotosanywhere.mpa.library.LibraryFeaturesModel
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.time.YearMonth
import java.time.ZoneId

@RunWith(AndroidJUnit4::class)
class DateScrubDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    private fun model()=compose.runOnUiThread { ViewModelProvider(compose.activity)[TimelineViewModel::class.java] }
    private fun waitTag(tag: String)=compose.waitUntil(30000) { compose.onAllNodes(hasTestTag(tag) and isEnabled()).fetchSemanticsNodes().isNotEmpty() }
    private class Fixture(val app: MpaApplication,val prefix: String) {
        val source=DriveSource(prefix,prefix,"dates@test","APP_FILES",prefix,"Date fixture $prefix")
        val query=TimelineQuery(source=prefix)
        val file=File.createTempFile("date-fixture-",".png",app.filesDir)
        val photos=mutableListOf<Photo>()
        val layout=app.galleryPresentation.layout.value
        val density=app.galleryPresentation.density.value
        suspend fun create() {
            val bitmap=Bitmap.createBitmap(160,240,Bitmap.Config.ARGB_8888)
            try { bitmap.eraseColor(android.graphics.Color.rgb(65,120,165));file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { bitmap.recycle() }
            val sha=com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(file,"SHA-256")
            app.database.withTransaction {
                app.database.drive().saveSource(source)
                repeat(240) { index ->
                    val id="$prefix-${index.toString().padStart(3,'0')}"
                    val time=YearMonth.of(9100,12).minusMonths(index.toLong()).atDay(15).atTime(12,0).atZone(ZoneId.systemDefault()).toInstant().toEpochMilli()
                    val photo=Photo(id,"$id.png",file.path,file.path,"image/png",file.length(),time,sha,capturedAt=time,sourceBindingKey=source.key)
                    app.database.photos().insert(photo);photos.add(photo)
                }
            }
            app.galleryPresentation.update(GridDensity.DEFAULT);app.galleryPresentation.updateLayout(GalleryLayout.MOSAIC)
        }
        suspend fun close() {
            app.database.withTransaction {
                photos.forEach { app.database.photos().find(it.id)?.let { row -> app.database.photos().update(row.copy(archived=true)) } }
                app.database.drive().saveSource(source.copy(enabled=false))
            }
            check(file.delete());app.galleryPresentation.updateLayout(layout);app.galleryPresentation.update(density)
        }
    }
    private fun filter(fixture: Fixture) {
        compose.onNodeWithTag("shell:filters").performClick()
        compose.onNodeWithText("${fixture.source.folderName} · ${fixture.source.email}").performScrollTo().performClick()
        compose.onNodeWithText("Done").performScrollTo().performClick()
        waitTag("photo:${fixture.photos.first().id}");waitTag("timeline:date-scrubber")
    }
    private fun dragTo(month: YearMonth,scale: DateScrubScale,begin: Boolean=false,finish: Boolean=false) {
        val track=compose.onNodeWithTag("timeline:scrub-track").fetchSemanticsNode().boundsInWindow
        val handle=compose.onNodeWithTag("timeline:date-scrubber").fetchSemanticsNode().boundsInWindow
        val height=handle.height
        val target=Offset(track.width/2,height/2+(track.height-height).coerceAtLeast(0f)*scale.fraction(month))
        compose.onNodeWithTag("timeline:scrub-track").performTouchInput {
            if(begin) down(handle.center-track.topLeft)
            moveTo(target);advanceEventTime(150)
            if(finish) up()
        }
    }
    private fun waitAnchor(model: TimelineViewModel,query: TimelineQuery,key: String) {
        compose.waitUntil(30000) {
            model.anchor(query)?.cursor?.key==key && compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty() &&
                compose.onAllNodes(hasTestTag(key) and isEnabled()).fetchSemanticsNodes().size==1
        }
    }
    @Test fun directDragRapidSeeksImportAndViewerReturnPreserveCanonicalAnchor()=runBlocking<Unit> {
        val fixture=Fixture(app,"scrub-main");fixture.create()
        try {
            filter(fixture);val model=model();val zone=ZoneId.systemDefault()
            val scale=DateScrubScale(requireNotNull(app.timeline.dateRange(fixture.query)).months(zone))
            val month=scale.months.last.minusMonths(120)
            val expected=requireNotNull(app.timeline.seek(fixture.query,month.atEndOfMonth().plusDays(1).atStartOfDay(zone).toInstant().toEpochMilli()))
            dragTo(month,scale,begin=true)
            waitAnchor(model,fixture.query,expected.key)
            // The original gesture remains active after its own seek/restoration.
            val added=fixture.photos.first().copy(id="scrub-main-import",capturedAt=requireNotNull(fixture.photos.first().capturedAt)+86400000L)
            fixture.photos.add(added);app.database.photos().insert(added)
            val final=scale.months.last.minusMonths(180)
            for(offset in listOf(30L,150L,60L)) dragTo(scale.months.last.minusMonths(offset),scale)
            dragTo(final,scale,finish=true)
            val finalCursor=requireNotNull(app.timeline.seek(fixture.query,final.atEndOfMonth().plusDays(1).atStartOfDay(zone).toInstant().toEpochMilli()))
            waitAnchor(model,fixture.query,finalCursor.key)
            screenshot("date-scrub-gallery")
            val before=compose.onNodeWithTag(finalCursor.key).fetchSemanticsNode().boundsInWindow.top
            compose.onNodeWithTag(finalCursor.key).performClick();waitTag("viewer-screen")
            compose.onNodeWithTag("viewer-close").performClick();waitAnchor(model,fixture.query,finalCursor.key)
            assertEquals(before,compose.onNodeWithTag(finalCursor.key).fetchSemanticsNode().boundsInWindow.top,4f)
            compose.activityRule.scenario.recreate();waitAnchor(model(),fixture.query,finalCursor.key)
            fixture.photos.forEach { assertEquals(it,app.database.photos().find(it.id)?.copy(lastAccess=it.lastAccess)) }
            assertTrue(fixture.file.isFile)
        } finally { fixture.close() }
    }
    @Test fun rtlLargeTextHasAccessibleHandleClearOfNavigationAndJumpAlternative()=runBlocking<Unit> {
        val fixture=Fixture(app,"scrub-rtl");fixture.create();val model=model()
        val features=compose.runOnUiThread { ViewModelProvider(compose.activity)[LibraryFeaturesModel::class.java] }
        try {
            compose.runOnUiThread { compose.activity.setContent {
                val pixels=LocalDensity.current.density
                CompositionLocalProvider(LocalDensity provides Density(pixels,2f),LocalLayoutDirection provides LayoutDirection.Rtl) {
                    MaterialTheme(colorScheme=darkColorScheme()) { Surface(Modifier.fillMaxSize()) {
                        TimelineGrid(fixture.query,model,features,80.dp,Modifier.fillMaxSize().safeDrawingPadding(),emptyList(),{},false,true,{}, {}, {})
                    } }
                }
            } }
            waitTag("timeline:date-scrubber")
            val track=compose.onNodeWithTag("timeline:scrub-track").fetchSemanticsNode().boundsInWindow
            val handle=compose.onNodeWithTag("timeline:date-scrubber").fetchSemanticsNode()
            assertTrue(track.left<100f);assertTrue(handle.boundsInWindow.width>=48*compose.density.density-1)
            assertTrue(handle.config[SemanticsProperties.ContentDescription].any { it.contains("Jump to date") })
            val scale=DateScrubScale(requireNotNull(app.timeline.dateRange(fixture.query)).months(ZoneId.systemDefault()))
            dragTo(scale.months.first,scale,begin=true,finish=true)
            waitAnchor(model,fixture.query,"photo:${fixture.photos.last().id}")
            screenshot("date-scrub-rtl-large")
            compose.onNodeWithTag("timeline:date-scrubber").performClick();waitTag("timeline:date-navigation")
            screenshot("date-scrub-jump")
            compose.onNodeWithText("Cancel").performClick()
            assertEquals("photo:${fixture.photos.last().id}",compose.runOnUiThread { model.anchor(fixture.query)?.cursor?.key })
            fixture.photos.forEach { assertEquals(it,app.database.photos().find(it.id)) }
        } finally { fixture.close() }
    }
    @Test fun unknownDatesOfferHonestJumpWithoutInventedScrubRange()=runBlocking<Unit> {
        val key="scrub-unknown";val source=DriveSource(key,key,"unknown@test","APP_FILES",key,"Unknown dates")
        val cloud=CloudPhoto("$key:photo",key,"photo",key,"unknown.png","image/png",1,null,"v1",null,null,null,null)
        val query=TimelineQuery(source=key);val model=model()
        val features=compose.runOnUiThread { ViewModelProvider(compose.activity)[LibraryFeaturesModel::class.java] }
        try {
            app.database.drive().saveSource(source);app.database.cloud().savePhoto(cloud)
            compose.runOnUiThread { compose.activity.setContent {
                MaterialTheme { TimelineGrid(query,model,features,80.dp,Modifier.fillMaxSize().safeDrawingPadding(),emptyList(),{},false,true,{}, {}, {}) }
            } }
            waitTag("cloud:${cloud.key}");waitTag("timeline:date-rail")
            compose.onNodeWithTag("timeline:date-rail").assertTextContains("Unknown date")
            assertNull(app.timeline.dateRange(query));compose.onNodeWithTag("timeline:date-scrubber").assertDoesNotExist()
            compose.onNodeWithTag("timeline:date-rail").performClick();waitTag("timeline:date-navigation")
            compose.onNodeWithText("No dated photos match this view. Unknown-date photos remain in the gallery.").assertExists()
            compose.onNodeWithTag("timeline:month-jump").assertIsNotEnabled();screenshot("date-scrub-unknown")
            compose.onNodeWithText("Cancel").performClick();waitTag("cloud:${cloud.key}")
            assertEquals(cloud,app.database.cloud().photo(cloud.key))
        } finally { app.database.drive().saveSource(source.copy(enabled=false)) }
    }
    private fun screenshot(name: String) {
        val image=requireNotNull(InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot())
        try {
            val values=android.content.ContentValues().apply { put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"$name.png");put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png");put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA") }
            val uri=requireNotNull(app.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
            requireNotNull(app.contentResolver.openOutputStream(uri)).use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) }
        } finally { image.recycle() }
    }
}

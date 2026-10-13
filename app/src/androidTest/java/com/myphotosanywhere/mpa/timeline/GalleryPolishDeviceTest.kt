package com.myphotosanywhere.mpa.timeline

import android.graphics.Bitmap
import android.graphics.Color
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.unit.*
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
import java.time.YearMonth
import java.time.ZoneId

@RunWith(AndroidJUnit4::class)
class GalleryPolishDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    private fun waitTag(tag: String)=compose.waitUntil(30000) { compose.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty() }
    private fun columns(value: Int)=compose.waitUntil(15000) {
        compose.onNodeWithTag("photos:grid").fetchSemanticsNode().config[SemanticsProperties.StateDescription]=="$value columns"
    }
    private fun pinch(out: Boolean) {
        compose.onNodeWithTag("photos:grid").performTouchInput {
            val point=Offset(center.x,center.y)
            val begin=if(out) 60f else 180f;val end=if(out) 180f else 60f
            down(0,point.copy(x=point.x-begin));down(1,point.copy(x=point.x+begin))
            moveTo(0,point.copy(x=point.x-end),200);moveTo(1,point.copy(x=point.x+end),200)
            up(0);up(1)
        }
    }
    @Test fun densityPinchPreservesAnchorRotationSelectionAndViewer()=runBlocking<Unit> {
        val original=app.galleryPresentation.density.value
        val file=File.createTempFile("polish-",".png",app.filesDir)
        val bitmap=Bitmap.createBitmap(200,200,Bitmap.Config.ARGB_8888)
        try { bitmap.eraseColor(Color.CYAN);file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { bitmap.recycle() }
        val base=YearMonth.of(2090,12).atEndOfMonth().atStartOfDay(ZoneId.systemDefault()).toInstant().toEpochMilli()
        val photos=(0 until 90).map { i -> Photo("polish-$i","polish-$i.png",file.path,file.path,"image/png",file.length(),base,"polish-sha",capturedAt=base-i*86400000L) }
        try {
            app.galleryPresentation.update(GridDensity.DEFAULT);photos.forEach { app.database.photos().insert(it) }
            waitTag("photo:polish-0");compose.onNodeWithTag("photos:grid").performScrollToNode(hasTestTag("photo:polish-30"));waitTag("photo:polish-30")
            compose.waitForIdle()
            val timeline=compose.runOnUiThread { ViewModelProvider(compose.activity)[TimelineViewModel::class.java] }
            val anchor=compose.runOnUiThread { requireNotNull(timeline.anchor()) }
            val before=compose.onNodeWithTag(anchor.cursor.key).fetchSemanticsNode().boundsInWindow.top
            pinch(true);columns(2);waitTag(anchor.cursor.key);compose.waitForIdle()
            val after=compose.runOnUiThread { requireNotNull(timeline.anchor()) }
            android.util.Log.i("DensityViewport","before=$anchor after=$after")
            assertEquals("Density must preserve the actual first visible identity",anchor.cursor.key,after.cursor.key)
            assertEquals("Density must preserve that identity's actual pixel offset",anchor.offset.toDouble(),after.offset.toDouble(),4.0)
            assertEquals(before,compose.onNodeWithTag(anchor.cursor.key).fetchSemanticsNode().boundsInWindow.top,4f)
            compose.activityRule.scenario.recreate();columns(2);waitTag(anchor.cursor.key)
            compose.onNodeWithTag("photos:grid").performScrollToNode(hasTestTag("photo:polish-30"));waitTag("photo:polish-30")
            screenshotPolish("density-large")
            compose.onNodeWithTag("photo:polish-30").performTouchInput { longClick() }
            compose.onNodeWithTag("timeline:date-rail").assertDoesNotExist();pinch(false);assertEquals(GridDensity.LARGE,app.galleryPresentation.density.value)
            compose.onNodeWithTag("photo:polish-30").assertIsSelected()
            compose.onNodeWithTag("selection:close").performClick()
            waitTag("timeline:date-rail");pinch(false);columns(3)
            compose.onNodeWithTag("photo:polish-30").performClick();waitTag("viewer-screen");compose.onNodeWithTag("viewer-close").performClick();waitTag("photo:polish-30")
            photos.forEach { assertEquals(it,app.database.photos().find(it.id)?.copy(lastAccess=it.lastAccess)) }
        } finally { photos.forEach { app.database.photos().find(it.id)?.let { row -> app.database.photos().update(row.copy(archived=true)) } };file.delete();app.galleryPresentation.update(original) }
    }
    @Test fun dateRailJumpsDirectlyAndCancelLeavesPosition()=runBlocking<Unit> {
        val base=YearMonth.of(2080,1).atDay(15).atStartOfDay(ZoneId.systemDefault()).toInstant().toEpochMilli()
        val photos=listOf(Photo("polish-date-old","date-old.png","","","image/png",1,base,"old",capturedAt=base),Photo("polish-date-new","date-new.png","","","image/png",1,base+86400000L*366,"new",capturedAt=base+86400000L*366))+(0 until 8).map { index ->
            val time=base-(index+1)*40*86400000L
            Photo("polish-date-tail-$index","date-tail-$index.png","","","image/png",1,time,"tail-$index",capturedAt=time)
        }
        try {
            photos.forEach { app.database.photos().insert(it) };waitTag("timeline:date-rail")
            compose.onNodeWithTag("timeline:date-rail").performClick();waitTag("timeline:year")
            compose.onNodeWithTag("timeline:year").performTextReplacement("999999999")
            compose.onNodeWithTag("timeline:month-jump").assertIsNotEnabled()
            compose.onNodeWithTag("timeline:year").performTextReplacement("2080")
            compose.onNodeWithTag("timeline:year").performImeAction()
            compose.waitUntil(10000) {
                compose.runOnUiThread { androidx.core.view.ViewCompat.getRootWindowInsets(compose.activity.window.decorView)?.isVisible(androidx.core.view.WindowInsetsCompat.Type.ime())!=true }
            }
            compose.onNodeWithTag("timeline:month:1").performScrollTo().performClick();screenshotPolish("date-navigation")
            compose.onNodeWithTag("timeline:month-jump").assertIsDisplayed().performClick();waitTag("photo:polish-date-old")
            val timeline=compose.runOnUiThread { ViewModelProvider(compose.activity)[TimelineViewModel::class.java] }
            // The selected photo was already visible before Jump. Its presence does not mean that the seek finished.
            // Enough older fixture rows make zero-offset placement possible without end-of-list clamping.
            var lastSeekState=""
            try { compose.waitUntil(30000) {
                val anchor=compose.runOnUiThread { timeline.anchor() }
                val ready=timeline.refreshedGeneration.value==timeline.restoration.value
                val photoBounds=compose.onAllNodesWithTag("photo:polish-date-old").fetchSemanticsNodes().singleOrNull()?.boundsInWindow ?: return@waitUntil false
                val gridBounds=compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInWindow
                val diagnostic="anchor=$anchor generations=${timeline.refreshedGeneration.value}/${timeline.restoration.value} photo=$photoBounds grid=$gridBounds"
                if(diagnostic!=lastSeekState) { lastSeekState=diagnostic;android.util.Log.i("DateSeekState",diagnostic) }
                ready && anchor?.cursor?.key=="photo:polish-date-old" && anchor.offset==0 && kotlin.math.abs(photoBounds.top-gridBounds.top)<4f
            } } finally { screenshotPolish("date-after-seek");android.util.Log.i("DateSeekState","last: $lastSeekState") }
            compose.waitForIdle()
            val before=compose.onNodeWithTag("photo:polish-date-old").fetchSemanticsNode().boundsInWindow.top
            val beforeAnchor=compose.runOnUiThread { timeline.anchor() }
            compose.onNodeWithTag("timeline:date-rail").performClick();waitTag("timeline:year");compose.onNodeWithText("Cancel").performClick();compose.waitForIdle()
            val afterAnchor=compose.runOnUiThread { timeline.anchor() }
            android.util.Log.i("DateViewport","before=$beforeAnchor after=$afterAnchor")
            assertEquals(beforeAnchor,afterAnchor)
            assertEquals(before,compose.onNodeWithTag("photo:polish-date-old").fetchSemanticsNode().boundsInWindow.top,3f)
            photos.forEach { assertEquals(it,app.database.photos().find(it.id)) }
        } finally { photos.forEach { app.database.photos().update(it.copy(archived=true)) } }
    }
}

@RunWith(AndroidJUnit4::class)
class GalleryPolishChromeDeviceTest {
    @get:Rule val compose=createComposeRule()
    @Test fun largeLightControlsHaveAccessibleAlternatives()=verify(false,LayoutDirection.Ltr)
    @Test fun largeDarkRtlControlsHaveAccessibleAlternatives()=verify(true,LayoutDirection.Rtl)
    private fun verify(dark: Boolean,direction: LayoutDirection) {
        var pixels=0f;var selected=GridDensity.DEFAULT
        compose.setContent {
            pixels=LocalDensity.current.density
            CompositionLocalProvider(LocalDensity provides Density(pixels,2f),LocalLayoutDirection provides direction) {
                MaterialTheme(colorScheme=if(dark) darkColorScheme() else lightColorScheme()) {
                    Surface { Column(Modifier.width(320.dp).fillMaxHeight().verticalScroll(rememberScrollState()).padding(16.dp)) {
                        GridDensityControls(selected) { selected=it }
                        MonthNavigationControls(MonthNavigation(YearMonth.of(1998,1),YearMonth.of(2026,12)),YearMonth.of(2024,2)) { }
                    } }
                }
            }
        }
        for(value in GridDensity.entries) {
            val node=compose.onNodeWithTag("density:${value.columns}").performScrollTo().assertIsDisplayed();val rect=node.fetchSemanticsNode().boundsInRoot
            assertTrue(rect.width>=48*pixels-1);assertTrue(rect.height>=48*pixels-1);node.performClick();assertEquals(value,selected)
        }
        compose.onNodeWithTag("timeline:year").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("timeline:month:2").performScrollTo().assertIsDisplayed()
        screenshotPolish(if(dark) "polish-dark-rtl-large" else "polish-light-large")
    }
}

private fun screenshotPolish(name: String) {
    val instrumentation=InstrumentationRegistry.getInstrumentation();val bitmap=requireNotNull(instrumentation.uiAutomation.takeScreenshot());val context=instrumentation.targetContext
    try {
        val values=android.content.ContentValues().apply { put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"$name.png");put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png");put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA") }
        val uri=requireNotNull(context.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values));requireNotNull(context.contentResolver.openOutputStream(uri)).use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) }
    } finally { bitmap.recycle() }
}

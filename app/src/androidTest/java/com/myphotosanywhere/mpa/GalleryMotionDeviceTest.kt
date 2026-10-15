package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import android.graphics.Color
import android.provider.MediaStore
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.background
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.*
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.unit.*
import androidx.lifecycle.ViewModelProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.library.*
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/** Real nested-scroll geometry with synthetic local originals; no Google access. */
@RunWith(AndroidJUnit4::class)
class GalleryMotionDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    private fun waitTag(tag: String)=compose.waitUntil(30000) { compose.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty() }
    @Test fun scrollingReleasesTopSpaceReverseRestoresActionsAndViewerKeepsAnchor()=runBlocking<Unit> {
        val file=File.createTempFile("motion-",".png",app.filesDir)
        val image=Bitmap.createBitmap(180,240,Bitmap.Config.ARGB_8888)
        try { image.eraseColor(Color.rgb(40,150,130));file.outputStream().use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { image.recycle() }
        val photos=(0 until 120).map { index -> Photo("motion-$index","Motion $index.png",file.path,file.path,"image/png",file.length(),4_000_000_000_000L,"motion-sha",capturedAt=4_000_000_000_000L-index*86400000L) }
        val oldLayout=app.galleryPresentation.layout.value
        try {
            app.galleryPresentation.updateLayout(GalleryLayout.SQUARE)
            photos.forEach { app.database.photos().insert(it) }
            waitTag("photo:motion-0");compose.waitForIdle()
            val expanded=compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInWindow
            compose.onNodeWithTag("photos:grid").performTouchInput { swipeUp() }
            compose.waitForIdle()
            val hidden=compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInWindow
            assertTrue("The gallery gains the top-bar space",hidden.top<expanded.top-32f)
            assertTrue("The released viewport is usable",hidden.height>expanded.height+32f)
            compose.onNodeWithTag("shell:storage").assertIsNotDisplayed()
            motionScreenshot("gallery-bar-hidden")
            val model=compose.runOnUiThread { ViewModelProvider(compose.activity)[TimelineViewModel::class.java] }
            val anchor=compose.runOnUiThread { requireNotNull(model.anchor()) }
            compose.onNodeWithTag(anchor.cursor.key).performClick();waitTag("viewer-screen")
            compose.onNodeWithTag("viewer-close").performClick();waitTag(anchor.cursor.key);compose.waitForIdle()
            val restored=compose.runOnUiThread { requireNotNull(model.anchor()) }
            assertEquals(anchor.cursor.key,restored.cursor.key);assertEquals(anchor.offset.toDouble(),restored.offset.toDouble(),4.0)
            assertEquals(hidden.top,compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInWindow.top,4f)
            compose.activityRule.scenario.recreate();waitTag(anchor.cursor.key);compose.waitForIdle()
            assertEquals(hidden.top,compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInWindow.top,4f)
            compose.onNodeWithTag("photos:grid").performTouchInput { swipeDown() };compose.waitForIdle()
            compose.onNodeWithTag("shell:storage").assertIsDisplayed()
            assertEquals(expanded.top,compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInWindow.top,4f)
            motionScreenshot("gallery-bar-returned")
            compose.onNodeWithTag("photos:grid").performTouchInput { swipeUp() };compose.waitForIdle()
            compose.onNodeWithTag("shell:storage").assertIsNotDisplayed()
            // Actual keyboard navigation pins and reveals actions; a recreated root's
            // default input mode must not erase the saved touch-scrolling position.
            repeat(2) { InstrumentationRegistry.getInstrumentation().sendKeyDownUpSync(android.view.KeyEvent.KEYCODE_TAB) }
            compose.waitForIdle()
            compose.onNodeWithTag("shell:storage").assertIsDisplayed()
            assertEquals(expanded.top,compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInWindow.top,4f)
            compose.onNodeWithTag("nav:Search").performClick();waitTag("search:query")
            compose.onNodeWithTag("search:query").assertIsDisplayed()
        } finally {
            photos.forEach { app.database.photos().find(it.id)?.let { row -> app.database.photos().update(row.copy(archived=true)) } }
            app.galleryPresentation.updateLayout(oldLayout);file.delete()
        }
    }
}

@RunWith(AndroidJUnit4::class)
class MotionChromeDeviceTest {
    @get:Rule val compose=createComposeRule()
    @Test fun outgoingViewerControlsLoseActionsWhileMediaAndRecoveryStayVisible() {
        lateinit var chrome: ViewerChromeController
        var details=0
        compose.setContent { MaterialTheme {
            chrome=remember { ViewerChromeController() }
            ViewerSurface(chrome,topBar={ ViewerTopBar(null,"Fixture",{},{ details++ },chrome::hide) },
                bottomBar={ Text("Actions",Modifier.heightIn(min=48.dp).testTag("motion:actions")) },
                status={ Text("Retry available",Modifier.testTag("motion:status"),color=androidx.compose.ui.graphics.Color.White) }) { Box(Modifier.fillMaxSize().testTag("motion:media")) }
        } }
        val media=compose.onNodeWithTag("motion:media").fetchSemanticsNode().boundsInRoot
        compose.mainClock.autoAdvance=false
        compose.runOnUiThread { chrome.hide() }
        compose.mainClock.advanceTimeByFrame()
        compose.onNodeWithTag("viewer-details").assertDoesNotExist()
        compose.onNodeWithTag("motion:status").assertIsDisplayed()
        assertEquals(media,compose.onNodeWithTag("motion:media").fetchSemanticsNode().boundsInRoot)
        assertEquals(0,details)
        compose.mainClock.autoAdvance=true
        compose.onNodeWithTag("viewer-show-controls").performClick()
        compose.onNodeWithTag("viewer-details").assertIsDisplayed().performClick();assertEquals(1,details)
    }
    @Test fun largeRtlChromeTransitionsRetainExplicitRecoveryAndMinimumTargets() {
        compose.setContent {
            val pixels=LocalDensity.current.density
            CompositionLocalProvider(LocalDensity provides Density(pixels,2f),LocalLayoutDirection provides LayoutDirection.Rtl) {
                MaterialTheme(colorScheme=darkColorScheme()) {
                    val chrome=remember { ViewerChromeController() }
                    ViewerSurface(chrome,Modifier.width(320.dp).background(androidx.compose.ui.graphics.Color.Black),topBar={ ViewerTopBar(null,"צילום.png",{},{},chrome::hide) },
                        bottomBar={ Text("פעולות",Modifier.heightIn(min=48.dp),color=androidx.compose.ui.graphics.Color.White) },status={ Text("Preview only — Retry",Modifier.testTag("motion:status"),color=androidx.compose.ui.graphics.Color.White) }) { Box(Modifier.fillMaxSize()) }
                }
            }
        }
        compose.onNodeWithTag("viewer-hide-controls").performClick()
        compose.onNodeWithTag("viewer-show-controls").assertIsDisplayed().assertHasClickAction()
        compose.onNodeWithTag("motion:status").assertIsDisplayed();motionScreenshot("motion-dark-rtl")
        compose.onNodeWithTag("viewer-show-controls").performClick();compose.onNodeWithTag("viewer-details").assertIsDisplayed()
    }
}

@RunWith(AndroidJUnit4::class)
class ReducedMotionDeviceTest {
    @get:Rule val compose=createComposeRule(effectContext=object: androidx.compose.ui.MotionDurationScale {
        override val scaleFactor=0f
    })
    @Test fun zeroDurationScaleKeepsRecoveryAndReachesFinalControlState() {
        lateinit var chrome: ViewerChromeController
        var observedScale=-1f
        var pixels=0f
        compose.setContent { MaterialTheme {
            pixels=LocalDensity.current.density
            LaunchedEffect(Unit) { observedScale=kotlinx.coroutines.currentCoroutineContext()[androidx.compose.ui.MotionDurationScale]?.scaleFactor ?: 1f }
            chrome=remember { ViewerChromeController() }
            ViewerSurface(chrome,topBar={ ViewerTopBar(null,"Fixture",{},{},chrome::hide) },
                bottomBar={ Text("Actions") },status={ Text("Retry",Modifier.testTag("reduced:retry"),color=androidx.compose.ui.graphics.Color.White) }) { Box(Modifier.fillMaxSize()) }
        } }
        compose.waitForIdle();assertEquals(0f,observedScale,0f)
        compose.mainClock.autoAdvance=false
        compose.runOnUiThread { chrome.hide() };repeat(2) { compose.mainClock.advanceTimeByFrame() }
        compose.onNodeWithTag("viewer-details").assertDoesNotExist()
        val show=compose.onNodeWithTag("viewer-show-controls").assertIsDisplayed().fetchSemanticsNode().boundsInRoot
        assertTrue("Reduced motion reaches a full touch target",show.width>=48*pixels-1 && show.height>=48*pixels-1)
        compose.onNodeWithTag("reduced:retry").assertIsDisplayed()
        compose.runOnUiThread { chrome.show() };repeat(2) { compose.mainClock.advanceTimeByFrame() }
        compose.onNodeWithTag("viewer-details").assertIsDisplayed()
        compose.onNodeWithTag("viewer-show-controls").assertDoesNotExist()
        compose.mainClock.autoAdvance=true
    }
}

private fun motionScreenshot(name: String) {
    val instrumentation=InstrumentationRegistry.getInstrumentation();val bitmap=requireNotNull(instrumentation.uiAutomation.takeScreenshot())
    try {
        val context=instrumentation.targetContext
        val values=android.content.ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME,"$name.png");put(MediaStore.MediaColumns.MIME_TYPE,"image/png")
            put(MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
        }
        val uri=requireNotNull(context.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
        requireNotNull(context.contentResolver.openOutputStream(uri)).use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) }
    } finally { bitmap.recycle() }
}

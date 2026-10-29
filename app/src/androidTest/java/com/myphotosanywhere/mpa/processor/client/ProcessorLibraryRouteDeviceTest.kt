package com.myphotosanywhere.mpa.processor.client

import androidx.compose.ui.test.*
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.NativeScreenshotEvidence
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.lifecycle.ViewModelProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.myphotosanywhere.mpa.MainActivity
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ProcessorLibraryRouteDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    @Test fun nativeSettingsRouteAndDraftSurviveRotationWithoutGoogleAccess() {
        compose.onNodeWithTag("nav:Collections").performClick()
        compose.onNodeWithTag("shell:storage").performClick()
        compose.onNodeWithTag("management:Settings").performClick()
        compose.onNodeWithTag("management:Shared library").performScrollTo().assertIsDisplayed().performClick()
        compose.onNodeWithTag("processor:connection").assertExists()
        val model=compose.runOnUiThread { ViewModelProvider(compose.activity)[ProcessorLibraryConnectionViewModel::class.java] }
        compose.waitUntil(10000) { model.connection.state.value.phase==ProcessorLibraryConnectionPhase.IDLE }
        assertNull(model.connection.state.value.accepted)
        compose.onNodeWithTag("processor:connection-code").performScrollTo().assertIsEnabled().performTextInput("mpa-library-v1:synthetic-draft")
        compose.activityRule.scenario.recreate()
        compose.waitUntil(10000) { compose.onAllNodesWithTag("processor:connection-code").fetchSemanticsNodes().isNotEmpty() }
        compose.onNodeWithTag("processor:connection-code").performScrollTo().assertTextContains("mpa-library-v1:synthetic-draft")
        val restored=compose.runOnUiThread { ViewModelProvider(compose.activity)[ProcessorLibraryConnectionViewModel::class.java] }
        assertSame(model,restored);assertNull(restored.connection.state.value.accepted);assertNull(restored.connection.state.value.review)
        compose.mainClock.advanceTimeBy(1000);compose.waitForIdle()
        val image=compose.onRoot().captureToImage().asAndroidBitmap()
        try { NativeScreenshotEvidence(InstrumentationRegistry.getInstrumentation().targetContext).write("processor-native-route",image) }
        finally { image.recycle() }
    }
}

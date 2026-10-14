package com.myphotosanywhere.mpa

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.*
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.unit.*
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.library.*
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ViewerSurfaceDeviceTest {
    @get:Rule val compose=createComposeRule()
    @Test fun immersiveControlsKeepTargetsAndStatusInLargeLightLayout()=verify(false,LayoutDirection.Ltr)
    @Test fun immersiveControlsKeepTargetsAndStatusInLargeDarkRtlLayout()=verify(true,LayoutDirection.Rtl)
    private fun verify(dark: Boolean,direction: LayoutDirection) {
        var pixels=0f
        compose.setContent {
            pixels=LocalDensity.current.density
            CompositionLocalProvider(LocalDensity provides Density(pixels,2f),LocalLayoutDirection provides direction) {
                MaterialTheme(colorScheme=if(dark) darkColorScheme() else lightColorScheme()) {
                    val chrome=rememberSaveable(saver=ViewerChromeController.saver) { ViewerChromeController() }
                    Surface(color=Color.Black,contentColor=Color.White,modifier=Modifier.width(320.dp).fillMaxHeight()) {
                        ViewerSurface(chrome,
                            topBar={ ViewerTopBar(null,"צילום.png",{},{ chrome.show() },{ chrome.hide() }) },
                            bottomBar={ Text("Viewer actions",Modifier.heightIn(min=48.dp).testTag("surface:actions")) },
                            status={ Text("Preview only — retry remains available",Modifier.testTag("surface:status")) }) { inset ->
                            Box(Modifier.fillMaxSize().background(Color(0xff17785a)).testTag("surface:canvas")) {
                                Text("Preview",Modifier.align(Alignment.BottomCenter).padding(bottom=inset))
                            }
                        }
                    }
                }
            }
        }
        val before=compose.onNodeWithTag("surface:canvas").fetchSemanticsNode().boundsInRoot
        val hide=compose.onNodeWithTag("viewer-hide-controls").assertIsDisplayed().assertContentDescriptionEquals("Hide controls")
        val hb=hide.fetchSemanticsNode().boundsInRoot;assertTrue(hb.width>=48*pixels-1);assertTrue(hb.height>=48*pixels-1)
        hide.performClick()
        compose.onNodeWithTag("viewer-details").assertDoesNotExist();compose.onNodeWithTag("surface:actions").assertDoesNotExist()
        compose.onNodeWithTag("surface:status").assertIsDisplayed()
        val after=compose.onNodeWithTag("surface:canvas").fetchSemanticsNode().boundsInRoot
        assertEquals(before,after)
        val show=compose.onNodeWithTag("viewer-show-controls").assertIsDisplayed().assertContentDescriptionEquals("Show controls")
        val sb=show.fetchSemanticsNode().boundsInRoot;assertTrue(sb.width>=48*pixels-1);assertTrue(sb.height>=48*pixels-1)
        val instrumentation=InstrumentationRegistry.getInstrumentation();val image=requireNotNull(instrumentation.uiAutomation.takeScreenshot())
        try {
            val context=instrumentation.targetContext
            val values=android.content.ContentValues().apply {
                put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,if(dark) "viewer-immersive-dark-rtl.png" else "viewer-immersive-light.png")
                put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png");put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
            }
            val uri=requireNotNull(context.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
            requireNotNull(context.contentResolver.openOutputStream(uri)).use { check(image.compress(android.graphics.Bitmap.CompressFormat.PNG,100,it)) }
        } finally { image.recycle() }
        show.performClick();compose.onNodeWithTag("viewer-details").assertIsDisplayed();compose.onNodeWithTag("surface:actions").assertIsDisplayed()
        assertEquals(before,compose.onNodeWithTag("surface:canvas").fetchSemanticsNode().boundsInRoot)
    }
}

package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.lifecycle.ViewModelStore
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.library.LibraryFeaturesModel
import com.myphotosanywhere.mpa.library.PhotoDetails
import com.myphotosanywhere.mpa.library.ViewerTopBar
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class ViewerChromeDeviceTest {
    @get:Rule val compose=createComposeRule()
    @Test fun lightViewerControlsFitCompactWidth()=verifyBar(false,LayoutDirection.Ltr)
    @Test fun darkRtlViewerControlsFitCompactWidth()=verifyBar(true,LayoutDirection.Rtl)
    @Test fun largeTextDetailsActionsRemainReachable()=verifyDetails(false,LayoutDirection.Ltr,"light-large")
    @Test fun largeTextRtlDetailsActionsRemainReachable()=verifyDetails(true,LayoutDirection.Rtl,"dark-rtl-large")

    private fun verifyBar(dark: Boolean,direction: LayoutDirection) {
        var pixels=0f
        var details=false
        var close=false
        compose.setContent {
            pixels=LocalDensity.current.density
            CompositionLocalProvider(LocalDensity provides Density(pixels,2f),LocalLayoutDirection provides direction) {
                MaterialTheme(colorScheme=if(dark) darkColorScheme() else lightColorScheme()) {
                    Box(Modifier.width(320.dp).background(Color.Black)) {
                        ViewerTopBar(2_000_000_000_000L,"A long original filename.png",{ close=true },{ details=true })
                    }
                }
            }
        }
        val back=compose.onNodeWithTag("viewer-close").assertIsDisplayed().fetchSemanticsNode().boundsInRoot
        val info=compose.onNodeWithTag("viewer-details").assertIsDisplayed().fetchSemanticsNode().boundsInRoot
        for(bounds in listOf(back,info)) {
            assertTrue(bounds.width>=48*pixels-1);assertTrue(bounds.height>=48*pixels-1)
        }
        assertFalse(back.overlaps(info))
        if(direction==LayoutDirection.Rtl) assertTrue(info.right<=back.left) else assertTrue(back.right<=info.left)
        compose.onNodeWithTag("viewer-details").assertContentDescriptionEquals("Photo details").performClick();assertTrue(details)
        compose.onNodeWithTag("viewer-close").assertContentDescriptionEquals("Close").performClick();assertTrue(close)
    }

    private fun verifyDetails(dark: Boolean,direction: LayoutDirection,name: String) {
        val app=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
        val file=File.createTempFile("details-layout",".png",app.cacheDir)
        val bitmap=Bitmap.createBitmap(32,32,Bitmap.Config.ARGB_8888)
        try { file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { bitmap.recycle() }
        val photo=Photo("details-layout","צילום-משפחה.png",file.path,file.path,"image/png",file.length(),2_000_000_000_000L,"fixture")
        val store=ViewModelStore()
        lateinit var model: LibraryFeaturesModel
        compose.runOnIdle { model=LibraryFeaturesModel(app);store.put("details",model) }
        val open=mutableStateOf(true)
        var pixels=0f
        try {
            compose.setContent {
                pixels=LocalDensity.current.density
                CompositionLocalProvider(LocalDensity provides Density(pixels,2f),LocalLayoutDirection provides direction) {
                    MaterialTheme(colorScheme=if(dark) darkColorScheme() else lightColorScheme()) {
                        if(open.value) PhotoDetails(photo,model,emptyList(),emptyList(),emptyList(),emptyList(),
                            onClose={ open.value=false },onEdit={},onAI={},onExport={},showPreview=false)
                    }
                }
            }
            compose.waitUntil(10000) { compose.onAllNodesWithTag("photo-details-close").fetchSemanticsNodes().isNotEmpty() }
            for(tag in listOf("photo-edit","photo-details-close")) {
                val bounds=compose.onNodeWithTag(tag).assertIsDisplayed().fetchSemanticsNode().boundsInRoot
                assertTrue(bounds.width>=48*pixels-1);assertTrue(bounds.height>=48*pixels-1)
            }
            compose.onNodeWithText("Name or set location").performScrollTo().assertIsDisplayed()
            compose.onNodeWithText("Export original").performScrollTo().assertIsDisplayed()
            val screenshot=requireNotNull(InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot())
            try {
                val values=android.content.ContentValues().apply {
                    put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"viewer-details-$name.png")
                    put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png")
                    put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
                }
                val uri=requireNotNull(app.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
                requireNotNull(app.contentResolver.openOutputStream(uri)).use { check(screenshot.compress(Bitmap.CompressFormat.PNG,100,it)) }
            } finally { screenshot.recycle() }
            compose.onNodeWithTag("photo-details-close").performClick()
            compose.onNodeWithTag("photo-details").assertDoesNotExist()
        } finally { compose.runOnIdle { store.clear() };file.delete() }
    }
}

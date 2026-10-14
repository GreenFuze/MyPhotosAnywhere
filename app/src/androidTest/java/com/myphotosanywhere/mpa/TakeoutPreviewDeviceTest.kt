package com.myphotosanywhere.mpa

import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.platform.*
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.unit.*
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.importing.*
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class TakeoutPreviewDeviceTest {
    @get:Rule val compose=createComposeRule()
    @Test fun metadataFiltersKeepSelectionInLightLargeText()=verify(false,LayoutDirection.Ltr)
    @Test fun metadataFiltersKeepSelectionInDarkRtlLargeText()=verify(true,LayoutDirection.Rtl)
    private fun verify(dark: Boolean,direction: LayoutDirection) {
        fun media(id: String,raw: String?=null)=ImportItem(id,"preview","content://fixture","$id.jpg","$id.jpg","image/jpeg",12,metadata=raw)
        val raw="""{"title":"named.jpg","photoTakenTime":{"timestamp":"123"},"people":[{"name":"Synthetic person"}]}"""
        val entries=mutableStateOf(listOf(media("named",raw),media("missing"),media("invalid","{"),
            media("unmatched").copy(mime="application/json",state="UNRESOLVED",selected=false,error="Matching media missing")))
        val original=entries.value;var selections=0;var closed=false;var pixels=0f
        compose.setContent {
            pixels=LocalDensity.current.density
            CompositionLocalProvider(LocalDensity provides Density(pixels,1.5f),LocalLayoutDirection provides direction) {
                MaterialTheme(colorScheme=if(dark) darkColorScheme() else lightColorScheme()) {
                    ImportPreviewDialog(ImportJob("preview","binding","TAKEOUT"),entries.value,false,{ id,selected ->
                        selections++;entries.value=entries.value.map { if(it.id==id) it.copy(selected=selected) else it }
                    },{ closed=true })
                }
            }
        }
        compose.waitUntil(10000) { compose.onAllNodesWithTag("takeout-metadata-summary").fetchSemanticsNodes().size==1 }
        compose.onNodeWithText("1 media items with people labels · 1 different names").performScrollTo().assertIsDisplayed()
        compose.onNodeWithText("Selected media: 3 · with people labels: 1").assertExists()
        val people=compose.onNodeWithTag("takeout-filter:PEOPLE").performScrollTo().assertIsDisplayed()
        val bounds=people.fetchSemanticsNode().boundsInRoot
        assertTrue(bounds.width>=48*pixels-1);assertTrue(bounds.height>=48*pixels-1)
        people.performClick().assertIsSelected()
        compose.onNodeWithTag("import-preview-item:named").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("import-preview-item:missing").assertDoesNotExist()
        compose.runOnIdle { assertEquals(original,entries.value);assertEquals(0,selections) }
        compose.onNodeWithTag("import-preview-select:named").performClick()
        compose.onNodeWithText("Selected media: 2 · with people labels: 0").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("takeout-filter:MISSING").performScrollTo().performClick()
        compose.onNodeWithTag("import-preview-item:missing").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("import-preview-item:named").assertDoesNotExist()
        compose.onNodeWithTag("takeout-filter:REVIEW").performScrollTo().performClick()
        compose.onNodeWithTag("import-preview-item:invalid").performScrollTo().assertIsDisplayed()
        compose.onNodeWithText("Metadata could not be read. Source retained; review this item.").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("import-preview-item:unmatched").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("import-preview-select:unmatched").assertIsNotEnabled()
        compose.onNodeWithTag("takeout-filter:ALL").performScrollTo().performClick()
        compose.onNodeWithTag("takeout-metadata-summary").performScrollTo().assertIsDisplayed()
        compose.waitForIdle()
        screenshot(if(dark) "takeout-preview-dark-rtl" else "takeout-preview-light")
        compose.runOnIdle {
            assertEquals(1,selections);assertEquals(original.map { if(it.id=="named") it.copy(selected=false) else it },entries.value)
        }
        compose.onNodeWithText("Close").performClick();compose.runOnIdle { assertTrue(closed) }
    }
    @Test fun pickerPreviewKeepsItsExistingSemantics() {
        val item=ImportItem("picker","job","content://fixture","picked.jpg","picked.jpg","image/jpeg",0)
        compose.setContent { MaterialTheme { ImportPreviewDialog(ImportJob("job","binding","PICKER"),listOf(item),true,{ _,_ -> error("Busy preview must not select") },{}) } }
        compose.onNodeWithTag("import-preview-item:picker").assertExists()
        compose.onNodeWithTag("import-preview-select:picker").assertIsNotEnabled()
        compose.onNodeWithTag("takeout-filter:PEOPLE").assertDoesNotExist()
        compose.onNodeWithTag("takeout-metadata-summary").assertDoesNotExist()
    }
    private fun screenshot(name: String) {
        val instrumentation=InstrumentationRegistry.getInstrumentation()
        val image=requireNotNull(instrumentation.uiAutomation.takeScreenshot())
        try {
            val context=instrumentation.targetContext
            val values=android.content.ContentValues().apply {
                put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"$name.png")
                put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png")
                put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
            }
            val uri=requireNotNull(context.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
            context.contentResolver.openOutputStream(uri)!!.use { assertTrue(image.compress(android.graphics.Bitmap.CompressFormat.PNG,100,it)) }
        } finally { image.recycle() }
    }
}

package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.unit.*
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.search.*
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class SearchChromeDeviceTest {
    @get:Rule val compose=createComposeRule()
    @Test fun searchControlsRemainAccessibleWithLargeText()=verify(false,LayoutDirection.Ltr)
    @Test fun searchControlsAndFiltersRemainAccessibleInDarkRtl()=verify(true,LayoutDirection.Rtl)
    private fun verify(dark: Boolean,direction: LayoutDirection) {
        var pixels=0f;var text by mutableStateOf("beach");var filters by mutableStateOf(false)
        compose.setContent {
            pixels=LocalDensity.current.density
            CompositionLocalProvider(LocalDensity provides Density(pixels,2f),LocalLayoutDirection provides direction) {
                MaterialTheme(colorScheme=if(dark) darkColorScheme() else lightColorScheme()) {
                    Box(Modifier.width(320.dp).fillMaxHeight()) {
                        SearchTopBar(text,false,{ text=it },{}, {},{ filters=true })
                        if(filters) SearchFilters(SearchCriteria(text),emptyList(),{ filters=false },{ filters=false })
                    }
                }
            }
        }
        for(tag in listOf("search:back","search:query","search:clear","shell:filters")) {
            val bounds=compose.onNodeWithTag(tag).assertIsDisplayed().fetchSemanticsNode().boundsInRoot
            assertTrue(bounds.width>=48*pixels-1);assertTrue(bounds.height>=48*pixels-1)
        }
        compose.onNodeWithTag("search:clear").performClick();assertEquals("",text)
        val label=mutableListOf<androidx.compose.ui.text.TextLayoutResult>()
        compose.onNodeWithText("Search your photos",useUnmergedTree=true)
            .performSemanticsAction(androidx.compose.ui.semantics.SemanticsActions.GetTextLayoutResult) { it(label) }
        assertEquals("Search label remains one line at enlarged font scale",1,label.single().lineCount)
        compose.onNodeWithTag("shell:filters").performClick()
        compose.onNodeWithTag("search:type:VIDEOS").performClick().assertIsSelected()
        compose.onNodeWithTag("search:apply").performScrollTo().assertIsDisplayed()
        val image=requireNotNull(InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot())
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        try {
            val values=android.content.ContentValues().apply {
                put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,if(dark) "search-dark-rtl-large.png" else "search-light-large.png")
                put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png");put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
            }
            val uri=requireNotNull(context.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
            requireNotNull(context.contentResolver.openOutputStream(uri)).use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) }
        } finally { image.recycle() }
    }
}

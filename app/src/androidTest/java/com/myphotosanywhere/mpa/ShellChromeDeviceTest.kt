package com.myphotosanywhere.mpa

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.background
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.text.TextLayoutResult
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import android.graphics.Bitmap
import android.provider.MediaStore
import com.myphotosanywhere.mpa.ui.ShellBottomBar
import com.myphotosanywhere.mpa.ui.ShellDestination
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ShellChromeDeviceTest {
    @get:Rule val compose=createComposeRule()

    @Test fun compactNavigationHasLabeledSelectableTargets() = verifyNavigation("light",1f,false)

    @Test fun darkNavigationHasLabeledSelectableTargets() = verifyNavigation("dark",1f,true)

    @Test fun enlargedTextKeepsEveryNavigationTargetReachable() = verifyNavigation("large",2f,true)

    @Test fun navigationMirrorsWithoutOverlappingTargets() = verifyNavigation("rtl",1f,false,LayoutDirection.Rtl)

    private fun verifyNavigation(name: String,fontScale: Float,dark: Boolean,direction: LayoutDirection=LayoutDirection.Ltr) {
        val selected=mutableStateOf(ShellDestination.PHOTOS)
        var pixelsPerDp=0f
        compose.setContent {
            val density=LocalDensity.current.density
            pixelsPerDp=density
            CompositionLocalProvider(LocalDensity provides Density(density,fontScale),LocalLayoutDirection provides direction) {
                MaterialTheme(colorScheme=if(dark) darkColorScheme() else lightColorScheme()) {
                    Box(Modifier.width(320.dp).background(MaterialTheme.colorScheme.background).testTag("navigation:fixture")) {
                        ShellBottomBar(selected.value,{ selected.value=it })
                    }
                }
            }
        }
        compose.onNodeWithTag("nav:Photos").assertIsSelected()
        compose.onNodeWithTag("nav:Search").assertContentDescriptionEquals("Search")
        saveScreenshot(name)
        for(destination in listOf(ShellDestination.COLLECTIONS,ShellDestination.SEARCH,ShellDestination.PHOTOS)) {
            val target=compose.onNodeWithTag("nav:${destination.title}")
            target.assertIsDisplayed().performClick().assertIsSelected()
            compose.waitForIdle()
            saveScreenshot(name)
            assertEquals(destination,selected.value)
            for(label in listOf("Photos","Collections")) {
                val layouts=mutableListOf<TextLayoutResult>()
                compose.onNodeWithText(label).performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(layouts) }
                assertEquals("Navigation label stays on one line",1,layouts.single().lineCount)
                assertFalse("Navigation label is not clipped: selected=$destination label=$label size=${layouts.single().size} width=${layouts.single().didOverflowWidth} height=${layouts.single().didOverflowHeight} lineWidth=${layouts.single().getLineRight(0)-layouts.single().getLineLeft(0)}",layouts.single().hasVisualOverflow)
            }
            val bounds=listOf("Photos","Collections","Search").map { label ->
                compose.onNodeWithTag("nav:$label").assertIsDisplayed().fetchSemanticsNode().boundsInRoot
            }
            bounds.forEach {
                assertTrue("Minimum 48dp target width",it.width>=48*pixelsPerDp-1)
                assertTrue("Minimum 48dp target height",it.height>=48*pixelsPerDp-1)
            }
            for(i in bounds.indices) for(j in i+1 until bounds.size) {
                assertFalse("Navigation targets do not overlap",bounds[i].overlaps(bounds[j]))
            }
            if(direction==LayoutDirection.Rtl) assertTrue("Search mirrors to the start",bounds[2].right<=bounds[0].left)
            else assertTrue("Search follows the navigation pill",bounds[2].left>=bounds[0].right)
        }
    }

    private fun saveScreenshot(name: String) {
        if(android.os.Build.VERSION.SDK_INT<29) return
        val app=InstrumentationRegistry.getInstrumentation().targetContext
        val bitmap=compose.onNodeWithTag("navigation:fixture").captureToImage().asAndroidBitmap()
        val values=android.content.ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME,"shell-navigation-$name.png")
            put(MediaStore.MediaColumns.MIME_TYPE,"image/png")
            put(MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
        }
        val uri=requireNotNull(app.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
        app.contentResolver.openOutputStream(uri)?.use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) }
            ?: error("Cannot save navigation screenshot")
    }
}

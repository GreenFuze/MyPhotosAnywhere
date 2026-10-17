package com.myphotosanywhere.mpa.people

import android.graphics.Bitmap
import android.provider.MediaStore
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.*
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.LayoutDirection
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.Person
import com.myphotosanywhere.mpa.library.LibraryFeaturesModel
import com.myphotosanywhere.mpa.library.PersonManagementDialog
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class PeopleMenusDeviceTest {
    @get:Rule val compose=createComposeRule()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    @Test fun sharedPersonMenuAndRenameFitDarkRtlAtDoubleFontSize() {
        val model=LibraryFeaturesModel(app);val people=listOf(Person("visual-person","כינרת / Samuel"))
        var pixelsPerDp=0f
        var actionColor=0
        compose.setContent {
            val native=LocalDensity.current;pixelsPerDp=native.density
            CompositionLocalProvider(LocalDensity provides Density(native.density,2f),LocalLayoutDirection provides LayoutDirection.Rtl) {
                MaterialTheme(colorScheme=darkColorScheme()) {
                    actionColor=MaterialTheme.colorScheme.primary.toArgb()
                    Box(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.surfaceContainerHigh)) {
                        PersonManagementDialog(people.single(),people,model) { }
                    }
                }
            }
        }
        compose.onNodeWithTag("person:rename").assertHasClickAction().assertIsDisplayed()
        assertTrue(compose.onNodeWithTag("person:rename").fetchSemanticsNode().boundsInRoot.height>=48*pixelsPerDp)
        compose.onNodeWithTag("person:rename").performClick()
        compose.onNodeWithTag("person:name").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("person:save").assertIsEnabled().assertIsDisplayed()
        screenshot("person-rename-dark-rtl-large","person:save",actionColor)
        compose.onNodeWithText("Back").performClick();compose.onNodeWithTag("person:rename").assertIsDisplayed()
    }
    @Test fun mergeChooserSearchesLargePeopleListInLightThemeWithoutAnUnboundedMenu() {
        val model=LibraryFeaturesModel(app);val people=(0 until 500).map { Person("visual-$it","Person $it") }
        var actionColor=0
        compose.setContent { MaterialTheme(colorScheme=lightColorScheme()) {
            actionColor=MaterialTheme.colorScheme.primary.toArgb()
            PersonManagementDialog(people.first(),people,model) { }
        } }
        compose.onNodeWithTag("person:merge").performClick()
        compose.onNodeWithTag("person:find").performScrollTo().performTextInput("Person 499")
        compose.onNodeWithTag("person:choices").performScrollToNode(hasTestTag("person:merge-target:visual-499"))
        compose.onNodeWithTag("person:merge-target:visual-499").performClick().assertIsSelected()
        compose.onNodeWithTag("person:confirm-merge").assertIsEnabled().assertIsDisplayed()
        screenshot("person-merge-light","person:confirm-merge",actionColor)
        compose.onNodeWithText("Back").performClick();compose.onNodeWithTag("person:merge").assertIsDisplayed()
    }
    private fun screenshot(name: String,actionTag: String,actionColor: Int) {
        val frame=DialogFrame(compose.onNodeWithTag(actionTag).fetchSemanticsNode().boundsInWindow,actionColor)
        var accepted: Bitmap?=null
        try {
            // Semantics can be ready before the new dialog surface reaches the compositor.
            // Save the exact verified frame, never an unchecked second screenshot or a blank PNG.
            compose.waitUntil(10000) {
                val candidate=requireNotNull(InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot())
                if(frame.isDrawn(candidate)) { accepted=candidate;true }
                else { candidate.recycle();false }
            }
            val image=requireNotNull(accepted)
            val values=android.content.ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME,"$name.png");put(MediaStore.MediaColumns.MIME_TYPE,"image/png");put(MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
            }
            val uri=requireNotNull(app.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
            requireNotNull(app.contentResolver.openOutputStream(uri)).use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) }
        } finally { accepted?.recycle() }
    }
    private class DialogFrame(private val bounds: Rect,private val color: Int) {
        fun isDrawn(image: Bitmap): Boolean {
            check(color!=0 && bounds.width>0 && bounds.height>0)
            var matching=0
            for(xFraction in listOf(.15f,.3f,.5f,.7f,.85f)) for(yFraction in listOf(.3f,.5f,.7f)) {
                val x=(bounds.left+bounds.width*xFraction).toInt()
                val y=(bounds.top+bounds.height*yFraction).toInt()
                check(x in 0 until image.width && y in 0 until image.height)
                val pixel=image.getPixel(x,y)
                if(kotlin.math.abs(android.graphics.Color.red(pixel)-android.graphics.Color.red(color))<=8 &&
                    kotlin.math.abs(android.graphics.Color.green(pixel)-android.graphics.Color.green(color))<=8 &&
                    kotlin.math.abs(android.graphics.Color.blue(pixel)-android.graphics.Color.blue(color))<=8) matching++
            }
            return matching>=6
        }
    }
}

package com.myphotosanywhere.mpa

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.*
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.unit.*
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.myphotosanywhere.mpa.ui.IconLabelAction
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class IconActionDeviceTest {
    @get:Rule val compose=createComposeRule()
    @Test fun iconToggleKeepsMeaningAndStateInLightLargeText()=verify(false,LayoutDirection.Ltr)
    @Test fun iconToggleKeepsMeaningAndStateInDarkRtlLargeText()=verify(true,LayoutDirection.Rtl)
    private fun verify(dark: Boolean,direction: LayoutDirection) {
        var pixels=0f
        val selected=mutableStateOf(false);val enabled=mutableStateOf(true);var calls=0
        compose.setContent {
            pixels=LocalDensity.current.density
            CompositionLocalProvider(LocalDensity provides Density(pixels,2f),LocalLayoutDirection provides direction) {
                MaterialTheme(colorScheme=if(dark) darkColorScheme() else lightColorScheme()) {
                    Surface { IconLabelAction(R.drawable.ic_shell_favorite,"Favorite",{ calls++;selected.value=!selected.value },
                        Modifier.testTag("action"),enabled=enabled.value,description=if(selected.value) "Remove from favorites" else "Add to favorites",selected=selected.value) }
                }
            }
        }
        val action=compose.onNodeWithTag("action").assertIsDisplayed().assertIsEnabled().assertIsNotSelected().assertContentDescriptionEquals("Add to favorites")
        val bounds=action.fetchSemanticsNode().boundsInRoot
        assertTrue(bounds.width>=48*pixels-1);assertTrue(bounds.height>=48*pixels-1)
        action.performClick();assertEquals(1,calls)
        action.assertIsSelected().assertContentDescriptionEquals("Remove from favorites")
        compose.runOnIdle { enabled.value=false }
        action.assertIsNotEnabled().performClick();assertEquals(1,calls)
        compose.runOnIdle { enabled.value=true }
        action.performClick();assertEquals(2,calls);action.assertIsNotSelected()
    }
}

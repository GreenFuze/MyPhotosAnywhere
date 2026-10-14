package com.myphotosanywhere.mpa

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.myphotosanywhere.mpa.selection.*
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class SelectionChromeDeviceTest {
    @get:Rule val compose=createComposeRule()
    @Test fun largeTextLightSelectionActionsRemainReachable()=verify(false,LayoutDirection.Ltr)
    @Test fun largeTextDarkRtlSelectionActionsRemainReachable()=verify(true,LayoutDirection.Rtl)
    private fun verify(dark: Boolean,direction: LayoutDirection) {
        var pixels=0f
        var action: BulkKind?=null
        compose.setContent {
            pixels=LocalDensity.current.density
            CompositionLocalProvider(LocalDensity provides Density(pixels,2f),LocalLayoutDirection provides direction) {
                MaterialTheme(colorScheme=if(dark) darkColorScheme() else lightColorScheme()) {
                    Box(Modifier.width(320.dp).fillMaxHeight()) {
                        SelectionTopBar(3,false,{})
                        SelectionBottomBar(false,false,true,{ action=it },Modifier.align(Alignment.BottomCenter))
                    }
                }
            }
        }
        for(kind in listOf(BulkKind.SHARE,BulkKind.ALBUM,BulkKind.FAVORITE,BulkKind.ARCHIVE,BulkKind.OFFLINE,BulkKind.UNFAVORITE)) {
            val node=compose.onNodeWithTag("selection:${kind.name}").assertIsDisplayed()
            val bounds=node.fetchSemanticsNode().boundsInRoot
            assertTrue(bounds.width>=48*pixels-1);assertTrue(bounds.height>=48*pixels-1)
            node.assertContentDescriptionEquals(when(kind) { BulkKind.ALBUM -> "Add to album";BulkKind.OFFLINE -> "Make available offline";else -> kind.name.lowercase().replaceFirstChar { it.uppercase() } });node.performClick();assertEquals(kind,action)
        }
        compose.onNodeWithTag("selection:REMOVE_ALBUM").assertIsDisplayed().assertContentDescriptionEquals("Remove from album").performClick();assertEquals(BulkKind.REMOVE_ALBUM,action)
        compose.onNodeWithTag("selection:close").assertIsDisplayed().assertContentDescriptionEquals("Exit selection")
    }
}

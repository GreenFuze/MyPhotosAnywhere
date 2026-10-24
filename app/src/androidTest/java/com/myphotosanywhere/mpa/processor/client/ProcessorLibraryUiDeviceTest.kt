package com.myphotosanywhere.mpa.processor.client

import android.graphics.Bitmap
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import java.io.File
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.UUID
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ProcessorLibraryUiDeviceTest {
    @get:Rule val compose=createComposeRule()
    private fun descriptor(): LibraryConnectionDescriptor {
        val publicKey=KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair().public.encoded
        return LibraryConnectionDescriptor(UUID.randomUUID(),UUID.randomUUID(),1,CatalogWorkspace("synthetic-permission","synthetic-workspace"),publicKey)
    }
    private data class Presentation(val dark: Boolean=false,val rtl: Boolean=false,val large: Boolean=false)
    private fun screenshot(label: String) {
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val directory=File(checkNotNull(context.getExternalFilesDir(null)),"screenshots").apply { check(isDirectory || mkdirs()) }
        val bitmap=compose.onRoot().captureToImage().asAndroidBitmap()
        try { File(directory,"processor-$label.png").outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } }
        finally { bitmap.recycle() }
    }
    @Test fun allPresentationsRequireAnAccessibleExplicitApproval() {
        val descriptor=descriptor(); val account=ProcessorLibraryAccount("synthetic-permission","synthetic@example.invalid")
        val review=ProcessorLibraryConnectionReview(descriptor,ProcessorLibraryWorkspace("synthetic-workspace","ספריית משפחה / Family library",7,true,false),account)
        val state=mutableStateOf(ProcessorLibraryConnectionState(phase=ProcessorLibraryConnectionPhase.REVIEW,draft=descriptor.encode(),review=review))
        val presentation=mutableStateOf(Presentation()); var approvals=0
        compose.setContent {
            val display=presentation.value; val density=LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density,if(display.large) 1.8f else 1f),
                LocalLayoutDirection provides if(display.rtl) LayoutDirection.Rtl else LayoutDirection.Ltr) {
                MaterialTheme(colorScheme=if(display.dark) darkColorScheme() else lightColorScheme()) { Surface(Modifier.fillMaxSize()) {
                    ProcessorLibraryConnectionContent(state.value,{},{ state.value=state.value.copy(acknowledged=it) },{ approvals++ },{},{},{})
                } }
            }
        }
        for(dark in listOf(false,true)) for(rtl in listOf(false,true)) for(large in listOf(false,true)) {
            compose.runOnIdle { state.value=state.value.copy(acknowledged=false);presentation.value=Presentation(dark,rtl,large) }
            compose.onNodeWithTag("processor:approve").performScrollTo().assertIsNotEnabled()
            compose.onNodeWithTag("processor:confirm-source").performScrollTo().assertHasClickAction().assertIsToggleable().assertHeightIsAtLeast(48.dp)
            compose.onNodeWithTag("processor:confirm-source").performClick().assertIsOn()
            compose.onNodeWithTag("processor:approve").performScrollTo().assertIsEnabled().assertHeightIsAtLeast(48.dp)
            screenshot("review-${if(dark) "dark" else "light"}-${if(rtl) "rtl" else "ltr"}-${if(large) "large" else "normal"}")
            compose.onNodeWithTag("processor:approve").performClick()
        }
        assertEquals(8,approvals)
    }
    @Test fun acceptedContextRemainsVisibleThroughLoadingAndFailure() {
        val descriptor=descriptor();val account=ProcessorLibraryAccount("synthetic-permission","synthetic@example.invalid")
        val accepted=ProcessorLibraryRegistration(descriptor,account)
        val state=mutableStateOf(ProcessorLibraryConnectionState(phase=ProcessorLibraryConnectionPhase.CHECKING,draft=descriptor.encode(),accepted=accepted))
        var refreshes=0
        compose.setContent { MaterialTheme { Surface(Modifier.fillMaxSize()) {
            ProcessorLibraryConnectionContent(state.value,{},{},{},{},{},{ refreshes++ })
        } } }
        compose.onNodeWithTag("processor:accepted").assertIsDisplayed()
        compose.onNodeWithTag("processor:connection-progress").assertIsDisplayed()
        val before=compose.onNodeWithTag("processor:accepted").fetchSemanticsNode().boundsInRoot
        compose.onNodeWithTag("processor:connection-code").assertDoesNotExist()
        compose.runOnIdle { state.value=state.value.copy(phase=ProcessorLibraryConnectionPhase.IDLE,message="Connection check failed; saved library retained.") }
        compose.onNodeWithTag("processor:accepted").assertIsDisplayed()
        compose.onNodeWithTag("processor:connection-message").assertIsDisplayed()
        val after=compose.onNodeWithTag("processor:accepted").fetchSemanticsNode().boundsInRoot
        assertEquals(before.top,after.top,1f);assertEquals(before.bottom,after.bottom,1f)
        compose.onNodeWithTag("processor:refresh").performScrollTo().assertIsEnabled().performClick()
        assertEquals(1,refreshes)
        screenshot("retained-connection")
    }
}

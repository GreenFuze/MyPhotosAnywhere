package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.Saver
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.myphotosanywhere.mpa.R
import com.myphotosanywhere.mpa.ui.ShellIcon

/** Session UI only. Visibility never owns media, paging, decoders or durable work. */
internal class ViewerChromeController(initiallyVisible: Boolean=true) {
    var visible by mutableStateOf(initiallyVisible)
        private set
    fun toggle() { visible=!visible }
    fun hide() { visible=false }
    fun show() { visible=true }
    companion object {
        val saver=Saver<ViewerChromeController,Boolean>(save={ it.visible },restore={ ViewerChromeController(it) })
    }
}

/** Gesture ownership follows the active identity, never a mutable pager index. */
internal class ViewerGestureState {
    private var mediaKey: String?=null
    private var zoomed=false
    fun update(key: String,value: Boolean) { mediaKey=key;zoomed=value }
    fun isZoomed(key: String)=mediaKey==key && zoomed
}

/** The media always receives the same viewport. Only caption clearance changes. */
@Composable
internal fun ViewerSurface(chrome: ViewerChromeController,modifier: Modifier=Modifier,
    topBar: @Composable ()->Unit,bottomBar: @Composable ()->Unit,status: @Composable ()->Unit,
    media: @Composable (Dp)->Unit) {
    var footerPixels by remember { mutableIntStateOf(0) }
    val density=LocalDensity.current
    val toggleLabel=stringResource(if(chrome.visible) R.string.viewer_hide_controls else R.string.viewer_show_controls)
    Box(modifier.fillMaxSize()) {
        Box(Modifier.fillMaxSize().testTag("viewer-media").semantics {
            customActions=listOf(CustomAccessibilityAction(toggleLabel) { chrome.toggle();true })
        }) { media(with(density) { footerPixels.toDp() }) }
        if(chrome.visible) Box(Modifier.align(Alignment.TopCenter).fillMaxWidth().background(Color.Black.copy(alpha=.72f))) { topBar() }
        else IconButton(onClick={ chrome.show() },modifier=Modifier.align(Alignment.TopEnd).padding(4.dp).size(48.dp)
            .background(Color.Black.copy(alpha=.72f),CircleShape).testTag("viewer-show-controls")) {
            ShellIcon(R.drawable.ic_viewer_show,stringResource(R.string.viewer_show_controls))
        }
        Column(Modifier.align(Alignment.BottomCenter).fillMaxWidth().background(Color.Black.copy(alpha=.72f))
            .onSizeChanged { footerPixels=it.height }) {
            if(chrome.visible) bottomBar()
            status()
        }
    }
}

package com.myphotosanywhere.mpa.ui

import android.content.Context
import android.view.accessibility.AccessibilityManager
import androidx.compose.animation.*
import androidx.compose.animation.core.tween
import androidx.compose.foundation.layout.Box
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.focus.focusProperties
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.clearAndSetSemantics

internal enum class MotionEdge { TOP,BOTTOM }

/** Compose animations respect the platform duration scale. Media resources are never transitioned. */
@Composable
internal fun MotionVisibility(visible: Boolean,edge: MotionEdge,modifier: Modifier=Modifier,content: @Composable ()->Unit) {
    val direction=if(edge==MotionEdge.TOP) -1 else 1
    AnimatedVisibility(visible,modifier,
        enter=fadeIn(tween(160))+slideInVertically(tween(200)) { direction*it/3 }+expandVertically(tween(200),expandFrom=if(edge==MotionEdge.TOP) Alignment.Top else Alignment.Bottom),
        exit=fadeOut(tween(120))+slideOutVertically(tween(160)) { direction*it/3 }+shrinkVertically(tween(160),shrinkTowards=if(edge==MotionEdge.TOP) Alignment.Top else Alignment.Bottom)) {
        MotionInputGate(visible,content)
    }
}

/** Only toolbar presentation is swapped. Outgoing actions cannot own input or accessibility focus. */
@Composable
internal fun <T> MotionSwap(selected: T,content: @Composable (T)->Unit) {
    AnimatedContent(selected,transitionSpec={
        (fadeIn(tween(160))+slideInVertically(tween(200)) { -it/4 }) togetherWith
            (fadeOut(tween(120))+slideOutVertically(tween(160)) { -it/4 })
    },label="Toolbar mode") { value -> MotionInputGate(value==selected) { content(value) } }
}

@Composable
private fun MotionInputGate(active: Boolean,content: @Composable ()->Unit) {
    val outgoing=if(active) Modifier else Modifier.focusProperties { canFocus=false }.clearAndSetSemantics { }.pointerInput(Unit) {
        awaitPointerEventScope {
            while(true) awaitPointerEvent(PointerEventPass.Initial).changes.forEach { it.consume() }
        }
    }
    Box(outgoing) { content() }
}

@Composable
internal fun touchExplorationEnabled(): Boolean {
    val context=LocalContext.current
    val manager=remember(context) { requireNotNull(context.getSystemService(Context.ACCESSIBILITY_SERVICE) as? AccessibilityManager) }
    var enabled by remember(manager) { mutableStateOf(manager.isTouchExplorationEnabled) }
    DisposableEffect(manager) {
        val listener=AccessibilityManager.TouchExplorationStateChangeListener { enabled=it }
        manager.addTouchExplorationStateChangeListener(listener)
        onDispose { manager.removeTouchExplorationStateChangeListener(listener) }
    }
    return enabled
}

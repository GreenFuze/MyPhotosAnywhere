package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculatePan
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp

@Composable
internal fun Modifier.viewerDetailsSwipe(key: Any,enabled: Boolean=true,zoomed: ()->Boolean={ false },onDetails: (()->Unit)?): Modifier {
    val currentAction by rememberUpdatedState(onDetails)
    val currentZoom by rememberUpdatedState(zoomed)
    if(!enabled || onDetails==null) return this
    return pointerInput(key,enabled) {
        awaitEachGesture {
            awaitFirstDown(requireUnconsumed=false)
            val gesture=ViewerDetailsGesture(72.dp.toPx(),viewConfiguration.touchSlop)
            if(currentZoom()) gesture.cancel()
            do {
                val event=awaitPointerEvent(PointerEventPass.Initial)
                val pan=event.calculatePan()
                if(gesture.move(pan.x,pan.y,event.changes.count { it.pressed },currentZoom(),event.changes.any { it.isConsumed })) {
                    event.changes.forEach { it.consume() }
                }
            } while(event.changes.any { it.pressed })
            if(gesture.finish()) currentAction?.invoke()
        }
    }
}

package com.myphotosanywhere.mpa.selection

import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.boundsInWindow
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInWindow
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.Job

/** Actual measured window rectangles handle portrait spans, RTL and moving app chrome. */
internal class TimelineSelectionHits {
    private data class Hit(val target: SelectionTarget,val rectangle: Rect)
    private val cells=LinkedHashMap<String,Hit>()
    var bounds=Rect.Zero
    var origin=Offset.Zero
    fun update(target: SelectionTarget,rectangle: Rect) { cells[target.key]=Hit(target,rectangle) }
    fun remove(key: String) { cells.remove(key) }
    fun hit(position: Offset,bottomInset: Float): SelectionTarget? {
        if(position.x<bounds.left || position.x>=bounds.right || position.y<bounds.top || position.y>=bounds.bottom-bottomInset) return null
        return cells.values.firstOrNull { it.rectangle.contains(position) }?.target
    }
}

@Composable
internal fun Modifier.selectionHit(registry: TimelineSelectionHits,target: SelectionTarget): Modifier {
    DisposableEffect(registry,target.key) { onDispose { registry.remove(target.key) } }
    return onGloballyPositioned { registry.update(target,it.boundsInWindow()) }
}

@Composable
internal fun Modifier.dragRangeSelection(registry: TimelineSelectionHits,token: Any,enabled: Boolean,
    window: List<SelectionTarget>,selection: List<SelectionTarget>,bottomInset: Dp,
    onChange: (List<SelectionTarget>)->Unit,onLimit: ()->Unit,onScroll: suspend (Float)->Float): Modifier {
    val latestWindow by rememberUpdatedState(window)
    val latestSelection by rememberUpdatedState(selection)
    val change by rememberUpdatedState(onChange)
    val limit by rememberUpdatedState(onLimit)
    val scroll by rememberUpdatedState(onScroll)
    val pixels=LocalDensity.current
    val inset by rememberUpdatedState(with(pixels) { bottomInset.toPx() })
    val speed=remember(pixels) { SelectionEdgeScroll(with(pixels) { 48.dp.toPx() },with(pixels) { 800.dp.toPx() }) }
    return onGloballyPositioned { registry.bounds=it.boundsInWindow();registry.origin=it.positionInWindow() }
        .pointerInput(registry,token,enabled,speed) {
            if(!enabled) return@pointerInput
            coroutineScope {
                val gestureScope=this
                awaitEachGesture {
                    val down=awaitFirstDown(requireUnconsumed=false,pass=PointerEventPass.Initial)
                    val downWindow=down.position+registry.origin
                    val first=registry.hit(downWindow,inset) ?: return@awaitEachGesture
                    android.util.Log.i("RangeGesture","down=$downWindow bounds=${registry.bounds} firstIndex=${latestWindow.indexOfFirst { it.key==first.key }} enabled=$enabled")
                    val baseline=latestSelection.toList()
                    // Child combinedClickable consumes its down/long-press events. Observe
                    // Initial-pass motion and our own timeout, so that consumption cannot
                    // cancel the parent range recognizer. Real scrolling/multitouch cancels.
                    var latest=down;var aborted=false
                    val early=withTimeoutOrNull(viewConfiguration.longPressTimeoutMillis) {
                        while(!aborted) {
                            val event=awaitPointerEvent(PointerEventPass.Initial)
                            val pointer=event.changes.firstOrNull { it.id==down.id }
                            aborted=pointer==null || !pointer.pressed || event.changes.count { it.pressed }>1 ||
                                (pointer.position+registry.origin-downWindow).getDistance()>viewConfiguration.touchSlop
                            if(!aborted) latest=requireNotNull(pointer)
                        }
                    }
                    if(early!=null || aborted || latestWindow.none { it.key==first.key }) {
                        android.util.Log.i("RangeGesture","longPressCancelled early=$early aborted=$aborted")
                        return@awaitEachGesture
                    }
                    android.util.Log.i("RangeGesture","longPressReady")
                    val press=latest
                    val range=DragRangeSelection(baseline,latestWindow,first.key)
                    var finger=press.position+registry.origin
                    var active=true;var atLimit=false;var announced=false
                    var lastKey: String?=null;var lastWindow: List<SelectionTarget>?=null
                    fun reconcile(target: SelectionTarget?) {
                        if(!active || target==null || lastKey==target.key && lastWindow===latestWindow) return
                        val update=range.move(latestWindow,target.key)
                        lastKey=target.key;lastWindow=latestWindow
                        if(update.cancelled) { active=false;return }
                        atLimit=update.atLimit
                        if(update.targets!=latestSelection) change(update.targets)
                        if(atLimit && !announced) { announced=true;limit() }
                    }
                    reconcile(first);press.consume()
                    var scrolling: Job?=null
                    fun edgeVelocity()=speed.velocity(finger.y,registry.bounds.top,maxOf(registry.bounds.top,registry.bounds.bottom-inset))
                    fun updateScrolling() {
                        if(!active || atLimit || edgeVelocity()==0f) { scrolling?.cancel();scrolling=null;return }
                        if(scrolling?.isActive==true) return
                        scrolling=gestureScope.launch {
                            var previous=withFrameNanos { it }
                            while(active && !atLimit && edgeVelocity()!=0f) {
                                val now=withFrameNanos { it };val elapsed=((now-previous)/1_000_000_000f).coerceIn(0f,0.032f);previous=now
                                val amount=edgeVelocity()*elapsed
                                if(amount!=0f && scroll(amount)==0f) break
                                reconcile(registry.hit(finger,inset))
                            }
                        }
                    }
                    updateScrolling()
                    try {
                        do {
                            val event=awaitPointerEvent(PointerEventPass.Initial)
                            if(event.changes.count { it.pressed }>1) active=false
                            val pointer=event.changes.firstOrNull { it.id==press.id }
                            if(pointer==null || !pointer.pressed) break
                            finger=pointer.position+registry.origin
                            val hit=registry.hit(finger,inset)
                            android.util.Log.i("RangeGesture","move=$finger hitIndex=${latestWindow.indexOfFirst { it.key==hit?.key }} active=$active")
                            reconcile(hit)
                            updateScrolling()
                            event.changes.forEach { it.consume() }
                        } while(true)
                    } finally { active=false;scrolling?.cancel() }
                }
            }
        }
}

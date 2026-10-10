package com.myphotosanywhere.mpa.library

import android.widget.VideoView
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculatePan
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import coil.compose.AsyncImage
import coil.request.ImageRequest
import com.myphotosanywhere.mpa.data.Photo
import java.io.File

@Composable
fun PhotoViewer(photos: List<Photo>,selectedId: String,model: LibraryFeaturesModel,onClose: ()->Unit,
    onCurrent: (String)->Unit,onEdit: (String)->Unit) {
    val available=remember(photos) { photos.filter { it.originalPath.isNotBlank() && File(it.originalPath).isFile }.sortedByDescending { it.capturedAt ?: it.importedAt } }
    if(available.isEmpty()) { LaunchedEffect(Unit) { onClose() }; return }
    val pager=rememberPagerState(initialPage=available.indexOfFirst { it.id==selectedId }.coerceAtLeast(0),pageCount={ available.size })
    val current=available.getOrNull(pager.currentPage) ?: available.first()
    LaunchedEffect(current.id) { onCurrent(current.id) }
    Dialog(onDismissRequest=onClose,properties=DialogProperties(usePlatformDefaultWidth=false)) {
        Surface(Modifier.fillMaxSize().testTag("viewer-screen"),color=Color.Black,contentColor=Color.White) {
            Column(Modifier.fillMaxSize().safeDrawingPadding()) {
                Row(Modifier.fillMaxWidth().padding(8.dp),horizontalArrangement=Arrangement.SpaceBetween) {
                    TextButton(modifier=Modifier.testTag("viewer-close"),onClick=onClose) { Text("Close",color=Color.White) }
                    if(current.mimeType.startsWith("image/")) TextButton(onClick={ onEdit(current.id) }) { Text("Edit",color=Color.White) }
                }
                Text(current.displayName,maxLines=1,modifier=Modifier.padding(horizontal=16.dp))
                HorizontalPager(state=pager,key={ available[it].id },modifier=Modifier.fillMaxWidth().weight(1f)) { page ->
                    val photo=available[page]
                    val reader by produceState<AutoCloseable?>(null,photo.id) {
                        value=model.app.cache.acquireReader(photo.id);awaitDispose { value?.close() }
                    }
                    var zoom by remember(photo.id) { mutableFloatStateOf(1f) }
                    var x by remember(photo.id) { mutableFloatStateOf(0f) }
                    var y by remember(photo.id) { mutableFloatStateOf(0f) }
                    LaunchedEffect(pager.currentPage) { if(pager.currentPage!=page) { zoom=1f;x=0f;y=0f } }
                    if(reader==null) Box(Modifier.fillMaxSize()) { CircularProgressIndicator() }
                    else if(photo.mimeType.startsWith("video/")) AndroidView(factory={ context -> VideoView(context).apply {
                        setVideoPath(photo.originalPath)
                        val controls=android.widget.MediaController(context);controls.setAnchorView(this);setMediaController(controls)
                        setOnPreparedListener { seekTo(1) }
                    } },modifier=Modifier.fillMaxSize(),update={ if(pager.currentPage!=page) it.pause() },onRelease={ it.stopPlayback() })
                    else Box(Modifier.fillMaxSize().clipToBounds().pointerInput(photo.id) {
                        detectTapGestures(onDoubleTap={ zoom=if(zoom>1f) 1f else 2.5f;x=0f;y=0f })
                    }.pointerInput(photo.id) {
                        awaitEachGesture {
                            awaitFirstDown(requireUnconsumed=false)
                            var transforming=zoom>1f
                            do {
                                val event=awaitPointerEvent(PointerEventPass.Initial)
                                if(event.changes.count { it.pressed }>=2) transforming=true
                                if(transforming) {
                                    val pan=event.calculatePan();val scale=event.calculateZoom()
                                    zoom=(zoom*scale).coerceIn(1f,6f)
                                    x=(x+pan.x).coerceIn(-size.width*(zoom-1)/2,size.width*(zoom-1)/2)
                                    y=(y+pan.y).coerceIn(-size.height*(zoom-1)/2,size.height*(zoom-1)/2)
                                    event.changes.forEach { it.consume() }
                                }
                            } while(event.changes.any { it.pressed })
                        }
                    }) {
                        AsyncImage(ImageRequest.Builder(model.app).data(File(photo.originalPath)).size(2048).build(),photo.displayName,
                            Modifier.fillMaxSize().graphicsLayer { scaleX=zoom;scaleY=zoom;translationX=x;translationY=y },contentScale=ContentScale.Fit)
                    }
                }
                Text("${pager.currentPage+1} / ${available.size} · Swipe to browse · Pinch or double-tap to zoom",
                    style=MaterialTheme.typography.bodySmall,modifier=Modifier.padding(16.dp))
            }
        }
    }
}

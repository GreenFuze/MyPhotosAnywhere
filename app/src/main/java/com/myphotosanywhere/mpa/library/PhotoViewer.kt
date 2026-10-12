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
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.data.MediaCache
import kotlinx.coroutines.CancellationException
import java.io.File

private sealed interface ViewerOriginal {
    data object Loading: ViewerOriginal
    data class Ready(val read: MediaCache.OriginalRead): ViewerOriginal
    data class Unavailable(val message: String): ViewerOriginal
}

@Composable
fun PhotoViewer(photos: List<Photo>,selectedId: String,model: LibraryFeaturesModel,onClose: ()->Unit,
    onCurrent: (String)->Unit,onEdit: (String)->Unit,onDetails: (String)->Unit,obscured: Boolean=false) {
    // Keep paging stable while an import inserts newer photos into the gallery.
    val available=remember(selectedId) { photos.filter { it.originalPath.isNotBlank() && File(it.originalPath).isFile }.sortedByDescending { it.capturedAt ?: it.importedAt } }
    if(available.isEmpty()) { LaunchedEffect(Unit) { onClose() }; return }
    val pager=rememberPagerState(initialPage=available.indexOfFirst { it.id==selectedId }.coerceAtLeast(0),pageCount={ available.size })
    val current=available.getOrNull(pager.currentPage) ?: available.first()
    LaunchedEffect(current.id) { onCurrent(current.id) }
    Dialog(onDismissRequest=onClose,properties=DialogProperties(usePlatformDefaultWidth=false)) {
        Surface(Modifier.fillMaxSize().testTag("viewer-screen"),color=Color.Black,contentColor=Color.White) {
            Column(Modifier.fillMaxSize().safeDrawingPadding()) {
                ViewerTopBar(current.capturedAt ?: current.importedAt,current.displayName,onClose,{ onDetails(current.id) })
                HorizontalPager(state=pager,key={ available[it].id },modifier=Modifier.fillMaxWidth().weight(1f)) { page ->
                    val photo=available[page]
                    ViewerMediaContent(photo,model,pager.currentPage==page,interactive=!obscured,onDetails={ onDetails(photo.id) })
                }
                ViewerActions(current,model,onEdit,showStatus=!obscured)
                Text("${pager.currentPage+1} / ${available.size}",
                    style=MaterialTheme.typography.bodySmall,modifier=Modifier.padding(16.dp).testTag("viewer-position"))
            }
        }
    }
}

/** The existing bounded renderer and gesture/lifecycle contract is shared by both viewer adapters. */
@Composable
internal fun ViewerMediaContent(photo: Photo,model: LibraryFeaturesModel,active: Boolean,preview: File?=null,interactive: Boolean=true,onDetails: (()->Unit)?=null) {
    Box(Modifier.fillMaxSize()) {
        if(preview!=null) coil.compose.AsyncImage(preview,"Preview",Modifier.fillMaxSize(),contentScale=androidx.compose.ui.layout.ContentScale.Fit)
    val original by produceState<ViewerOriginal>(ViewerOriginal.Loading,photo.id,photo.originalPath,photo.cachedVersion) {
        var read: MediaCache.OriginalRead?=null
        try {
            read=model.app.cache.openOriginal(photo.id)
            value=read?.let { ViewerOriginal.Ready(it) } ?: ViewerOriginal.Unavailable(
                "This downloaded original is no longer on this phone. Close and download it again.")
        } catch(e: CancellationException) { throw e }
        catch(_: Exception) { value=ViewerOriginal.Unavailable("Cannot open this original. Close and try downloading it again.") }
        awaitDispose { read?.close() }
    }
    val reader=(original as? ViewerOriginal.Ready)?.read
    var quality by remember(photo.id) { mutableStateOf("Loading original…") }
    var zoom by remember(photo.id) { mutableFloatStateOf(1f) }
    var x by remember(photo.id) { mutableFloatStateOf(0f) }
    var y by remember(photo.id) { mutableFloatStateOf(0f) }
    LaunchedEffect(active) { if(!active) { zoom=1f;x=0f;y=0f } }
    if(original is ViewerOriginal.Unavailable) Box(Modifier.fillMaxSize()) {
        Text((original as ViewerOriginal.Unavailable).message,color=Color.White,
            modifier=Modifier.align(androidx.compose.ui.Alignment.Center).padding(20.dp))
    } else if(reader==null) Box(Modifier.fillMaxSize()) { CircularProgressIndicator() }
    else if(photo.mimeType.startsWith("video/")) AndroidView(factory={ context -> VideoView(context).apply {
        setVideoPath(reader.file.absolutePath)
        val controls=android.widget.MediaController(context);controls.setAnchorView(this);setMediaController(controls)
        setOnPreparedListener { seekTo(1) }
    } },modifier=Modifier.fillMaxSize(),update={ if(!active || !interactive) it.pause() },onRelease={ it.stopPlayback() })
    else Box(Modifier.fillMaxSize().clipToBounds().viewerDetailsSwipe(photo.id,active && interactive,{ zoom>1f },onDetails).pointerInput(photo.id) {
        detectTapGestures(onDoubleTap={ zoom=if(zoom>1f) 1f else 2.5f;x=0f;y=0f })
    }.pointerInput(photo.id) {
        awaitEachGesture {
            awaitFirstDown(requireUnconsumed=false)
            var transforming=zoom>1f
            var pendingPan=Offset.Zero
            var moved=false
            do {
                val event=awaitPointerEvent(PointerEventPass.Initial)
                if(event.changes.count { it.pressed }>=2) transforming=true
                if(transforming) {
                    val pan=event.calculatePan();val scale=event.calculateZoom()
                    pendingPan+=pan
                    // Leave stationary/small-jitter taps for double-tap. A real zoomed
                    // pan claims movement in Initial before the pager handles it.
                    if(moved || event.changes.count { it.pressed }>=2 || scale!=1f || pendingPan.getDistance()>viewConfiguration.touchSlop) {
                        moved=true
                        zoom=(zoom*scale).coerceIn(1f,32f)
                        x=(x+pendingPan.x).coerceIn(-size.width*(zoom-1)/2,size.width*(zoom-1)/2)
                        y=(y+pendingPan.y).coerceIn(-size.height*(zoom-1)/2,size.height*(zoom-1)/2)
                        pendingPan=Offset.Zero
                        event.changes.forEach { it.consume() }
                    }
                }
            } while(event.changes.any { it.pressed })
        }
    }) {
        AndroidView(factory={ context -> OriginalPhotoView(context).apply {
            onQuality={ quality=it };setActive(active);load(reader.file)
        } },modifier=Modifier.fillMaxSize().testTag("original-image"),update={ it.setActive(active && interactive);it.transform(zoom,x,y) },onRelease={ it.close() })
        Text(quality,color=Color.White,style=MaterialTheme.typography.bodySmall,
            modifier=Modifier.align(androidx.compose.ui.Alignment.BottomCenter).padding(8.dp))
    }
    }
}

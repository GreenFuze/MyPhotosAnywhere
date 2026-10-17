package com.myphotosanywhere.mpa.library

import androidx.core.net.toUri
import android.widget.VideoView
import androidx.compose.foundation.background
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
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
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
    data class Device(val record: com.myphotosanywhere.mpa.data.DeviceMediaRecord): ViewerOriginal
    data class Ready(val read: MediaCache.OriginalRead): ViewerOriginal
    data class Unavailable(val message: String): ViewerOriginal
}

@Composable
fun PhotoViewer(photos: List<Photo>,selectedId: String,model: LibraryFeaturesModel,onClose: ()->Unit,
    onCurrent: (String)->Unit,onEdit: (String)->Unit,onDetails: (String)->Unit,obscured: Boolean=false,onTrash: ((Photo)->Unit)?=null) {
    // Keep paging stable while an import inserts newer photos into the gallery.
    val available=remember(selectedId) { photos.filter { it.originalPath.isNotBlank() && File(it.originalPath).isFile || it.id.startsWith("device-") }.sortedByDescending { it.capturedAt ?: it.importedAt } }
    if(available.isEmpty()) { LaunchedEffect(Unit) { onClose() }; return }
    val pager=rememberPagerState(initialPage=available.indexOfFirst { it.id==selectedId }.coerceAtLeast(0),pageCount={ available.size })
    val current=available.getOrNull(pager.currentPage) ?: available.first()
    val chrome=rememberSaveable(saver=ViewerChromeController.saver) { ViewerChromeController() }
    val gestures=remember { ViewerGestureState() }
    LaunchedEffect(current.id) { onCurrent(current.id) }
    Dialog(onDismissRequest=onClose,properties=DialogProperties(usePlatformDefaultWidth=false)) {
        Surface(Modifier.fillMaxSize().testTag("viewer-screen"),color=Color.Black,contentColor=Color.White) {
            ViewerSurface(chrome,Modifier.fillMaxSize().safeDrawingPadding().viewerDetailsSwipe(current.id,
                !obscured && current.mimeType.startsWith("image/"),{ gestures.isZoomed(current.id) },{ chrome.show();onDetails(current.id) }),
                topBar={ ViewerTopBar(current.capturedAt ?: current.importedAt,current.displayName,onClose,{ chrome.show();onDetails(current.id) },{ chrome.hide() }) },
                bottomBar={
                    ViewerActions(current,model,onEdit,showStatus=false,onTrash=onTrash?.let { action -> { action(current) } })
                    Text("${pager.currentPage+1} / ${available.size}",style=MaterialTheme.typography.bodySmall,
                        modifier=Modifier.padding(16.dp).testTag("viewer-position"))
                },status={ if(!obscured) ViewerStatus(model,Color.White) }) { captionInset ->
                HorizontalPager(state=pager,key={ available[it].id },modifier=Modifier.fillMaxSize()) { page ->
                    val photo=available[page]
                    ViewerMediaContent(photo,model,pager.currentPage==page,interactive=!obscured,
                        onToggleControls={ chrome.toggle() },captionInset=captionInset,onZoomChanged={ gestures.update(photo.id,it) })
                }
            }
        }
    }
}

/** The existing bounded renderer and gesture/lifecycle contract is shared by both viewer adapters. */
@Composable
internal fun ViewerMediaContent(photo: Photo,model: LibraryFeaturesModel,active: Boolean,preview: File?=null,interactive: Boolean=true,onDetails: (()->Unit)?=null,onToggleControls: (()->Unit)?=null,captionInset: Dp=0.dp,onZoomChanged: ((Boolean)->Unit)?=null) {
    Box(Modifier.fillMaxSize()) {
        val safePreview by produceState(preview,photo.id,photo.thumbnailPath,photo.sha256) {
            try { value=preview ?: model.app.cache.thumbnail(photo) }
            catch(error: CancellationException) { throw error }
            catch(_: java.io.IOException) { value=preview }
            catch(_: android.database.sqlite.SQLiteException) { value=preview }
        }
        if(safePreview!=null) coil.compose.AsyncImage(safePreview,"Preview",Modifier.fillMaxSize(),contentScale=androidx.compose.ui.layout.ContentScale.Fit)
    val referenceState by produceState<Pair<Boolean,com.myphotosanywhere.mpa.data.DeviceMediaRecord?>>(false to null,photo.id) {
        model.app.deviceMedia.observe(photo.id).collect { value=true to it }
    }
    val reference=referenceState.second
    val original by produceState<ViewerOriginal>(ViewerOriginal.Loading,photo.id,photo.originalPath,photo.cachedVersion,referenceState.first,reference?.available,reference?.revision) {
        var read: MediaCache.OriginalRead?=null
        try {
            read=model.app.cache.openOriginal(photo.id)
            val deviceOriginal=if(read==null) reference?.takeIf { model.app.deviceMedia.readable(photo.id) } else null
            value=read?.let { ViewerOriginal.Ready(it) } ?: deviceOriginal?.let { ViewerOriginal.Device(it) } ?: if(!referenceState.first) ViewerOriginal.Loading else ViewerOriginal.Unavailable(
                "This original is unavailable. Check device photo access or reconnect its Drive source and retry.")
        } catch(e: CancellationException) { throw e }
        catch(_: java.io.IOException) { value=ViewerOriginal.Unavailable("Cannot open this original. Check photo access or download it again.") }
        catch(_: SecurityException) { value=ViewerOriginal.Unavailable("Android photo access changed. Manage device photo access and retry.") }
        catch(_: android.database.sqlite.SQLiteException) { value=ViewerOriginal.Unavailable("Cannot read original availability. Close and retry.") }
        awaitDispose { read?.close() }
    }
    val reader=(original as? ViewerOriginal.Ready)?.read
    val device=(original as? ViewerOriginal.Device)?.record
    val toggleControls by rememberUpdatedState(onToggleControls)
    var quality by remember(photo.id) { mutableStateOf("Loading original…") }
    var zoom by remember(photo.id) { mutableFloatStateOf(1f) }
    var x by remember(photo.id) { mutableFloatStateOf(0f) }
    var y by remember(photo.id) { mutableFloatStateOf(0f) }
    val zoomedNow=zoom>1f
    SideEffect { if(active) onZoomChanged?.invoke(zoomedNow) }
    LaunchedEffect(active) { if(!active) { zoom=1f;x=0f;y=0f } }
    val previewTap=if(photo.mimeType.startsWith("image/")) Modifier.pointerInput(photo.id,active,interactive) {
        detectTapGestures(onTap={ if(active && interactive) toggleControls?.invoke() })
    } else Modifier
    if(original is ViewerOriginal.Unavailable) Box(Modifier.fillMaxSize().then(previewTap)) {
        Text((original as ViewerOriginal.Unavailable).message,color=Color.White,
            modifier=Modifier.align(androidx.compose.ui.Alignment.Center).padding(20.dp))
    } else if(reader==null && device==null) Box(Modifier.fillMaxSize().then(previewTap)) { CircularProgressIndicator() }
    else if(photo.mimeType.startsWith("video/")) BoxWithConstraints(Modifier.fillMaxSize()) {
        val frame=remember(photo.id) { VideoFrameState(device?.width ?: 0,device?.height ?: 0) }
        val fitted=if(maxWidth/maxHeight>frame.aspect) Modifier.fillMaxHeight().aspectRatio(frame.aspect,matchHeightConstraintsFirst=true)
            else Modifier.fillMaxWidth().aspectRatio(frame.aspect)
        AndroidView(factory={ context -> VideoView(context).apply {
        outlineProvider=android.view.ViewOutlineProvider.BOUNDS;clipToOutline=true
        if(device!=null) setVideoURI(device.uri.toUri()) else setVideoPath(requireNotNull(reader).file.absolutePath)
        val controls=android.widget.MediaController(context);controls.setAnchorView(this);setMediaController(controls)
        setOnPreparedListener { player -> frame.onPrepared(player.videoWidth,player.videoHeight);seekTo(1) }
        } },modifier=Modifier.align(Alignment.Center).then(fitted).clipToBounds(),update={ if(!active || !interactive) it.pause() },onRelease={ it.stopPlayback() })
        // A SurfaceView can clear its parent window before its first measured video frame.
        // Retain the available poster over that preparation interval, including on API 29.
        if(!frame.prepared) Box(Modifier.fillMaxSize().background(Color.Black)) {
            if(safePreview!=null) coil.compose.AsyncImage(safePreview,"Preview",Modifier.fillMaxSize(),contentScale=androidx.compose.ui.layout.ContentScale.Fit)
        }
    }
    else Box(Modifier.fillMaxSize().clipToBounds().viewerDetailsSwipe(photo.id,active && interactive,{ zoom>1f },onDetails).pointerInput(photo.id,active,interactive) {
        detectTapGestures(onTap={ if(active && interactive) toggleControls?.invoke() },
            onDoubleTap={ if(active && interactive) { zoom=if(zoom>1f) 1f else 2.5f;x=0f;y=0f } })
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
            onQuality={ quality=it };setActive(active)
            if(device!=null) loadReader { OriginalImageSource(android.os.ParcelFileDescriptor.AutoCloseInputStream(model.app.deviceMedia.openDescriptor(device))) }
            else load(requireNotNull(reader).file)
        } },modifier=Modifier.fillMaxSize().testTag("original-image"),update={ it.setActive(active && interactive);it.transform(zoom,x,y) },onRelease={ it.close() })
        Text(quality,color=Color.White,style=MaterialTheme.typography.bodySmall,
            modifier=Modifier.align(androidx.compose.ui.Alignment.BottomCenter).padding(bottom=captionInset).padding(8.dp))
    }
    }
}

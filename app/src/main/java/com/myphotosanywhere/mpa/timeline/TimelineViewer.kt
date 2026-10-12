package com.myphotosanywhere.mpa.timeline

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import com.myphotosanywhere.mpa.library.ViewerTopBar
import com.myphotosanywhere.mpa.library.ViewerActions
import com.myphotosanywhere.mpa.library.viewerDetailsSwipe
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import coil.compose.AsyncImage
import com.myphotosanywhere.mpa.library.LibraryFeaturesModel
import com.myphotosanywhere.mpa.library.ViewerMediaContent
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import java.io.File

/** Presentation adapter only: originals, gestures, decoders, leases and video resources remain shared. */
@Composable
internal fun TimelineViewer(state: TimelineViewerState,model: TimelineViewModel,features: LibraryFeaturesModel,
    onEdit: (String)->Unit,onDetails: (String)->Unit,onSource: ()->Unit,obscured: Boolean=false) {
    var current by remember(state.session?.id) { mutableStateOf(state.selected) }
    val currentMedia=rememberTimelineMedia(current,model.repository).media
    var remoteDetailsKey by rememberSaveable { mutableStateOf<String?>(null) }
    val openDetails: ()->Unit={ currentMedia?.photo?.let { onDetails(it.id) } ?: run { remoteDetailsKey=current.key } }
    val interactive=!obscured && remoteDetailsKey==null
    Dialog(onDismissRequest={ model.closeViewer() },properties=DialogProperties(usePlatformDefaultWidth=false)) {
        Surface(Modifier.fillMaxSize().testTag("viewer-screen"),color=Color.Black,contentColor=Color.White) {
            Column(Modifier.fillMaxSize().safeDrawingPadding()) {
                ViewerTopBar(current.millis,current.name,{ model.closeViewer() },openDetails)
                val session=state.session
                if(session==null) {
                    Box(Modifier.fillMaxWidth().weight(1f)) { TimelineViewerPage(state.selected,true,model,features,onSource,interactive,openDetails) }
                    if(state.problem!=null) {
                        Text(state.problem,Modifier.padding(horizontal=16.dp),style=MaterialTheme.typography.bodySmall)
                        TextButton(onClick={ model.open(state.selected) },modifier=Modifier.heightIn(min=48.dp)) { Text("Retry browsing") }
                    } else Text("Preparing photo sequence…",Modifier.padding(16.dp),style=MaterialTheme.typography.bodySmall)
                } else {
                    val pager=rememberPagerState(initialPage=session.initialPage,pageCount={ session.count })
                    HorizontalPager(state=pager,key={ "${session.id}:$it" },modifier=Modifier.fillMaxWidth().weight(1f)) { page ->
                        val entry by produceState<TimelineEntry?>(null,session.id,page) { value=session.entry(page) }
                        val item=entry
                        LaunchedEffect(item,pager.currentPage) {
                            if(item!=null && pager.currentPage==page) { current=item }
                        }
                        if(item!=null) TimelineViewerPage(item,pager.currentPage==page,model,features,onSource,interactive,openDetails)
                    }
                    Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween,verticalAlignment=Alignment.CenterVertically) {
                        val coroutine=rememberCoroutineScope()
                        TextButton(enabled=pager.currentPage>0,onClick={ coroutine.launchPage(pager,pager.currentPage-1) }) { Text("Previous") }
                        TextButton(enabled=pager.currentPage<session.count-1,onClick={ coroutine.launchPage(pager,pager.currentPage+1) }) { Text("Next") }
                    }
                    ViewerActions(currentMedia?.photo,features,onEdit,showStatus=interactive)
                    Text("${pager.currentPage+1} / ${session.count}",
                        style=MaterialTheme.typography.bodySmall,modifier=Modifier.padding(horizontal=16.dp,vertical=8.dp).testTag("viewer-position"))
                }
            }
        }
    }
    remoteDetailsKey?.let { key ->
        RemotePhotoDetails(current.takeIf { it.key==key },currentMedia.takeIf { current.key==key },features,
            onClose={ remoteDetailsKey=null },onSource=onSource)
    }
}

private fun kotlinx.coroutines.CoroutineScope.launchPage(pager: androidx.compose.foundation.pager.PagerState,page: Int) {
    launch { pager.animateScrollToPage(page) }
}

private class TimelineMediaState {
    var media by mutableStateOf<TimelineMedia?>(null)
    var problem by mutableStateOf<String?>(null)
    var attempt by mutableIntStateOf(0)
}

@Composable
private fun rememberTimelineMedia(entry: TimelineEntry,repository: TimelineRepository): TimelineMediaState {
    val state=remember(entry) { TimelineMediaState() }
    LaunchedEffect(entry,state.attempt) {
        state.problem=null
        try { repository.media(entry).collect { state.media=it } }
        catch(cancelled: CancellationException) { throw cancelled }
        catch(error: android.database.sqlite.SQLiteException) {
            kotlinx.coroutines.currentCoroutineContext().ensureActive()
            state.problem=error.message ?: "Cannot read photo availability. Retry; the preview remains visible."
        }
    }
    return state
}

@Composable
private fun TimelineViewerPage(entry: TimelineEntry,active: Boolean,model: TimelineViewModel,features: LibraryFeaturesModel,onSource: ()->Unit,interactive: Boolean,onDetails: ()->Unit) {
    val resolution=rememberTimelineMedia(entry,model.repository)
    val media=resolution.media
    val busy by features.busy.collectAsStateWithLifecycle()
    val tasks by features.downloads.collectAsStateWithLifecycle()
    val task=tasks.find { it.key==entry.cloudKey }
    var attempted by remember(entry.key) { mutableStateOf(false) }
    var available by remember(entry.key) { mutableStateOf(false) }
    LaunchedEffect(media) { available=media?.let { model.repository.originalAvailable(it) } ?: false }
    LaunchedEffect(media?.cloud?.key,active,busy) {
        val value=media
        if(value!=null && active && !busy && !attempted && !model.repository.originalAvailable(value) && value.cloud!=null) {
            attempted=true
            // Preserve durable failure/authentication states until the user chooses recovery.
            val existing=model.repository.database.cloud().download(value.cloud.key)
            if(existing?.state !in setOf("AUTH_REQUIRED","FAILED","PENDING","DOWNLOADING")) features.download(value.cloud)
        }
    }
    val preview=entry.thumbnailPath.takeIf { it.isNotBlank() }?.let(::File)
    Box(Modifier.fillMaxSize().then(if(media?.photo==null) Modifier.viewerDetailsSwipe(entry.key,active && interactive,onDetails=onDetails) else Modifier)) {
        if(preview!=null) AsyncImage(preview,"Preview",Modifier.fillMaxSize(),contentScale=ContentScale.Fit)
        val photo=media?.photo
        if(photo!=null) ViewerMediaContent(photo,features,active,preview,interactive,onDetails)
        if(!available) Column(Modifier.align(Alignment.BottomCenter).fillMaxWidth().padding(12.dp),horizontalAlignment=Alignment.CenterHorizontally) {
            Text(when {
                resolution.problem!=null -> requireNotNull(resolution.problem)
                task?.state=="AUTH_REQUIRED" -> "Preview only — reconnect this Drive source."
                task?.state=="FAILED" -> task.error ?: "Original download failed. Preview remains available."
                media?.cloud==null -> "Preview only — this original is unavailable."
                else -> "Preview only — waiting for a verified original. Network preference applies."
            },color=Color.White,style=MaterialTheme.typography.bodySmall)
            if(resolution.problem!=null) TextButton(onClick={ resolution.attempt++ }) { Text("Retry availability") }
            else if(task?.state=="AUTH_REQUIRED") TextButton(onClick=onSource) { Text("Manage source") }
            else media?.cloud?.let { cloud -> TextButton(enabled=!busy,onClick={ features.download(cloud,retry=true) }) { Text("Retry original") } }
        }
    }
}

package com.myphotosanywhere.mpa.timeline

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import androidx.core.content.ContextCompat
import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import com.myphotosanywhere.mpa.selection.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.*
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.paging.LoadState
import androidx.paging.compose.collectAsLazyPagingItems
import androidx.paging.insertSeparators
import androidx.paging.map
import coil.compose.AsyncImage
import com.myphotosanywhere.mpa.library.LibraryFeaturesModel
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.*
import java.io.File
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

internal sealed interface TimelineRow {
    data class Media(val entry: TimelineEntry,val generation: Long,val query: TimelineQuery,val aspect: Float): TimelineRow
    data class Day(val date: LocalDate?,val label: String): TimelineRow
}

@Composable
internal fun deviceTimelineZone(model: TimelineViewModel): ZoneId {
    val context=LocalContext.current
    var zone by remember { mutableStateOf(ZoneId.systemDefault()) }
    DisposableEffect(context,model) {
        val receiver=object: BroadcastReceiver() {
            override fun onReceive(context: Context?,intent: Intent?) {
                if(intent?.action==Intent.ACTION_TIMEZONE_CHANGED) { zone=ZoneId.systemDefault();model.restoreAnchor() }
            }
        }
        ContextCompat.registerReceiver(context,receiver,IntentFilter(Intent.ACTION_TIMEZONE_CHANGED),ContextCompat.RECEIVER_NOT_EXPORTED)
        onDispose { context.unregisterReceiver(receiver) }
    }
    return zone
}

@Composable
internal fun TimelineGrid(query: TimelineQuery,model: TimelineViewModel,features: LibraryFeaturesModel,
    state: LazyGridState,bottomClearance: Dp,modifier: Modifier=Modifier,
    selection: List<SelectionTarget>,onToggle: (SelectionTarget)->Unit,selectionLocked: Boolean,
    hasSources: Boolean,onConnect: ()->Unit,onClearFilters: ()->Unit,onRefresh: ()->Unit,emptyActionLabel: String="Clear filters") {
    val zone=deviceTimelineZone(model)
    val density by model.density.collectAsStateWithLifecycle()
    val layout by model.layout.collectAsStateWithLifecycle()
    var dateNavigation by remember(query) { mutableStateOf(false) }
    val rowsFlow=remember(model,zone) {
        model.paging.map { data -> data.map<TimelineItem,TimelineRow> { TimelineRow.Media(it.entry,it.generation,it.query,it.aspect) }.insertSeparators { before,after ->
            val media=after as? TimelineRow.Media
            if(media==null) null else {
                val date=media.entry.millis?.let { Instant.ofEpochMilli(it).atZone(zone).toLocalDate() }
                val previous=(before as? TimelineRow.Media)?.entry?.millis?.let { Instant.ofEpochMilli(it).atZone(zone).toLocalDate() }
                if(before==null || previous!=date) TimelineRow.Day(date,date?.format(DateTimeFormatter.ofLocalizedDate(FormatStyle.FULL)) ?: "Unknown date") else null
            }
        } }
    }
    val rows=rowsFlow.collectAsLazyPagingItems()
    val retained=rows.itemSnapshotList.items
    val mediaByKey=remember(retained) { retained.filterIsInstance<TimelineRow.Media>().associateBy { it.entry.key } }
    val restoration by model.restoration.collectAsStateWithLifecycle()
    val refreshedGeneration by model.refreshedGeneration.collectAsStateWithLifecycle()
    val viewer by model.viewer.collectAsStateWithLifecycle()
    var restoring by remember(query,restoration,zone,density,layout) { mutableStateOf(true) }
    fun viewport(): TimelineAnchor? {
        val visible=state.layoutInfo.visibleItemsInfo.mapNotNull { item ->
            mediaByKey[item.key]?.takeIf { it.query==query && item.offset.y+item.size.height>state.layoutInfo.viewportStartOffset && item.offset.y<state.layoutInfo.viewportEndOffset }
                ?.entry?.let { TimelineAnchor(it.cursor,-item.offset.y) }
        }
        return TimelineViewport.choose(visible,model.anchor(query))
    }
    LaunchedEffect(query) { model.configure(query) }
    LaunchedEffect(query,restoration,zone,density,layout) {
        val anchor=model.anchor(query)
        if(anchor!=null) {
            snapshotFlow { Triple(rows.itemSnapshotList.items,rows.loadState.refresh,refreshedGeneration) }.first { (items,load,generation) ->
                load is LoadState.NotLoading && ((items.isEmpty() && generation==restoration) || items.any { (it as? TimelineRow.Media)?.generation==restoration }) || load is LoadState.Error
            }
            val index=rows.itemSnapshotList.items.indexOfFirst { (it as? TimelineRow.Media)?.entry?.key==anchor.cursor.key }
            if(index>=0) {
                state.scrollToItem(index,anchor.offset)
                // A target already visible before a seek can still have old bounds while a new
                // page/density is applied. Observe a completed layout frame before correcting pixels.
                withFrameNanos { }
                withFrameNanos { }
                // Full-span date rows can change the line selected by scrollToItem.
                // Correct against the actual media bounds after measurement.
                val placed=snapshotFlow {
                    state.layoutInfo.visibleItemsInfo.firstOrNull { it.key==anchor.cursor.key }
                }.filterNotNull().first()
                state.scrollBy((placed.offset.y+anchor.offset).toFloat())
            }
            else {
                // A real removal restores the nearest remaining identity, never a mutable old index.
                val candidates=rows.itemSnapshotList.items.mapIndexedNotNull { position,row ->
                    (row as? TimelineRow.Media)?.takeIf { it.generation==restoration }?.let { position to it.entry }
                }
                val nearest=candidates.firstOrNull { (_,entry) -> TimelineOrder.compare(entry.cursor,anchor.cursor)>=0 }
                    ?: candidates.lastOrNull()
                if(nearest!=null) state.scrollToItem(nearest.first,anchor.offset)
            }
        }
        if(anchor==null && query.search!=null) state.scrollToItem(0)
        model.restored(restoration)
        restoring=false
    }
    LaunchedEffect(query,restoring,viewer,mediaByKey) {
        if(restoring || viewer!=null) return@LaunchedEffect
        snapshotFlow {
            if(rows.loadState.refresh is LoadState.Loading) null else viewport()?.let { anchor ->
                mediaByKey[anchor.cursor.key]?.takeIf { it.generation==restoration }?.entry?.let { it to anchor.offset }
            }
        }.filterNotNull().distinctUntilChanged().collect { (entry,offset) -> model.rememberVisibleAnchor(entry,offset,restoration) }
    }
    val pinch by rememberUpdatedState<(Float)->Unit> { scale -> model.changeDensity(density.pinch(scale),viewport()) }
    if(dateNavigation) TimelineDateNavigator(query,model.repository,zone,viewport()?.cursor?.millis,
        onJump={ dateNavigation=false;model.jump(it,zone) },onClose={ dateNavigation=false })
    Column(modifier) {
        val refresh=rows.loadState.refresh
        val failure=(refresh as? LoadState.Error) ?: (rows.loadState.append as? LoadState.Error) ?: (rows.loadState.prepend as? LoadState.Error)
        if(failure!=null) TextButton(onClick=rows::retry,modifier=Modifier.fillMaxWidth().heightIn(min=48.dp).testTag("timeline:retry")) {
            Text("Library could not update. Available photos are shown. Retry")
        }
        if(rows.itemCount==0 && (refresh is LoadState.Loading || refresh is LoadState.NotLoading && refreshedGeneration!=restoration)) {
            LazyVerticalGrid(GridCells.Fixed(density.columns),modifier=Modifier.weight(1f).testTag("timeline:loading"),
                horizontalArrangement=Arrangement.spacedBy(2.dp),verticalArrangement=Arrangement.spacedBy(2.dp)) {
                items(18) { Box(Modifier.aspectRatio(1f).background(MaterialTheme.colorScheme.surfaceVariant)) }
            }
        } else if(rows.itemCount==0 && refresh is LoadState.NotLoading) Box(Modifier.weight(1f).fillMaxWidth().padding(32.dp),contentAlignment=Alignment.Center) {
            Column(horizontalAlignment=Alignment.CenterHorizontally,verticalArrangement=Arrangement.spacedBy(12.dp)) {
                val filtered=query!=TimelineQuery()
                Text(if(filtered) "No matching photos" else if(hasSources) "No photos indexed yet" else "Your photos belong here",style=MaterialTheme.typography.titleMedium)
                if(filtered) TextButton(onClick=onClearFilters,modifier=Modifier.heightIn(min=48.dp).testTag("timeline:empty-action")) { Text(emptyActionLabel) }
                else if(!hasSources) Button(onClick=onConnect) { Text("Connect Google Drive") }
                else TextButton(onClick=onRefresh) { Text("Refresh library") }
            }
        } else BoxWithConstraints(Modifier.weight(1f).fillMaxWidth()) {
        val plan=remember(retained,density,layout,maxWidth) {
            JustifiedMosaic().plan(retained.map { row -> when(row) {
                is TimelineRow.Media -> MosaicInput(row.entry.key,row.aspect)
                is TimelineRow.Day -> MosaicInput("day:${row.date}",heading=true)
            } },density.columns,(maxWidth.value-4f).coerceAtLeast(1f),layout)
        }
        LazyVerticalGrid(GridCells.Fixed(if(layout==GalleryLayout.SQUARE) plan.columns else JustifiedMosaic.SLOTS),state=state,modifier=Modifier.fillMaxSize().testTag("photos:grid")
            .semantics { stateDescription="${plan.columns} columns";collectionInfo=CollectionInfo(plan.rowCount,plan.columns) }
            .pointerInput(selection.isEmpty(),selectionLocked,restoring) {
                if(selectionLocked || restoring) return@pointerInput
                awaitEachGesture {
                    awaitFirstDown(requireUnconsumed=false,pass=PointerEventPass.Initial)
                    var scale=1f
                    var transformed=false
                    do {
                        val event=awaitPointerEvent(PointerEventPass.Initial)
                        if(event.changes.count { it.pressed && it.previousPressed }>=2) {
                            scale*=event.calculateZoom()
                            if(kotlin.math.abs(scale-1f)>0.08f) transformed=true
                            if(transformed) event.changes.forEach { it.consume() }
                        }
                    } while(event.changes.any { it.pressed })
                    if(selection.isEmpty() && transformed && scale.isFinite() && scale>0) pinch(scale)
                }
            },
            contentPadding=PaddingValues(start=2.dp,top=2.dp,end=2.dp,bottom=bottomClearance+2.dp),
            horizontalArrangement=Arrangement.spacedBy(2.dp),verticalArrangement=Arrangement.spacedBy(2.dp)) {
            items(retained.size,key={ index -> when(val row=retained[index]) {
                is TimelineRow.Media -> row.entry.key
                is TimelineRow.Day -> "day:${row.date}"
            } },span={ index -> GridItemSpan(if(layout==GalleryLayout.SQUARE) { if(plan.cells[index].heading) plan.columns else 1 } else plan.cells[index].span) },
                contentType={ index -> if(retained[index] is TimelineRow.Day) "date" else "media" }) { index ->
                // Signal Paging access, but render one immutable row/geometry snapshot together.
                // A concurrent prepend must never apply another identity's cell dimensions/actions.
                if(index<rows.itemCount) rows[index]
                when(val row=retained[index]) {
                    is TimelineRow.Day -> Text(row.label,Modifier.fillMaxWidth().padding(horizontal=12.dp,vertical=10.dp)
                        .semantics { heading();plan.cells.getOrNull(index)?.let { collectionItemInfo=CollectionItemInfo(it.row,1,0,plan.columns) } }.testTag("timeline:date:${row.date}"),style=MaterialTheme.typography.titleSmall)
                    is TimelineRow.Media -> TimelineThumbnail(row.entry,model,features,selection,onToggle,selectionLocked || row.query!=query,
                        Modifier.height(requireNotNull(plan.cells.getOrNull(index)?.height).dp).semantics {
                            val cell=plan.cells[index];collectionItemInfo=CollectionItemInfo(cell.row,1,cell.column,1)
                        }) {
                        val offset=state.layoutInfo.visibleItemsInfo.firstOrNull { it.key==row.entry.key }?.offset?.y
                        model.open(row.entry,offset?.let { -it },viewport())
                    }
                }
            }
            if(rows.loadState.append is LoadState.Loading) item(span={ GridItemSpan(maxLineSpan) }) {
                LinearProgressIndicator(Modifier.fillMaxWidth().padding(8.dp))
            }
        }
        if(restoring || refresh is LoadState.Loading) LinearProgressIndicator(Modifier.align(Alignment.TopCenter).fillMaxWidth().testTag("timeline:refreshing"))
        if(selection.isEmpty() && !selectionLocked && !restoring) TimelineDateRail(state,retained,query,zone) { dateNavigation=true }
        }
    }
}

@Composable
private fun TimelineThumbnail(entry: TimelineEntry,model: TimelineViewModel,features: LibraryFeaturesModel,selection: List<SelectionTarget>,onToggle: (SelectionTarget)->Unit,selectionLocked: Boolean,modifier: Modifier=Modifier,onOpen: ()->Unit) {
    val context=LocalContext.current
    var problem by remember(entry) { mutableStateOf<String?>(null) }
    val thumbnail by produceState<File?>(null,entry) {
        try {
            val media=model.repository.resolve(entry)
            value=media.photo?.let { features.app.cache.thumbnail(it) }
            media.cloud?.let { cloud ->
                val source=features.app.database.drive().source(cloud.bindingKey)
                com.myphotosanywhere.mpa.drive.ThumbnailWorker.request(context,cloud,source?.connection())
            }
        } catch(cancelled: CancellationException) { throw cancelled }
        catch(error: java.io.IOException) { problem=error.message ?: "Thumbnail unavailable" }
        catch(error: android.database.sqlite.SQLiteException) { problem=error.message ?: "Cannot read thumbnail availability" }
    }
    val target=SelectionTarget.from(entry)
    val chosen=selection.any { it.matches(target) }
    val selecting=selection.isNotEmpty()
    val date=entry.millis?.let { Instant.ofEpochMilli(it).atZone(ZoneId.systemDefault()).toLocalDate().toString() } ?: "Unknown date"
    Box(modifier.background(MaterialTheme.colorScheme.surfaceVariant)
        .testTag(entry.photoId?.let { "photo:$it" } ?: "cloud:${entry.cloudKey}")
        .combinedClickable(enabled=!selectionLocked,role=Role.Button,onClick={ if(selecting) onToggle(target) else onOpen() },
            onLongClickLabel="Select photo",onLongClick={ onToggle(target) }).semantics(mergeDescendants=true) {
            if(selecting) selected=chosen
            customActions=listOf(CustomAccessibilityAction(if(chosen) "Deselect photo" else "Select photo") { if(!selectionLocked) onToggle(target);!selectionLocked })
            contentDescription="${if(entry.mimeType.startsWith("video/")) "Video" else "Photo"}, $date, ${entry.name}"
        }) {
        AsyncImage(coil.request.ImageRequest.Builder(context).data(thumbnail ?: entry.thumbnailPath.takeIf { it.isNotBlank() }?.let(::File))
            .memoryCacheKey("${entry.key}:${entry.thumbnailRevision}:${entry.originalPath}").build(),null,
            modifier=Modifier.fillMaxSize(),contentScale=ContentScale.Crop,
            onError={ problem="Thumbnail unavailable. Open photo to retry." })
        if(selecting) SelectionMark(chosen)
        problem?.let { message -> Box(Modifier.align(Alignment.TopEnd).padding(4.dp)) {
            com.myphotosanywhere.mpa.ui.ShellIcon(com.myphotosanywhere.mpa.R.drawable.ic_shell_warning,message)
        } }
        if(entry.mimeType.startsWith("video/")) Box(Modifier.align(Alignment.BottomEnd).padding(4.dp)) {
            com.myphotosanywhere.mpa.ui.ShellIcon(com.myphotosanywhere.mpa.R.drawable.ic_shell_video,"Video")
        }
    }
}

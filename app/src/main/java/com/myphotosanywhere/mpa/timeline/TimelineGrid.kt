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
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.*
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import androidx.paging.LoadState
import androidx.paging.compose.collectAsLazyPagingItems
import androidx.paging.map
import coil.compose.AsyncImage
import com.myphotosanywhere.mpa.library.LibraryFeaturesModel
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.*
import java.io.File
import java.time.Instant
import java.time.ZoneId

/** One retained photo per grid item; calendar boundaries do not consume layout slots. */
internal data class TimelineRow(val entry: TimelineEntry,val generation: Long,val query: TimelineQuery,val aspect: Float)

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
    bottomClearance: Dp,modifier: Modifier=Modifier,
    selection: List<SelectionTarget>,onToggle: (SelectionTarget)->Unit,selectionLocked: Boolean,
    hasSources: Boolean,onConnect: ()->Unit,onClearFilters: ()->Unit,onRefresh: ()->Unit,emptyActionLabel: String="Clear filters",
    onRangeChange: ((List<SelectionTarget>)->Unit)?=null,onBeginSelection: (SelectionTarget)->Unit=onToggle,onSelectionLimit: ()->Unit={}) {
    val zone=deviceTimelineZone(model)
    val lifecycleOwner=LocalLifecycleOwner.current
    // Content/span callbacks must capture one immutable geometry snapshot. A delegated
    // State read inside an old callback can mix new spans with the preceding slot count.
    val density=model.density.collectAsStateWithLifecycle().value
    val layout=model.layout.collectAsStateWithLifecycle().value
    // LazyGridState owns measured placeables as well as scroll coordinates. Never move
    // that cache between incompatible slot structures; restore the canonical model anchor.
    val state=key(density,layout) { rememberLazyGridState() }
    val tiles=remember(state) { TimelineGridViewport(state) }
    val scrollScope=rememberCoroutineScope()
    val selectionHits=remember(query,density,layout) { TimelineSelectionHits() }
    var dateNavigation by remember(query) { mutableStateOf(false) }
    val rowsFlow=remember(model) {
        model.paging.map { data -> data.map { TimelineRow(it.entry,it.generation,it.query,it.aspect) } }
    }
    val rows=rowsFlow.collectAsLazyPagingItems()
    val retained=rows.itemSnapshotList.items
    val mediaByKey=remember(retained) { retained.associateBy { it.entry.key } }
    val restoration by model.restoration.collectAsStateWithLifecycle()
    val refreshedGeneration by model.refreshedGeneration.collectAsStateWithLifecycle()
    val viewer by model.viewer.collectAsStateWithLifecycle()
    var restoring by remember(query,restoration,zone,density,layout) { mutableStateOf(true) }
    fun viewport(): TimelineAnchor? {
        val visible=tiles.visible().mapNotNull { item ->
            mediaByKey[item.key]?.takeIf { it.query==query && item.offset.y+item.size.height>state.layoutInfo.viewportStartOffset && item.offset.y<state.layoutInfo.viewportEndOffset }
                ?.entry?.let { TimelineAnchor(it.cursor,-item.offset.y) }
        }
        return TimelineViewport.choose(visible,model.anchor(query))
    }
    LaunchedEffect(query) { model.configure(query) }
    LaunchedEffect(lifecycleOwner,query,restoration,zone,density,layout) {
        lifecycleOwner.lifecycle.repeatOnLifecycle(androidx.lifecycle.Lifecycle.State.STARTED) {
        restoring=true
        val anchor=model.anchor(query)
        if(anchor!=null) {
            val placement=object: TimelinePlacement {
                private var requestedIndex=0
                override suspend fun place(index: Int,offset: Int) {
                    requestedIndex=index
                    // Let paging/query recomposition install its immutable item provider before
                    // each bounded attempt; synchronous retries in one frame can measure old keys.
                    withFrameNanos { }
                    // Await attachment and force a synchronous measure through the scroll API.
                    // A deferred request can be coalesced when this position is already current;
                    // waiting for layout object identity then falsely reports a stalled measure.
                    val positioned=kotlinx.coroutines.withTimeoutOrNull(5000) {
                        tiles.scrollToItem(index,offset)
                        true
                    }
                    check(positioned==true) { "Timeline scroll did not complete for index $index; ${diagnostic()}" }
                }
                override fun diagnostic(): String = "visible=${tiles.visible().map { it.key to it.offset.y }} scroll=${state.canScrollBackward}/${state.canScrollForward}"
                override suspend fun align(key: String,offset: Int): Boolean {
                    // A completed layout can still reflect the preceding span/index cache.
                    // Missing placement must retry, even when the retained keys did not change.
                    var placed=tiles.visible().firstOrNull { it.key==key }
                    if(placed==null) {
                        // Shrinking cells may consume the entire anchored item with its
                        // formerly valid crop offset. Probe that canonical index at zero.
                        place(requestedIndex,0)
                        placed=tiles.visible().firstOrNull { it.key==key } ?: return false
                    }
                    val target=TimelineAnchorAlignment.visibleOffset(offset,placed.size.height,state.layoutInfo.viewportEndOffset)
                    val correction=placed.offset.y+target
                    if(correction!=0) state.scrollBy(correction.toFloat())
                    // scrollBy completes its synchronous remeasure before returning.
                    val corrected=tiles.visible().firstOrNull { it.key==key }
                    if(corrected==null) return false
                    val viewport=state.layoutInfo
                    val visible=corrected.offset.y+corrected.size.height>viewport.viewportStartOffset && corrected.offset.y<viewport.viewportEndOffset
                    return visible && TimelineAnchorAlignment.satisfied(corrected.offset.y,target,state.canScrollForward,state.canScrollBackward)
                }
            }
            val snapshots=snapshotFlow { Triple(rows.itemSnapshotList.items,rows.loadState.refresh,refreshedGeneration) }
                .filter { (items,load,generation) ->
                    load is LoadState.NotLoading && ((items.isEmpty() && generation==restoration) || items.any { it.generation==restoration }) || load is LoadState.Error
                }.map { (items,_,_) -> items.map { it.entry.key } }
            val restored=TimelineAnchorRestorer(placement).restore(anchor,snapshots)
            if(!restored) {
                // A real removal restores the nearest remaining identity, never a mutable old index.
                val candidates=rows.itemSnapshotList.items.mapIndexedNotNull { position,row ->
                    row.takeIf { it.generation==restoration }?.let { position to it.entry }
                }
                val nearest=candidates.firstOrNull { (_,entry) -> TimelineOrder.compare(entry.cursor,anchor.cursor)>=0 }
                    ?: candidates.lastOrNull()
                if(nearest!=null) tiles.requestScrollToItem(nearest.first,anchor.offset)
            }
        }
        if(anchor==null && query.search!=null) tiles.requestScrollToItem(0)
        model.restored(restoration)
        restoring=false
        }
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
            JustifiedMosaic().plan(retained.map { MosaicInput(it.entry.key,it.aspect) },density.columns,(maxWidth.value-4f).coerceAtLeast(1f),layout)
        }
        val pixels=LocalDensity.current
        val blocks=remember(retained,density,layout,maxWidth,rows.loadState.append) {
            if(layout==GalleryLayout.MOSAIC) BlockMosaic().plan(retained.map { MosaicInput(it.entry.key,it.aspect) },density.columns,
                (maxWidth.value-4f).coerceAtLeast(48f),(rows.loadState.append as? LoadState.NotLoading)?.endOfPaginationReached==true) else null
        }
        SideEffect {
            val held=tiles.install(blocks,pixels.density,model.anchor(query))
            if(held!=null && !restoring && viewer==null) mediaByKey[held.cursor.key]?.entry?.let {
                restoring=true;model.rememberAnchor(it,held.offset);model.restoreAnchor()
            }
        }
        val clearance=remember(plan,blocks,pixels) {
            TimelineAnchorClearance((blocks?.blocks?.map { it.height } ?: plan.cells.distinctBy { it.row }.map { requireNotNull(it.height) }).map { with(pixels) { it.dp.roundToPx() } },
                with(pixels) { 2.dp.roundToPx() },with(pixels) { 2.dp.roundToPx() })
        }
        val held=model.anchor(query)
        val heldIndex=retained.indexOfFirst { it.entry.key==held?.cursor?.key }
        val viewportPixels=with(pixels) { maxHeight.roundToPx() }
        val normalBottomPixels=with(pixels) { (bottomClearance+2.dp).roundToPx() }
        val bottomPixels=if(viewportPixels>0) clearance.bottom(blocks?.locations?.get(held?.cursor?.key)?.first ?: plan.cells.getOrNull(heldIndex)?.row,
            (held?.offset ?: 0)+(blocks?.locations?.get(held?.cursor?.key)?.second?.y?.let { with(pixels) { it.dp.roundToPx() } } ?: 0),
            viewportPixels,normalBottomPixels) else normalBottomPixels
        val bottomPadding=with(pixels) { bottomPixels.toDp() }
        val physicalColumns=if(blocks!=null) 1 else if(layout==GalleryLayout.SQUARE) plan.columns else JustifiedMosaic.SLOTS
        // Square and packed Mosaic use different slot structures. Give their layout nodes
        // distinct lifetimes with matching scroll-state lifetimes. The model retains the anchor.
        key(physicalColumns) {
        LazyVerticalGrid(GridCells.Fixed(physicalColumns),state=state,modifier=Modifier.fillMaxSize().testTag("photos:grid")
            .semantics {
                stateDescription="${plan.columns} columns";collectionInfo=CollectionInfo(blocks?.rows ?: plan.rowCount,plan.columns)
                if(blocks!=null) {
                    indexForKey { key -> retained.indexOfFirst { it.entry.key==key } }
                    scrollToIndex { index ->
                        if(index !in retained.indices) false else { scrollScope.launch { tiles.scrollToItem(index) };true }
                    }
                }
            }
            .dragRangeSelection(selectionHits,listOf(query,density,layout,restoration),
                onRangeChange!=null && !selectionLocked && !restoring && refresh !is LoadState.Loading,
                retained.map { SelectionTarget.from(it.entry) },selection,bottomClearance,
                { onRangeChange?.invoke(it) },onSelectionLimit,{ state.scrollBy(it) })
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
            contentPadding=PaddingValues(start=2.dp,top=2.dp,end=2.dp,bottom=bottomPadding),
            horizontalArrangement=Arrangement.spacedBy(2.dp),verticalArrangement=Arrangement.spacedBy(2.dp)) {
            if(blocks!=null) {
                items(blocks.blocks.size,key={ blocks.blocks[it].key },span={ GridItemSpan(maxLineSpan) },contentType={ "photo-block" }) { blockIndex ->
                    val block=blocks.blocks[blockIndex]
                    Box(Modifier.fillMaxWidth().height(block.height.dp).semantics { isTraversalGroup=true }) {
                        block.tiles.forEach { tile ->
                            key(tile.key) {
                                if(tile.index<rows.itemCount) rows[tile.index]
                                val row=retained[tile.index]
                                TimelineThumbnail(row.entry,model,features,selection,onToggle,onBeginSelection,selectionLocked || restoring || row.query!=query,zone,row.aspect,
                                    Modifier.offset(tile.x.dp,tile.y.dp).width(tile.width.dp).height(tile.height.dp)
                                        .selectionHit(selectionHits,SelectionTarget.from(row.entry)).semantics {
                                        traversalIndex=tile.index.toFloat()
                                        collectionItemInfo=CollectionItemInfo(tile.row,tile.rowSpan,tile.column,1)
                                    }) {
                                    val offset=tiles.visible().firstOrNull { it.key==row.entry.key }?.offset?.y
                                    model.open(row.entry,offset?.let { -it },TimelineViewport.opening(viewport(),offset?.let { TimelineAnchor(row.entry.cursor,-it) }))
                                }
                            }
                        }
                    }
                }
            } else {
            items(retained.size,key={ index -> retained[index].entry.key },
                span={ index -> GridItemSpan(if(layout==GalleryLayout.SQUARE) 1 else plan.cells[index].span) },contentType={ "media" }) { index ->
                // Signal Paging access, but render one immutable row/geometry snapshot together.
                // A concurrent prepend must never apply another identity's cell dimensions/actions.
                if(index<rows.itemCount) rows[index]
                val row=retained[index]
                TimelineThumbnail(row.entry,model,features,selection,onToggle,onBeginSelection,selectionLocked || restoring || row.query!=query,zone,row.aspect,
                    Modifier.selectionHit(selectionHits,SelectionTarget.from(row.entry)).height(requireNotNull(plan.cells.getOrNull(index)?.height).dp).semantics {
                        val cell=plan.cells[index];collectionItemInfo=CollectionItemInfo(cell.row,1,cell.column,1)
                    }) {
                    val offset=tiles.visible().firstOrNull { it.key==row.entry.key }?.offset?.y
                    model.open(row.entry,offset?.let { -it },TimelineViewport.opening(viewport(),offset?.let { TimelineAnchor(row.entry.cursor,-it) }))
                }
            }
            }
            if(rows.loadState.append is LoadState.Loading) item(span={ GridItemSpan(maxLineSpan) }) {
                LinearProgressIndicator(Modifier.fillMaxWidth().padding(8.dp))
            }
        }
        }
        if(restoring || refresh is LoadState.Loading) LinearProgressIndicator(Modifier.align(Alignment.TopCenter).fillMaxWidth().testTag("timeline:refreshing"))
        if(selection.isEmpty() && !selectionLocked && !restoring) TimelineDateRail(tiles,retained,query,zone,{ model.anchor(query) }) { dateNavigation=true }
        }
    }
}

private data class ThumbnailArtifact(val revision: String,val file: File?)

@Composable
private fun TimelineThumbnail(entry: TimelineEntry,model: TimelineViewModel,features: LibraryFeaturesModel,selection: List<SelectionTarget>,onToggle: (SelectionTarget)->Unit,onBeginSelection: (SelectionTarget)->Unit,selectionLocked: Boolean,zone: ZoneId,aspect: Float,modifier: Modifier=Modifier,onOpen: ()->Unit) {
    val context=LocalContext.current
    var problem by remember(entry) { mutableStateOf<String?>(null) }
    val revision=listOf(entry.key,entry.sha256,entry.version,entry.thumbnailRevision.toString(),entry.originalPath).joinToString("|") { it?.let { value -> "${value.length}:$value" } ?: "-" }
    val baseArtifact by produceState<ThumbnailArtifact?>(null,entry) {
        try {
            val media=model.repository.resolve(entry)
            value=ThumbnailArtifact(revision,media.photo?.let { features.app.cache.thumbnail(it) })
            media.cloud?.let { cloud ->
                val source=features.app.database.drive().source(cloud.bindingKey)
                com.myphotosanywhere.mpa.drive.ThumbnailWorker.request(context,cloud,source?.connection())
            }
        } catch(cancelled: CancellationException) { throw cancelled }
        catch(error: java.io.IOException) { problem=error.message ?: "Thumbnail unavailable" }
        catch(error: android.database.sqlite.SQLiteException) { problem=error.message ?: "Cannot read thumbnail availability" }
    }
    val thumbnail=baseArtifact?.takeIf { it.revision==revision }?.file
    var measured by remember(entry.key) { mutableStateOf(IntSize.Zero) }
    var rejectedVariant by remember(entry.key,entry.sha256,entry.version) { mutableStateOf<String?>(null) }
    val widthBucket=if(measured.width==0) 0 else ((measured.width.toLong()+63)/64*64).coerceAtMost(2048).toInt()
    val heightBucket=if(measured.height==0) 0 else ((measured.height.toLong()+63)/64*64).coerceAtMost(2048).toInt()
    val upgradeArtifact by produceState<ThumbnailArtifact?>(null,revision,widthBucket,heightBucket,aspect) {
        if(widthBucket>0 && heightBucket>0) try {
            value=ThumbnailArtifact(revision,features.app.thumbnailVariants.variant(entry,widthBucket,heightBucket,aspect))
        } catch(cancelled: CancellationException) { throw cancelled }
        catch(_: java.io.IOException) { value=null } // The cached small preview remains available.
        catch(_: android.database.sqlite.SQLiteException) { value=null }
    }
    val upgraded=upgradeArtifact?.takeIf { it.revision==revision }?.file
    val preferred=upgraded?.takeUnless { it.path==rejectedVariant }
    val preview=preferred ?: thumbnail ?: entry.thumbnailPath.takeIf { it.isNotBlank() }?.let(::File)
    val target=SelectionTarget.from(entry)
    val chosen=selection.any { it.matches(target) }
    val selecting=selection.isNotEmpty()
    val date=entry.millis?.let { Instant.ofEpochMilli(it).atZone(zone).toLocalDate().toString() } ?: "Unknown date"
    Box(modifier.background(MaterialTheme.colorScheme.surfaceVariant).onSizeChanged { measured=it }
        .testTag(entry.photoId?.let { "photo:$it" } ?: "cloud:${entry.cloudKey}")
        .combinedClickable(enabled=!selectionLocked,role=Role.Button,onClick={ if(selecting) onToggle(target) else onOpen() },
            onLongClickLabel="Select photo",onLongClick={ onBeginSelection(target) }).semantics(mergeDescendants=true) {
            if(selecting) selected=chosen
            customActions=listOf(CustomAccessibilityAction(if(chosen) "Deselect photo" else "Select photo") { if(!selectionLocked) onToggle(target);!selectionLocked })
            contentDescription="${if(entry.mimeType.startsWith("video/")) "Video" else "Photo"}, $date, ${entry.name}"
        }) {
        AsyncImage(coil.request.ImageRequest.Builder(context).data(preview)
            .memoryCacheKey("${entry.key}:${entry.thumbnailRevision}:${entry.originalPath}:${preview?.path}:$widthBucket:$heightBucket")
            .crossfade(if(android.animation.ValueAnimator.areAnimatorsEnabled()) 160 else 0).build(),null,
            modifier=Modifier.fillMaxSize(),contentScale=ContentScale.Crop,
            onSuccess={ problem=null },
            onError={ if(preferred!=null) rejectedVariant=preferred.path else problem="Thumbnail unavailable. Open photo to retry." })
        if(selecting) SelectionMark(chosen)
        problem?.let { message -> Box(Modifier.align(Alignment.TopEnd).padding(4.dp)) {
            com.myphotosanywhere.mpa.ui.ShellIcon(com.myphotosanywhere.mpa.R.drawable.ic_shell_warning,message)
        } }
        if(entry.mimeType.startsWith("video/")) Box(Modifier.align(Alignment.BottomEnd).padding(4.dp)) {
            com.myphotosanywhere.mpa.ui.ShellIcon(com.myphotosanywhere.mpa.R.drawable.ic_shell_video,"Video")
        }
    }
}

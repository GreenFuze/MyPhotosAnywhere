package com.myphotosanywhere.mpa.timeline

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.*
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.CancellationException
import java.time.Instant
import java.time.YearMonth
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.TextStyle
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.boundsInWindow
import androidx.compose.ui.layout.positionInWindow
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import com.myphotosanywhere.mpa.ui.MotionVisibility
import com.myphotosanywhere.mpa.ui.MotionEdge
import com.myphotosanywhere.mpa.ui.ShellIcon
import com.myphotosanywhere.mpa.R
import kotlin.math.abs
import kotlin.math.roundToInt
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle

@Composable
internal fun GalleryLayoutControls(selected: GalleryLayout,onSelect: (GalleryLayout)->Unit) {
    Column {
        Text("Photo layout",style=MaterialTheme.typography.titleMedium,modifier=Modifier.semantics { heading() })
        FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            GalleryLayout.entries.forEach { value ->
                FilterChip(selected==value,{ onSelect(value) },label={ Text(value.title) },
                    modifier=Modifier.heightIn(min=48.dp).testTag("layout:${value.name}"))
            }
        }
    }
}

@Composable
internal fun GridDensityControls(selected: GridDensity,onSelect: (GridDensity)->Unit) {
    Column {
        Text("Photo size",style=MaterialTheme.typography.titleMedium,modifier=Modifier.semantics { heading() })
        FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            GridDensity.entries.forEach { value ->
                FilterChip(selected==value,{ onSelect(value) },label={ Text("${value.title} · ${value.columns} columns") },
                    modifier=Modifier.heightIn(min=48.dp).testTag("density:${value.columns}"))
            }
        }
    }
}

@Composable
internal fun BoxScope.TimelineDateRail(state: TimelineGridViewport,items: List<TimelineRow>,query: TimelineQuery,zone: ZoneId,preferred: ()->TimelineAnchor?,
    model: TimelineViewModel,bottomClearance: Dp,visible: Boolean,onOpen: ()->Unit) {
    val media=remember(items,query) { items.filter { it.query==query }.associateBy { it.entry.key } }
    val currentPreferred by rememberUpdatedState(preferred)
    val indicator by remember(state,media,zone) { derivedStateOf {
        val layout=state.state.layoutInfo
        // A date seek can anchor a photo beside newer dates on the same top row.
        // Share the viewport's canonical identity policy; interpret the chosen timestamp
        // only afterwards so unknown dates never borrow a neighbor's known date.
        val visible=state.visible().mapNotNull { item ->
            media[item.key]?.takeIf {
                item.offset.y+item.size.height>layout.viewportStartOffset && item.offset.y<layout.viewportEndOffset
            }?.entry?.let { TimelineAnchor(it.cursor,-item.offset.y) }
        }
        TimelineViewport.choose(visible,currentPreferred())?.let { TimelineDateIndicator.from(it.cursor.millis,zone) }
    } }
    val scope=rememberCoroutineScope()
    val lifecycle=LocalLifecycleOwner.current
    val coordinator=remember(model,query,zone) { DateScrubCoordinator(scope,{ model.seekDate(query,it.atEndOfMonth(),zone) }) }
    val scrub by coordinator.state.collectAsState()
    var range by remember(model,query,zone) { mutableStateOf<MonthNavigation?>(null) }
    LaunchedEffect(model,query,zone,lifecycle) {
        lifecycle.lifecycle.repeatOnLifecycle(androidx.lifecycle.Lifecycle.State.STARTED) {
            try { model.repository.dates(query).collect { range=it?.months(zone) } }
            catch(cancelled: CancellationException) { throw cancelled }
            catch(_: android.database.sqlite.SQLiteException) { range=null } // Jump retains its explicit Retry dates surface.
            finally { coordinator.cancel() }
        }
    }
    DisposableEffect(coordinator) { onDispose { coordinator.cancel() } }
    LaunchedEffect(visible) { if(!visible) coordinator.cancel() }
    val preview=scrub.month?.takeIf { scrub.dragging || scrub.pending }
    MotionVisibility(visible,MotionEdge.TOP,Modifier.align(Alignment.TopCenter).padding(8.dp)) {
        TimelineFloatingDateChip(indicator,{ coordinator.cancel();onOpen() },preview=preview,seeking=scrub.pending)
    }
    MotionVisibility(visible && range!=null,MotionEdge.TOP,Modifier.align(Alignment.CenterEnd)
        .padding(top=72.dp,end=8.dp,bottom=bottomClearance+8.dp).width(48.dp).fillMaxHeight()) {
        range?.let { bounds -> DateScrubHandle(DateScrubScale(bounds),indicator?.date?.let(YearMonth::from),coordinator,visible) {
            coordinator.cancel();onOpen()
        } }
    }
}

@Composable
internal fun TimelineFloatingDateChip(indicator: TimelineDateIndicator?,onOpen: ()->Unit,modifier: Modifier=Modifier,
    preview: YearMonth?=null,seeking: Boolean=false) {
    val locale=LocalConfiguration.current.locales[0]
    val label=preview?.format(DateTimeFormatter.ofPattern("MMMM yyyy",locale)) ?: indicator?.label(locale) ?: "Jump to date"
    Surface(modifier,shape=androidx.compose.foundation.shape.RoundedCornerShape(50),
        color=MaterialTheme.colorScheme.surfaceContainerHigh,shadowElevation=2.dp) {
        TextButton(onClick=onOpen,modifier=Modifier.heightIn(min=48.dp).widthIn(min=48.dp,max=240.dp)
            .testTag("timeline:date-rail").semantics {
                contentDescription="Jump to month or year"
                stateDescription=if(preview!=null) "$label${if(seeking) ", seeking" else ", date preview"}" else indicator?.description(locale) ?: "No visible photo date"
            }) {
            Text(label,style=MaterialTheme.typography.labelLarge)
        }
    }
}

/** Only the visible 48dp handle owns input; the rest of the edge still opens photos. */
@Composable
private fun DateScrubHandle(scale: DateScrubScale,current: YearMonth?,coordinator: DateScrubCoordinator,enabled: Boolean,onOpen: ()->Unit) {
    val scrub by coordinator.state.collectAsState()
    val latestScale by rememberUpdatedState(scale)
    val locale=LocalConfiguration.current.locales[0]
    val height=with(LocalDensity.current) { 72.dp.toPx() }
    var track by remember { mutableStateOf(Rect.Zero) }
    var origin by remember { mutableStateOf(Offset.Zero) }
    val preview=scrub.month?.takeIf { scrub.dragging || scrub.pending }
    val month=preview ?: current ?: scale.months.first
    val fraction=scale.fraction(month)
    Box(Modifier.fillMaxSize().onGloballyPositioned { track=it.boundsInWindow() }.testTag("timeline:scrub-track")) {
        IconButton(onClick=onOpen,enabled=enabled,modifier=Modifier.width(48.dp).height(72.dp)
            .offset { IntOffset(0,((track.height-height).coerceAtLeast(0f)*fraction).roundToInt()) }
            .onGloballyPositioned { origin=it.positionInWindow() }
            .pointerInput(coordinator,enabled) {
                if(!enabled) return@pointerInput
                awaitEachGesture {
                    val down=awaitFirstDown(requireUnconsumed=false,pass=PointerEventPass.Initial)
                    val start=down.position+origin
                    val held=latestScale
                    var dragging=false;var released=false
                    try {
                        while(true) {
                            val event=awaitPointerEvent(PointerEventPass.Initial)
                            if(event.changes.count { it.pressed }>1) break
                            val pointer=event.changes.firstOrNull { it.id==down.id } ?: break
                            if(!pointer.pressed) {
                                if(dragging) { pointer.consume();coordinator.finish();released=true }
                                break
                            }
                            val position=pointer.position+origin
                            if(!dragging && abs(position.y-start.y)>viewConfiguration.touchSlop) dragging=true
                            if(dragging) {
                                val top=track.top+height/2;val bottom=maxOf(top,track.bottom-height/2)
                                coordinator.offer(held.month(held.fraction(position.y,top,bottom)))
                                event.changes.forEach { it.consume() }
                            }
                        }
                    } finally { if(dragging && !released) coordinator.cancel() }
                }
            }.testTag("timeline:date-scrubber").semantics {
                contentDescription="Browse photo dates. Drag for months, or activate Jump to date."
                stateDescription=if(current==null && preview==null) "Unknown date. Drag to dated photos." else month.format(DateTimeFormatter.ofPattern("MMMM yyyy",locale))
            },colors=IconButtonDefaults.iconButtonColors(containerColor=MaterialTheme.colorScheme.surfaceContainerHigh)) {
            ShellIcon(R.drawable.ic_date_scrub)
        }
    }
}

@Composable
internal fun TimelineDateNavigator(query: TimelineQuery,repository: TimelineRepository,zone: ZoneId,initialMillis: Long?,
    onJump: (LocalDate)->Unit,onClose: ()->Unit) {
    var attempt by remember { mutableIntStateOf(0) }
    var range by remember(query,zone) { mutableStateOf<MonthNavigation?>(null) }
    var loading by remember(query,zone) { mutableStateOf(true) }
    var problem by remember(query,zone) { mutableStateOf<String?>(null) }
    LaunchedEffect(query,zone,attempt) {
        loading=true;problem=null
        try { range=repository.dateRange(query)?.months(zone) }
        catch(cancelled: CancellationException) { throw cancelled }
        catch(error: android.database.sqlite.SQLiteException) { problem=error.message ?: "Cannot read photo dates. Retry." }
        finally { loading=false }
    }
    var selected by rememberSaveable(query.toString(),zone.id) { mutableStateOf(initialMillis?.let { YearMonth.from(Instant.ofEpochMilli(it).atZone(zone)).toString() }) }
    var yearValid by remember(query,zone) { mutableStateOf(true) }
    val bounds=range
    val month=bounds?.clamp(selected?.let(YearMonth::parse) ?: bounds.last)
    AlertDialog(onDismissRequest=onClose,title={ Text("Jump to month or year") },
        text={ Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).testTag("timeline:date-navigation"),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            when {
                loading -> { LinearProgressIndicator(Modifier.fillMaxWidth());Text("Reading available dates…") }
                problem!=null -> { Text(requireNotNull(problem));TextButton(onClick={ attempt++ }) { Text("Retry dates") } }
                bounds==null || month==null -> Text("No dated photos match this view. Unknown-date photos remain in the gallery.")
                else -> MonthNavigationControls(bounds,month,onValidityChanged={ yearValid=it },onSelect={ selected=it.toString() })
            }
        } },
        confirmButton={ TextButton(enabled=!loading && problem==null && month!=null && yearValid,onClick={ onJump(requireNotNull(bounds).jumpDate(requireNotNull(month))) },
            modifier=Modifier.heightIn(min=48.dp).testTag("timeline:month-jump")) { Text("Jump") } },
        dismissButton={ TextButton(onClick=onClose,modifier=Modifier.heightIn(min=48.dp)) { Text("Cancel") } })
}

@Composable
internal fun MonthNavigationControls(bounds: MonthNavigation,month: YearMonth,onValidityChanged: (Boolean)->Unit={},onSelect: (YearMonth)->Unit) {
    val locale=LocalConfiguration.current.locales[0]
    val focus=LocalFocusManager.current
    val keyboard=LocalSoftwareKeyboardController.current
    fun finishEntry() { focus.clearFocus(force=true);keyboard?.hide() }
    Text("${bounds.first} – ${bounds.last}",style=MaterialTheme.typography.bodySmall)
    Text(month.format(DateTimeFormatter.ofPattern("MMMM yyyy",locale)),Modifier.testTag("timeline:month-preview").semantics { heading() },style=MaterialTheme.typography.titleMedium)
    if(bounds.span>0) Slider(value=bounds.fraction(month),onValueChange={ onSelect(bounds.month(it)) },onValueChangeFinished={ finishEntry() },
        modifier=Modifier.fillMaxWidth().heightIn(min=48.dp).testTag("timeline:month-slider")
            .semantics { contentDescription="Browse months";stateDescription=month.format(DateTimeFormatter.ofPattern("MMMM yyyy",locale)) })
    var year by remember(month.year) { mutableStateOf(month.year.toString()) }
    val parsed=year.toIntOrNull()
    val valid=parsed!=null && parsed in bounds.first.year..bounds.last.year
    val validity by rememberUpdatedState(onValidityChanged)
    LaunchedEffect(valid) { validity(valid) }
    OutlinedTextField(year,{ value ->
        if(value.length<=10 && value.all { it.isDigit() || it=='-' }) {
            year=value
            value.toIntOrNull()?.takeIf { it in bounds.first.year..bounds.last.year }?.let { onSelect(bounds.clamp(YearMonth.of(it,month.monthValue))) }
        }
    },label={ Text("Year") },singleLine=true,isError=!valid,keyboardOptions=KeyboardOptions(keyboardType=KeyboardType.Number,imeAction=ImeAction.Done),keyboardActions=KeyboardActions(onDone={ finishEntry() }),
        supportingText={ if(!valid) Text("Choose a year from ${bounds.first.year} to ${bounds.last.year}") },modifier=Modifier.fillMaxWidth().testTag("timeline:year"))
    FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
        for(number in 1..12) {
            val candidate=YearMonth.of(month.year,number)
            FilterChip(selected=month.monthValue==number,onClick={ onSelect(candidate);finishEntry() },enabled=candidate in bounds.first..bounds.last,
                label={ Text(candidate.month.getDisplayName(TextStyle.SHORT,locale)) },
                modifier=Modifier.heightIn(min=48.dp).testTag("timeline:month:$number"))
        }
    }
    Text("Jump shows the newest matching photo in or before this month. Dates use this device’s timezone.",style=MaterialTheme.typography.bodySmall)
}

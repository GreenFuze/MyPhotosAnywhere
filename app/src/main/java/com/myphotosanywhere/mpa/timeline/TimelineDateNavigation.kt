package com.myphotosanywhere.mpa.timeline

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.LazyGridState
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
internal fun BoxScope.TimelineDateRail(state: LazyGridState,items: List<TimelineRow>,query: TimelineQuery,zone: ZoneId,onOpen: ()->Unit) {
    val indicator by remember(state,items,query,zone) { derivedStateOf {
        val layout=state.layoutInfo
        // Choose the first intersecting media row before interpreting its timestamp. Skipping
        // null timestamps would incorrectly label unknown-date photos with a later photo's date.
        layout.visibleItemsInfo.firstNotNullOfOrNull { visible ->
            (items.getOrNull(visible.index) as? TimelineRow.Media)?.takeIf {
                it.query==query && visible.offset.y+visible.size.height>layout.viewportStartOffset && visible.offset.y<layout.viewportEndOffset
            }
        }?.let { TimelineDateIndicator.from(it.entry.millis,zone) }
    } }
    TimelineFloatingDateChip(indicator,onOpen,Modifier.align(Alignment.TopCenter).padding(8.dp))
}

@Composable
internal fun TimelineFloatingDateChip(indicator: TimelineDateIndicator?,onOpen: ()->Unit,modifier: Modifier=Modifier) {
    val locale=LocalConfiguration.current.locales[0]
    Surface(modifier,shape=androidx.compose.foundation.shape.RoundedCornerShape(50),
        color=MaterialTheme.colorScheme.surfaceContainerHigh,shadowElevation=2.dp) {
        TextButton(onClick=onOpen,modifier=Modifier.heightIn(min=48.dp).widthIn(min=48.dp,max=240.dp)
            .testTag("timeline:date-rail").semantics {
                contentDescription="Jump to month or year"
                stateDescription=indicator?.description(locale) ?: "No visible photo date"
            }) {
            Text(indicator?.label(locale) ?: "Jump to date",style=MaterialTheme.typography.labelLarge)
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

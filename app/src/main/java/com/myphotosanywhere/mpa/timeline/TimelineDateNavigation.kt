package com.myphotosanywhere.mpa.timeline

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.LazyGridState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.text.KeyboardOptions
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
import java.util.Locale

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
internal fun BoxScope.TimelineDateRail(state: LazyGridState,items: List<TimelineRow>,zone: ZoneId,onOpen: ()->Unit) {
    val month by remember(state,items,zone) { derivedStateOf {
        state.layoutInfo.visibleItemsInfo.firstNotNullOfOrNull { visible ->
            (items.getOrNull(visible.index) as? TimelineRow.Media)?.entry?.millis
                ?.let { YearMonth.from(Instant.ofEpochMilli(it).atZone(zone)) }
        }
    } }
    // Logical end follows RTL. The rail occupies only its touch target and stays clear of bottom navigation.
    Surface(Modifier.align(Alignment.TopEnd).padding(top=8.dp,end=8.dp),shape=MaterialTheme.shapes.large,
        color=MaterialTheme.colorScheme.surfaceContainerHigh,shadowElevation=2.dp) {
        TextButton(onClick=onOpen,modifier=Modifier.heightIn(min=48.dp).widthIn(min=48.dp,max=160.dp)
            .testTag("timeline:date-rail").semantics { contentDescription="Jump to month or year" }) {
            com.myphotosanywhere.mpa.ui.ShellIcon(com.myphotosanywhere.mpa.R.drawable.ic_shell_date)
            Spacer(Modifier.width(4.dp))
            Text(month?.format(DateTimeFormatter.ofPattern("MMM yyyy")) ?: "Jump to date",style=MaterialTheme.typography.labelLarge)
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
    val bounds=range
    val month=bounds?.clamp(selected?.let(YearMonth::parse) ?: bounds.last)
    AlertDialog(onDismissRequest=onClose,title={ Text("Jump to month or year") },
        text={ Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).testTag("timeline:date-navigation"),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            when {
                loading -> { LinearProgressIndicator(Modifier.fillMaxWidth());Text("Reading available dates…") }
                problem!=null -> { Text(requireNotNull(problem));TextButton(onClick={ attempt++ }) { Text("Retry dates") } }
                bounds==null || month==null -> Text("No dated photos match this view. Unknown-date photos remain in the gallery.")
                else -> MonthNavigationControls(bounds,month) { selected=it.toString() }
            }
        } },
        confirmButton={ TextButton(enabled=!loading && problem==null && month!=null,onClick={ onJump(requireNotNull(bounds).jumpDate(requireNotNull(month))) },
            modifier=Modifier.heightIn(min=48.dp).testTag("timeline:month-jump")) { Text("Jump") } },
        dismissButton={ TextButton(onClick=onClose,modifier=Modifier.heightIn(min=48.dp)) { Text("Cancel") } })
}

@Composable
internal fun MonthNavigationControls(bounds: MonthNavigation,month: YearMonth,onSelect: (YearMonth)->Unit) {
    Text("${bounds.first} – ${bounds.last}",style=MaterialTheme.typography.bodySmall)
    Text(month.format(DateTimeFormatter.ofPattern("MMMM yyyy")),Modifier.testTag("timeline:month-preview").semantics { liveRegion=LiveRegionMode.Polite },style=MaterialTheme.typography.titleMedium)
    if(bounds.span>0) Slider(value=bounds.fraction(month),onValueChange={ onSelect(bounds.month(it)) },
        modifier=Modifier.fillMaxWidth().heightIn(min=48.dp).testTag("timeline:month-slider")
            .semantics { contentDescription="Browse months";stateDescription=month.format(DateTimeFormatter.ofPattern("MMMM yyyy")) })
    var year by remember(month.year) { mutableStateOf(month.year.toString()) }
    val parsed=year.toIntOrNull()
    val valid=parsed!=null && parsed in bounds.first.year..bounds.last.year
    OutlinedTextField(year,{ value ->
        if(value.length<=6 && value.all { it.isDigit() || it=='-' }) {
            year=value
            value.toIntOrNull()?.takeIf { it in bounds.first.year..bounds.last.year }?.let { onSelect(bounds.clamp(YearMonth.of(it,month.monthValue))) }
        }
    },label={ Text("Year") },singleLine=true,isError=!valid,keyboardOptions=KeyboardOptions(keyboardType=KeyboardType.Number),
        supportingText={ if(!valid) Text("Choose a year from ${bounds.first.year} to ${bounds.last.year}") },modifier=Modifier.fillMaxWidth().testTag("timeline:year"))
    FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
        for(number in 1..12) {
            val candidate=YearMonth.of(month.year,number)
            FilterChip(selected=month.monthValue==number,onClick={ onSelect(candidate) },enabled=candidate in bounds.first..bounds.last,
                label={ Text(candidate.month.getDisplayName(TextStyle.SHORT,Locale.getDefault())) },
                modifier=Modifier.heightIn(min=48.dp).testTag("timeline:month:$number"))
        }
    }
    Text("Jump shows the newest matching photo in or before this month. Dates use this device’s timezone.",style=MaterialTheme.typography.bodySmall)
}

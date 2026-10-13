package com.myphotosanywhere.mpa.search

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.myphotosanywhere.mpa.drive.DriveSource
import com.myphotosanywhere.mpa.timeline.TimelineMediaType
import java.time.Instant
import java.time.ZoneOffset

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun SearchFilters(initial: SearchCriteria,sources: List<DriveSource>,onApply: (SearchCriteria)->Unit,onClose: ()->Unit) {
    var criteria by remember { mutableStateOf(initial) }
    var dates by remember { mutableStateOf(false) }
    ModalBottomSheet(onDismissRequest=onClose) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(16.dp).testTag("search:filters"),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            Text("Filter search",Modifier.semantics { heading() },style=MaterialTheme.typography.titleLarge)
            Text("Media type",Modifier.semantics { heading() })
            FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) { TimelineMediaType.entries.forEach { type ->
                FilterChip(selected=criteria.type==type,onClick={ criteria=criteria.copy(type=type) },
                    modifier=Modifier.heightIn(min=48.dp).testTag("search:type:${type.name}"),label={ Text(when(type) { TimelineMediaType.ALL -> "All media";TimelineMediaType.PHOTOS -> "Photos";TimelineMediaType.VIDEOS -> "Videos" }) })
            } }
            Text("Organization",Modifier.semantics { heading() })
            FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) { listOf("All","Favorites","Offline","Edits","Archive").forEach { filter ->
                FilterChip(selected=criteria.filter==filter,onClick={ criteria=criteria.copy(filter=filter) },modifier=Modifier.heightIn(min=48.dp).testTag("search:organization:$filter"),
                    label={ Text(when(filter) { "Offline" -> "Offline originals";"Edits" -> "Edited photos";else -> filter }) })
            } }
            Text("Source",Modifier.semantics { heading() })
            FilterChip(selected=criteria.source==null,onClick={ criteria=criteria.copy(source=null) },modifier=Modifier.heightIn(min=48.dp).testTag("search:source:all"),label={ Text("All sources") })
            sources.forEach { source -> FilterChip(selected=criteria.source==source.key,onClick={ criteria=criteria.copy(source=source.key) },
                modifier=Modifier.heightIn(min=48.dp).testTag("search:source:${source.key}"),label={ Text("${source.folderName} · ${source.email}") }) }
            Text("Date range",Modifier.semantics { heading() })
            TextButton(onClick={ dates=true },modifier=Modifier.heightIn(min=48.dp).testTag("search:dates")) {
                Text(criteria.dates?.let { "${it.first} – ${it.last}" } ?: "Any date")
            }
            if(criteria.dates!=null) TextButton(onClick={ criteria=criteria.copy(dates=null) },modifier=Modifier.heightIn(min=48.dp)) { Text("Clear dates") }
            FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                Button(onClick={ onApply(criteria) },modifier=Modifier.heightIn(min=48.dp).testTag("search:apply")) { Text("Apply") }
                TextButton(onClick={ criteria=criteria.copy(type=TimelineMediaType.ALL,filter="All",source=null,dates=null) },modifier=Modifier.heightIn(min=48.dp).testTag("search:reset")) { Text("Clear filters") }
                TextButton(onClick=onClose,modifier=Modifier.heightIn(min=48.dp)) { Text("Cancel") }
            }
        }
    }
    if(dates) {
        val state=rememberDateRangePickerState(
            initialSelectedStartDateMillis=criteria.dates?.first?.atStartOfDay(ZoneOffset.UTC)?.toInstant()?.toEpochMilli(),
            initialSelectedEndDateMillis=criteria.dates?.last?.atStartOfDay(ZoneOffset.UTC)?.toInstant()?.toEpochMilli())
        DatePickerDialog(onDismissRequest={ dates=false },confirmButton={
            TextButton(enabled=state.selectedStartDateMillis!=null && state.selectedEndDateMillis!=null,onClick={
                val first=Instant.ofEpochMilli(requireNotNull(state.selectedStartDateMillis)).atZone(ZoneOffset.UTC).toLocalDate()
                val last=Instant.ofEpochMilli(requireNotNull(state.selectedEndDateMillis)).atZone(ZoneOffset.UTC).toLocalDate()
                criteria=criteria.copy(dates=SearchDates(first,last));dates=false
            }) { Text("Use dates") }
        },dismissButton={ TextButton(onClick={ dates=false }) { Text("Cancel") } }) {
            DateRangePicker(state,Modifier.heightIn(max=500.dp),showModeToggle=true)
        }
    }
}

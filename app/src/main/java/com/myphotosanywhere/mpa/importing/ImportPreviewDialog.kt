package com.myphotosanywhere.mpa.importing

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

@Composable
internal fun ImportPreviewDialog(job: ImportJob?,entries: List<ImportItem>,busy: Boolean,
    onSelect: (String,Boolean)->Unit,onClose: ()->Unit) {
    val takeout=job?.kind=="TAKEOUT"
    var filter by rememberSaveable(job?.id) { mutableStateOf(TakeoutPreviewFilter.ALL) }
    val inputs=remember(entries) { TakeoutPreviewSnapshot.inputs(entries) }
    val metadata by produceState<TakeoutPreviewMetadata?>(null,takeout,inputs) {
        value=null
        if(takeout) value=withContext(Dispatchers.Default) {
            val context=currentCoroutineContext()
            TakeoutPreviewAnalyzer().analyze(inputs) { context.ensureActive() }
        }
    }
    val snapshot=remember(entries,metadata) {
        metadata?.takeIf { it.inputs==inputs }?.let { TakeoutPreviewSnapshot(entries,it) }
    }
    val visible=if(takeout && filter!=TakeoutPreviewFilter.ALL) snapshot?.visible(filter).orEmpty() else entries
    AlertDialog(onDismissRequest=onClose,title={ Text("Import preview") },text={
        LazyColumn(Modifier.heightIn(max=480.dp).testTag("import-preview-list"),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            item {
                Column(verticalArrangement=Arrangement.spacedBy(8.dp)) {
                    Text("${entries.count { it.selected }} selected · %.1f MiB known media size".format(entries.filter { it.selected }.sumOf { it.size }/(1024.0*1024)))
                    Text("Picker sizes are unknown until downloaded. Exact duplicates are checked against verified bytes in this destination before upload.",style=MaterialTheme.typography.bodySmall)
                    if(takeout) {
                        if(snapshot==null) {
                            Text("Reading Takeout metadata…",Modifier.testTag("takeout-preview-loading"))
                            LinearProgressIndicator(Modifier.fillMaxWidth())
                        } else TakeoutPreviewOverview(snapshot)
                        FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                            TakeoutPreviewFilter.entries.forEach { option ->
                                FilterChip(selected=filter==option,onClick={ filter=option },enabled=snapshot!=null,
                                    label={ Text(option.title) },modifier=Modifier.heightIn(min=48.dp).testTag("takeout-filter:${option.name}"))
                            }
                        }
                        Text("Filters change this preview only. Your import selection stays unchanged.",style=MaterialTheme.typography.bodySmall)
                    }
                }
            }
            if(visible.isEmpty() && (!takeout || snapshot!=null)) item { Text("No items match this preview filter.",Modifier.testTag("takeout-preview-empty")) }
            items(visible,key={ it.id }) { item ->
                Column(Modifier.testTag("import-preview-item:${item.id}"),verticalArrangement=Arrangement.spacedBy(4.dp)) {
                    Row {
                        Checkbox(item.selected,{ selected -> onSelect(item.id,selected) },
                            enabled=!busy && job?.state in listOf("PREVIEW","PAUSED","FAILED") && item.state !in listOf("COMPLETE","UNSUPPORTED","MATCHED","UNRESOLVED","INFORMATION"),
                            modifier=Modifier.testTag("import-preview-select:${item.id}").semantics { contentDescription="Include ${item.name}" })
                        Column(Modifier.weight(1f)) {
                            Text(item.path.take(250))
                            Text("${item.state.lowercase()} · ${if(item.metadata!=null) "metadata present" else "no supplemental metadata"}",style=MaterialTheme.typography.bodySmall)
                        }
                    }
                    item.error?.let { Text(it,color=MaterialTheme.colorScheme.error) }
                    if(item.id in snapshot?.metadata?.invalidIds.orEmpty()) Text("Metadata could not be read. Source retained; review this item.",color=MaterialTheme.colorScheme.error)
                    snapshot?.metadata?.records?.get(item.id)?.let { record ->
                        if(record.description.isNotBlank()) Text(record.description.take(500))
                        Text("Incoming date: ${record.captured?.let { java.util.Date(it) } ?: "absent"}; GPS: ${record.latitude}, ${record.longitude}")
                        val names=record.people.map(String::trim).filter(String::isNotBlank).distinct()
                        if(names.isNotEmpty()) Text("Exported people labels: ${names.joinToString()}")
                    }
                }
            }
        }
    },confirmButton={ TextButton(onClick=onClose) { Text("Close") } })
}

@Composable
private fun TakeoutPreviewOverview(snapshot: TakeoutPreviewSnapshot) {
    Column(Modifier.testTag("takeout-metadata-summary"),verticalArrangement=Arrangement.spacedBy(4.dp)) {
        Text("Takeout metadata",style=MaterialTheme.typography.titleSmall,modifier=Modifier.semantics { heading() })
        Text("${snapshot.withMetadata} media items with metadata · ${snapshot.withoutMetadata} without metadata")
        Text("${snapshot.withPeople} media items with people labels · ${snapshot.differentNames} different names")
        Text("Selected media: ${snapshot.selectedMedia} · with people labels: ${snapshot.selectedWithPeople}",Modifier.testTag("takeout-selected-summary"))
        Text("${snapshot.reviewCount} items need review · ${snapshot.invalidMetadata} unreadable media records")
        Text("After import, exported names appear in Collections > People > Imported people. They are photo labels, not confirmed face assignments. No face recognition is started.",style=MaterialTheme.typography.bodySmall)
    }
}

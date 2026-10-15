package com.myphotosanywhere.mpa.trash

import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.*
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.paging.LoadState
import androidx.paging.compose.collectAsLazyPagingItems
import coil.compose.AsyncImage
import com.myphotosanywhere.mpa.R
import com.myphotosanywhere.mpa.ui.ShellIcon
import java.io.File

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun TrashScreen(model: TrashViewModel,bottomClearance: Dp,onSources: ()->Unit) {
    val rows=model.pages.collectAsLazyPagingItems()
    val busy by model.busy.collectAsStateWithLifecycle()
    val sources by model.sources.collectAsStateWithLifecycle()
    var preview by remember { mutableStateOf<DriveTrashRecord?>(null) }
    val selected by model.selected.collectAsStateWithLifecycle()
    Column(Modifier.fillMaxSize().testTag("trash:screen")) {
        Text("Google Drive Trash",Modifier.padding(horizontal=16.dp).semantics { heading() },style=MaterialTheme.typography.titleLarge)
        Text("Photos tracked by MPA use Google Drive Trash and are normally deleted by Google after 30 days. Local-only originals are preserved.",Modifier.padding(16.dp),style=MaterialTheme.typography.bodyMedium)
        Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceEvenly) {
            TextButton(onClick={ model.refresh();rows.refresh() },enabled=!busy,modifier=Modifier.heightIn(min=48.dp).testTag("trash:refresh")) { ShellIcon(R.drawable.ic_shell_activity);Text("Refresh") }
            TextButton(onClick=onSources,modifier=Modifier.heightIn(min=48.dp)) { Text("Sources") }
            if(selected.isNotEmpty()) {
                TextButton(onClick={ model.restore(selected) },enabled=!busy,modifier=Modifier.heightIn(min=48.dp).testTag("trash:restore-selected")) { Text("Restore ${selected.size}") }
                TextButton(onClick=model::clearSelection,modifier=Modifier.heightIn(min=48.dp)) { Text("Clear") }
            }
        }
        if(busy || rows.loadState.refresh is LoadState.Loading) LinearProgressIndicator(Modifier.fillMaxWidth())
        if(rows.loadState.refresh is LoadState.Error || rows.loadState.append is LoadState.Error)
            TextButton(onClick=rows::retry,modifier=Modifier.heightIn(min=48.dp)) { Text("Trash could not update. Cached items remain. Retry") }
        if(rows.itemCount==0 && rows.loadState.refresh is LoadState.NotLoading) Box(Modifier.weight(1f).fillMaxWidth(),contentAlignment=Alignment.Center) {
            Text("No indexed photos in Drive Trash",Modifier.testTag("trash:empty"))
        } else LazyVerticalGrid(GridCells.Fixed(3),Modifier.weight(1f).testTag("trash:grid"),
            contentPadding=PaddingValues(start=2.dp,end=2.dp,bottom=bottomClearance+8.dp),
            horizontalArrangement=Arrangement.spacedBy(2.dp),verticalArrangement=Arrangement.spacedBy(2.dp)) {
            items(rows.itemCount,key={ index -> rows.peek(index)?.key ?: "loading:$index" }) { index -> rows[index]?.let { row ->
                val checked=selected.any { it.key==row.key }
                Column {
                    Box(Modifier.fillMaxWidth().aspectRatio(1f).combinedClickable(
                        onClick={ if(selected.isEmpty()) preview=row else model.toggle(row) },
                        onLongClick={ model.beginSelection(row) }).testTag("trash:item:${row.key}").semantics { contentDescription="${row.name}, ${if(row.desired!=null) "waiting for Drive" else if(row.confirmed) "in Drive Trash" else "needs attention"}";this.selected=checked }) {
                        AsyncImage(coil.request.ImageRequest.Builder(androidx.compose.ui.platform.LocalContext.current).data(File(row.thumbnailPath)).size(480).build(),null,Modifier.fillMaxSize(),contentScale=ContentScale.Crop)
                        if(checked) com.myphotosanywhere.mpa.selection.SelectionMark(true)
                    }
                    if(row.desired!=null) Text(if(row.desired) "Moving to Trash…" else "Restoring…",style=MaterialTheme.typography.labelSmall)
                    else if(row.error!=null) Text("Needs attention",style=MaterialTheme.typography.labelSmall,color=MaterialTheme.colorScheme.error)
                }
            } }
        }
    }
    preview?.let { row -> ModalBottomSheet(onDismissRequest={ preview=null }) {
        Column(Modifier.fillMaxWidth().heightIn(max=with(androidx.compose.ui.platform.LocalDensity.current) { androidx.compose.ui.platform.LocalWindowInfo.current.containerSize.height.toDp()*.75f }).verticalScroll(rememberScrollState()).padding(20.dp).navigationBarsPadding(),verticalArrangement=Arrangement.spacedBy(12.dp)) {
            Text(row.name,style=MaterialTheme.typography.titleLarge)
            if(row.thumbnailPath.isNotBlank()) AsyncImage(coil.request.ImageRequest.Builder(androidx.compose.ui.platform.LocalContext.current).data(File(row.thumbnailPath)).size(640).build(),
                "Cached preview of ${row.name}",Modifier.fillMaxWidth().height(220.dp),contentScale=ContentScale.Fit)
            else Text("Preview unavailable. The Drive file identity is retained.")
            Text(sources.find { it.key==row.bindingKey }?.let { "Source: ${it.email} · ${it.folderName}" }
                ?: "Source disconnected. Reconnect its original account and folder.",modifier=Modifier.testTag("trash:source"))
            Text(if(row.confirmed) "Original in Google Drive Trash" else "Drive state needs verification")
            row.error?.let { Text(it,color=MaterialTheme.colorScheme.error) }
            Text("Local photo information and relationships are preserved. Restore checks the original account and file before changing Drive.")
            if(row.desired!=null) TextButton(onClick={ model.cancel(row.key);preview=null },modifier=Modifier.heightIn(min=48.dp).testTag("trash:cancel")) { Text("Stop pending operation") }
            Row { TextButton(onClick={ model.restore(listOf(row));preview=null },enabled=row.confirmed && row.desired==null,modifier=Modifier.heightIn(min=48.dp).testTag("trash:restore")) { Text("Restore") }
                TextButton(onClick={ preview=null },modifier=Modifier.heightIn(min=48.dp)) { Text("Close") } }
        }
    } }
}

@Composable
internal fun TrashDialogs(model: TrashViewModel,onQueued: ()->Unit) {
    val confirmation by model.confirmation.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val message by model.message.collectAsStateWithLifecycle()
    confirmation?.let { state -> AlertDialog(onDismissRequest=model::dismiss,
        title={ Text(if(state.candidates.isEmpty()) "Photos preserved" else if(state.restore) "Restore from Drive Trash?" else "Move to Google Drive Trash?") },
        text={ androidx.compose.foundation.lazy.LazyColumn(Modifier.heightIn(max=400.dp).testTag("trash:confirmation")) {
            if(message!=null) item { Text(requireNotNull(message),color=MaterialTheme.colorScheme.error,modifier=Modifier.semantics { liveRegion=LiveRegionMode.Polite }) }
            item { Text(if(state.candidates.isEmpty()) "No photos will be changed." else if(state.restore) "Restore ${state.candidates.size} photos to their existing Drive locations." else "Move ${state.candidates.size} photos to Drive Trash. Google normally deletes them after 30 days. Other Drive copies and local original bytes are preserved.") }
            state.candidates.forEach { candidate -> item(key=candidate.record.key) { Column(Modifier.padding(top=12.dp)) {
                Text(candidate.record.name,style=MaterialTheme.typography.titleSmall);Text("${candidate.account} · ${candidate.folder}",style=MaterialTheme.typography.bodySmall)
            } } }
            state.failures.forEachIndexed { index,failure -> item(key="failure:$index") { Text(failure,Modifier.padding(top=12.dp),color=MaterialTheme.colorScheme.error) } }
        } },confirmButton={ TextButton(onClick={ model.confirm(onQueued) },enabled=!busy && state.candidates.isNotEmpty(),modifier=Modifier.heightIn(min=48.dp).testTag("trash:confirm")) { Text(if(state.restore) "Restore" else "Move to Trash") } },
        dismissButton={ TextButton(onClick=model::dismiss,enabled=!busy,modifier=Modifier.heightIn(min=48.dp)) { Text("Cancel") } }) }
    if(message!=null && confirmation==null) AlertDialog(onDismissRequest=model::clearMessage,title={ Text("Drive Trash") },text={ Text(requireNotNull(message)) },
        confirmButton={ TextButton(onClick=model::clearMessage,modifier=Modifier.heightIn(min=48.dp)) { Text("Done") } })
}

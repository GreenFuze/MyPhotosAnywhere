package com.myphotosanywhere.mpa.selection

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.*
import androidx.compose.ui.unit.dp
import com.myphotosanywhere.mpa.data.Album
import com.myphotosanywhere.mpa.ui.ShellIcon
import com.myphotosanywhere.mpa.ui.IconLabelAction
import com.myphotosanywhere.mpa.R

@Composable
internal fun SelectionMark(selected: Boolean) {
    if(selected) Box(Modifier.fillMaxSize().border(3.dp,MaterialTheme.colorScheme.primary))
    Box(Modifier.padding(6.dp).size(26.dp).background(if(selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surface.copy(alpha=.75f),CircleShape)
        .border(2.dp,MaterialTheme.colorScheme.onSurface,CircleShape).clearAndSetSemantics {},contentAlignment=Alignment.Center) {
        if(selected) CompositionLocalProvider(LocalContentColor provides MaterialTheme.colorScheme.onPrimary) { ShellIcon(R.drawable.ic_selection_check) }
    }
}

@Composable
internal fun SelectionTopBar(count: Int,busy: Boolean,onClose: ()->Unit) {
    Row(Modifier.fillMaxWidth().statusBarsPadding().heightIn(min=56.dp).padding(horizontal=8.dp).testTag("selection:bar"),verticalAlignment=Alignment.CenterVertically) {
        IconButton(onClick=onClose,enabled=!busy,modifier=Modifier.size(48.dp).testTag("selection:close")) { ShellIcon(R.drawable.ic_shell_back,"Exit selection") }
        Text("$count selected",Modifier.weight(1f).semantics { heading();liveRegion=LiveRegionMode.Polite },style=MaterialTheme.typography.titleLarge)
    }
}

@Composable
internal fun SelectionBottomBar(busy: Boolean,archived: Boolean,inAlbum: Boolean,onAction: (BulkKind)->Unit,modifier: Modifier=Modifier) {
    Surface(modifier.fillMaxWidth(),color=MaterialTheme.colorScheme.surfaceContainer,shadowElevation=3.dp) {
        FlowRow(Modifier.padding(horizontal=8.dp,vertical=4.dp).navigationBarsPadding(),horizontalArrangement=Arrangement.SpaceEvenly) {
            listOf(
                Triple(BulkKind.SHARE,"Share",R.drawable.ic_action_share),
                Triple(BulkKind.ALBUM,"Album",R.drawable.ic_shell_collections),
                Triple(BulkKind.FAVORITE,"Favorite",R.drawable.ic_shell_favorite),
                if(archived) Triple(BulkKind.RESTORE,"Restore",R.drawable.ic_action_restore) else Triple(BulkKind.ARCHIVE,"Archive",R.drawable.ic_shell_archive),
                Triple(BulkKind.OFFLINE,"Offline",R.drawable.ic_shell_offline),
                Triple(BulkKind.UNFAVORITE,"Unfavorite",R.drawable.ic_action_unfavorite)
            ).forEach { (kind,label,icon) ->
                IconLabelAction(icon,label,{ onAction(kind) },Modifier.testTag("selection:${kind.name}"),enabled=!busy,
                    description=when(kind) { BulkKind.ALBUM -> "Add to album";BulkKind.OFFLINE -> "Make available offline";else -> label })
            }
            if(inAlbum) IconLabelAction(R.drawable.ic_action_remove_album,"Remove from album",{ onAction(BulkKind.REMOVE_ALBUM) },
                Modifier.testTag("selection:REMOVE_ALBUM"),enabled=!busy)
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun SelectionDialogs(model: SelectionViewModel,albums: List<Album>,showAlbums: Boolean,onAlbumsClosed: ()->Unit,
    result: SelectionResult?,busy: Boolean,progress: Int,count: Int) {
    if(showAlbums) ModalBottomSheet(onDismissRequest=onAlbumsClosed) {
        LazyColumn(Modifier.fillMaxWidth().heightIn(max=400.dp).padding(16.dp)) {
            item { Text("Add to album",Modifier.semantics { heading() },style=MaterialTheme.typography.titleLarge) }
            if(albums.isEmpty()) item { Text("Create an album in Collections, then select photos to add.",Modifier.padding(vertical=16.dp)) }
            items(albums,key={ it.id }) { album -> TextButton(onClick={ onAlbumsClosed();model.execute(BulkAction(BulkKind.ALBUM,album.id)) },
                modifier=Modifier.fillMaxWidth().heightIn(min=48.dp).testTag("selection:album:${album.id}")) { Text(album.name) } }
            item { TextButton(onClick=onAlbumsClosed,modifier=Modifier.heightIn(min=48.dp)) { Text("Cancel") } }
        }
    }
    if(busy) AlertDialog(onDismissRequest={},title={ Text("Working with selected photos") },
        text={ Column { LinearProgressIndicator(Modifier.fillMaxWidth());Text("$progress of $count processed") } },
        confirmButton={ TextButton(onClick=model::cancel,modifier=Modifier.heightIn(min=48.dp)) { Text("Cancel operation") } })
    if(result!=null && !busy) AlertDialog(onDismissRequest=model::dismissResult,title={ Text(if(result.cancelled) "Operation cancelled" else "Selection result") },
        text={ LazyColumn(Modifier.heightIn(max=360.dp).testTag("selection:result")) {
            item { Text("${result.completed} ${if(result.queued) "kept offline or queued for download" else "completed"}. ${result.failures.size} not completed. Originals are preserved.") }
            items(result.failures,key={ it.target.key }) { failure -> Column(Modifier.padding(top=12.dp)) {
                Text(failure.target.name,style=MaterialTheme.typography.titleSmall);Text(failure.reason)
            } }
        } },confirmButton={ TextButton(onClick=model::dismissResult,modifier=Modifier.heightIn(min=48.dp).testTag("selection:result-close")) { Text("Done") } })
}

package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.grid.*
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.*
import androidx.compose.ui.Alignment
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.text.style.TextOverflow
import coil.compose.AsyncImage
import com.myphotosanywhere.mpa.timeline.TimelineQuery
import kotlinx.coroutines.CancellationException
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.Dp
import com.myphotosanywhere.mpa.data.*

@Composable
fun AlbumsScreen(model: LibraryFeaturesModel,albums: List<Album>,state: LazyGridState,
    onAlbum: (String)->Unit,onFavorites: ()->Unit,onArchive: ()->Unit,onActivity: ()->Unit,
    onPeople: ()->Unit,onPlaces: ()->Unit,onOffline: ()->Unit,onEdits: ()->Unit,bottomClearance: Dp=0.dp) {
    var create by remember { mutableStateOf(false) }
    var name by remember { mutableStateOf("") }
    var manage by remember { mutableStateOf<Album?>(null) }
    var delete by remember { mutableStateOf(false) }
    val repository=remember(model) { CollectionsRepository(model.app.database,model.app.timeline) }
    LazyVerticalGrid(GridCells.Adaptive(148.dp),Modifier.fillMaxSize().testTag("collections:grid"),state=state,
        contentPadding=PaddingValues(start=16.dp,top=12.dp,end=16.dp,bottom=16.dp+bottomClearance),
        verticalArrangement=Arrangement.spacedBy(16.dp),horizontalArrangement=Arrangement.spacedBy(12.dp)) {
        item { CollectionCard("Favorites",com.myphotosanywhere.mpa.R.drawable.ic_shell_favorite,repository,TimelineQuery("Favorites"),onFavorites) }
        item { CollectionCard("Archive",com.myphotosanywhere.mpa.R.drawable.ic_shell_archive,repository,TimelineQuery("Archive"),onArchive) }
        item { CollectionCard("People",com.myphotosanywhere.mpa.R.drawable.ic_shell_people,repository,null,onPeople) }
        item { CollectionCard("Places",com.myphotosanywhere.mpa.R.drawable.ic_shell_places,repository,null,onPlaces) }
        item { CollectionCard("Offline available",com.myphotosanywhere.mpa.R.drawable.ic_shell_offline,repository,TimelineQuery("Offline"),onOffline) }
        item { CollectionCard("Edited photos",com.myphotosanywhere.mpa.R.drawable.ic_shell_edit,repository,TimelineQuery("Edits"),onEdits) }
        item(span={ GridItemSpan(maxLineSpan) }) { Row(verticalAlignment=Alignment.CenterVertically) {
            Text("Albums",Modifier.weight(1f).semantics { heading() },style=MaterialTheme.typography.titleLarge)
            TextButton(onClick={ create=true;name="" },modifier=Modifier.heightIn(min=48.dp).testTag("collections:new-album")) { Text("New album") }
        } }
        if(albums.isEmpty()) item(span={ GridItemSpan(maxLineSpan) }) { Text("Create an album, then long-press photos to add them.") }
        items(albums,key={ it.id }) { album -> Column {
            CollectionCard(album.name,com.myphotosanywhere.mpa.R.drawable.ic_shell_collections,repository,TimelineQuery(album=album.id),{ onAlbum(album.id) },"album:${album.id}")
            FlowRow {
                TextButton(onClick={ model.pinAlbum(album,!album.pinned) },modifier=Modifier.heightIn(min=48.dp)) { Text(if(album.pinned) "Unpin offline album" else "Keep offline") }
                TextButton(onClick={ manage=album;name=album.name;delete=false },modifier=Modifier.heightIn(min=48.dp).testTag("album:manage:${album.id}")) { Text("Manage") }
            }
        } }
        item(span={ GridItemSpan(maxLineSpan) }) { OutlinedButton(onClick=onActivity,modifier=Modifier.heightIn(min=48.dp)) { Text("AI activity") } }
    }
    if(create || manage!=null) AlertDialog(onDismissRequest={ create=false; manage=null },title={ Text(if(create) "New album" else "Manage album") },
        text={ Column {
            OutlinedTextField(name,{ if(it.length<=100) name=it },label={ Text("Album name") })
            manage?.let { album ->
                if(delete) TextButton(onClick={ model.deleteAlbum(album); manage=null }) { Text("Confirm removal; keep photos") }
                else TextButton(onClick={ delete=true }) { Text("Remove album") }
            }
        } },confirmButton={ Button(enabled=name.isNotBlank(),onClick={
            val album=manage; if(album==null) model.createAlbum(name) else model.renameAlbum(album,name)
            create=false; manage=null
        }) { Text("Save") } },dismissButton={ TextButton(onClick={ create=false; manage=null }) { Text("Cancel") } })
}


@Composable
internal fun CollectionCard(title: String,icon: Int,repository: CollectionsRepository,query: TimelineQuery?,onClick: ()->Unit,tag: String="collections:$title") {
    var failure by remember(query) { mutableStateOf(false) }
    var retry by remember(query) { mutableIntStateOf(0) }
    val preview by produceState<CollectionPreview?>(null,repository,query,retry) {
        failure=false
        try { (query?.let { repository.preview(it) } ?: repository.special(title)).collect { value=it } }
        catch(cancelled: CancellationException) { throw cancelled }
        catch(_: android.database.sqlite.SQLiteException) { failure=true }
        catch(_: java.io.IOException) { failure=true }
    }
    Column {
        Card(Modifier.fillMaxWidth().testTag(tag).clickable(role=Role.Button,onClick=onClick),shape=RoundedCornerShape(24.dp)) {
            Box(Modifier.fillMaxWidth().aspectRatio(1f).background(MaterialTheme.colorScheme.surfaceContainerHigh),contentAlignment=Alignment.Center) {
                val covers=preview?.paths.orEmpty()
                com.myphotosanywhere.mpa.ui.ShellIcon(icon)
                if(covers.isNotEmpty()) Column(Modifier.fillMaxSize()) {
                    covers.chunked(2).forEach { pair -> Row(Modifier.weight(1f)) { pair.forEach { path ->
                        AsyncImage(path.takeIf { it.isNotBlank() },null,Modifier.weight(1f).fillMaxHeight(),contentScale=ContentScale.Crop)
                    } } }
                }
            }
            Text(title,Modifier.padding(horizontal=12.dp,vertical=8.dp),style=MaterialTheme.typography.titleMedium,maxLines=2,overflow=TextOverflow.Ellipsis)
            preview?.count?.takeIf { it>=0 }?.let { Text("$it items",Modifier.padding(start=12.dp,end=12.dp,bottom=12.dp),style=MaterialTheme.typography.bodySmall) }
        }
        if(failure) TextButton(onClick={ retry++ },modifier=Modifier.heightIn(min=48.dp)) { Text("Retry cover") }
    }
}

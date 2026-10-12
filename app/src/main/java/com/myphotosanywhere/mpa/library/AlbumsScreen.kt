package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.Dp
import com.myphotosanywhere.mpa.data.*

@Composable
fun AlbumsScreen(model: LibraryFeaturesModel,albums: List<Album>,members: List<AlbumPhoto>,
    onAlbum: (String)->Unit,onFavorites: ()->Unit,onArchive: ()->Unit,onActivity: ()->Unit,
    onPeople: ()->Unit,onPlaces: ()->Unit,onOffline: ()->Unit,onEdits: ()->Unit,bottomClearance: Dp=0.dp) {
    var create by remember { mutableStateOf(false) }
    var name by remember { mutableStateOf("") }
    var manage by remember { mutableStateOf<Album?>(null) }
    var delete by remember { mutableStateOf(false) }
    LazyColumn(Modifier.fillMaxSize(),contentPadding=PaddingValues(start=20.dp,top=20.dp,end=20.dp,bottom=20.dp+bottomClearance),verticalArrangement=Arrangement.spacedBy(12.dp)) {
        item { Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick=onPeople,modifier=Modifier.weight(1f)) {
                com.myphotosanywhere.mpa.ui.ShellIcon(com.myphotosanywhere.mpa.R.drawable.ic_shell_people)
                Spacer(Modifier.width(8.dp));Text("People")
            }
            OutlinedButton(onClick=onPlaces,modifier=Modifier.weight(1f)) {
                com.myphotosanywhere.mpa.ui.ShellIcon(com.myphotosanywhere.mpa.R.drawable.ic_shell_places)
                Spacer(Modifier.width(8.dp));Text("Places")
            }
        } }
        item { Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick=onOffline) { Text("Offline") }
            OutlinedButton(onClick=onEdits) { Text("Edits") }
        } }
        item { Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick=onFavorites) { Text("Favorites") }; OutlinedButton(onClick=onArchive) { Text("Archive") }
        } }
        item { OutlinedButton(onClick=onActivity) { Text("AI activity") } }
        item { Button(onClick={ create=true; name="" }) { Text("New album") } }
        items(albums,key={ it.id }) { album ->
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp)) {
                    TextButton(onClick={ onAlbum(album.id) }) { Text(album.name,style=MaterialTheme.typography.titleLarge) }
                    Text("${members.count { it.albumId==album.id }} items${if(album.pinned) " · kept offline" else ""}")
                    Row {
                        TextButton(onClick={ model.pinAlbum(album,!album.pinned) }) { Text(if(album.pinned) "Unpin offline album" else "Keep album offline") }
                        TextButton(onClick={ manage=album; name=album.name; delete=false }) { Text("Manage") }
                    }
                }
            }
        }
        item { Text("Add photos from their details. Albums and names are stored on this phone.",style=MaterialTheme.typography.bodySmall) }
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

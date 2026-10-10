package com.myphotosanywhere.mpa

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import coil.compose.AsyncImage
import com.myphotosanywhere.mpa.core.AiProvider
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.binding
import com.myphotosanywhere.mpa.drive.DriveStorageScreen
import com.myphotosanywhere.mpa.edit.EditorScreen
import com.myphotosanywhere.mpa.library.*
import java.io.File
import java.text.DateFormat
import java.util.Date

class MainActivity: ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent { MaterialTheme(colorScheme=lightColorScheme(primary=Color(0xFF166D5B),secondary=Color(0xFF49675F),
            background=Color(0xFFF8FAF7),surface=Color(0xFFF8FAF7))) { LibraryScreen() } }
    }
}
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun LibraryScreen(model: LibraryViewModel = viewModel(),features: LibraryFeaturesModel = viewModel()) {
    val photos by model.photos.collectAsStateWithLifecycle()
    val handoffs by model.handoffs.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val featureBusy by features.busy.collectAsStateWithLifecycle()
    val message by model.message.collectAsStateWithLifecycle()
    val featureMessage by features.message.collectAsStateWithLifecycle()
    val albums by features.albums.collectAsStateWithLifecycle()
    val memberships by features.memberships.collectAsStateWithLifecycle()
    val people by features.people.collectAsStateWithLifecycle()
    val faces by features.faces.collectAsStateWithLifecycle()
    val cloud by features.cloud.collectAsStateWithLifecycle()
    val connection by features.connection.collectAsStateWithLifecycle()
    val sync by features.sync.collectAsStateWithLifecycle()
    val downloads by features.downloads.collectAsStateWithLifecycle()
    val settings by features.settings.collectAsStateWithLifecycle()
    var tab by rememberSaveable { mutableStateOf(0) }
    var selectedId by rememberSaveable { mutableStateOf<String?>(null) }
    var resultHandoffId by rememberSaveable { mutableStateOf<String?>(null) }
    var showShare by rememberSaveable { mutableStateOf(false) }
    var editorId by rememberSaveable { mutableStateOf<String?>(null) }
    var query by rememberSaveable { mutableStateOf("") }
    var filter by rememberSaveable { mutableStateOf("All") }
    var albumId by rememberSaveable { mutableStateOf<String?>(null) }
    var personId by rememberSaveable { mutableStateOf<String?>(null) }
    var activity by rememberSaveable { mutableStateOf(false) }
    var exportId by rememberSaveable { mutableStateOf<String?>(null) }
    val snackbar=remember { SnackbarHostState() }
    val context=LocalContext.current
    val addPhotos=rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(100)) { model.importPhotos(it) }
    val importResult=rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if(uri!=null) resultHandoffId?.let { model.importResult(uri,it) }; resultHandoffId=null
    }
    val export=rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        if(uri!=null) photos.find { it.id==exportId }?.let { features.export(it,uri) }; exportId=null
    }
    fun pickResult(handoff: Handoff) { resultHandoffId=handoff.id; importResult.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) }
    fun selectPhoto(id: String) { selectedId=id; showShare=false }
    val selected=photos.find { it.id==selectedId }
    LaunchedEffect(message) { message?.let { snackbar.showSnackbar(it); model.clearMessage() } }
    LaunchedEffect(featureMessage) { featureMessage?.let { snackbar.showSnackbar(it); features.clearMessage() } }
    Scaffold(topBar={ TopAppBar(title={ Column {
        Text("MyPhotosAnywhere",fontWeight=FontWeight.SemiBold)
        Text("Your photos. Your storage.",style=MaterialTheme.typography.labelMedium)
    } }) },snackbarHost={ SnackbarHost(snackbar) },bottomBar={
        NavigationBar { listOf("Photos","Library","People","Places","Storage").forEachIndexed { index,title ->
            NavigationBarItem(selected=tab==index,onClick={ tab=index; activity=false },
                icon={ Text(listOf("▦","▤","☺","⌖","☁")[index]) },label={ Text(title) })
        } }
    }) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {
            if(busy || featureBusy) LinearProgressIndicator(Modifier.fillMaxWidth())
            when(tab) {
                0 -> {
                    val title=albums.find { it.id==albumId }?.name ?: people.find { it.id==personId }?.name ?: "Your library"
                    Row(Modifier.fillMaxWidth().padding(horizontal=16.dp,vertical=8.dp),horizontalArrangement=Arrangement.SpaceBetween,verticalAlignment=Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) { Text(title,style=MaterialTheme.typography.headlineSmall)
                            Text("${photos.size} local items",style=MaterialTheme.typography.bodySmall) }
                        Button(enabled=!busy,onClick={ addPhotos.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo)) }) { Text("Add") }
                    }
                    OutlinedTextField(query,{ query=it },label={ Text("Search names, people, places, dates, objects") },singleLine=true,
                        modifier=Modifier.fillMaxWidth().padding(horizontal=16.dp))
                    Row(Modifier.fillMaxWidth().padding(horizontal=16.dp).horizontalScroll(rememberScrollState()),horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                        listOf("All","Favorites","Offline","Videos","Edits","Archive").forEach { option ->
                            FilterChip(selected=filter==option,onClick={ filter=option },label={ Text(option) })
                        }
                        if(albumId!=null || personId!=null) TextButton(onClick={ albumId=null; personId=null }) { Text("Clear collection") }
                    }
                    val currentKey=connection?.binding()?.key
                    val local=photos.filter { photo ->
                        val names=faces.filter { it.photoId==photo.id }.mapNotNull { face -> people.find { it.id==face.personId }?.name }
                        val indexed=cloud.any { it.photoId==photo.id && !it.removed }
                        (photo.sourceRemoteId==null || photo.originalPath.isNotBlank() || indexed || albumId!=null || filter=="Favorites" || filter=="Archive") &&
                        LibrarySearch.matches(photo,query,names) && (if(filter=="Archive") photo.archived else !photo.archived) &&
                            (filter!="Favorites" || photo.favorite) && (filter!="Offline" || photo.originalPath.isNotBlank()) &&
                            (filter!="Videos" || photo.mimeType.startsWith("video/")) && (filter!="Edits" || photo.parentId!=null) &&
                            (albumId==null || memberships.any { it.albumId==albumId && it.photoId==photo.id }) &&
                            (personId==null || faces.any { it.personId==personId && it.photoId==photo.id })
                    }.sortedWith(compareByDescending<Photo> { it.capturedAt ?: it.importedAt }.thenByDescending { it.id })
                    val remote=cloud.filter { row -> row.bindingKey==currentKey && !row.removed && row.photoId?.let { id -> photos.any { it.id==id } }!=true &&
                        albumId==null && personId==null && filter in listOf("All","Videos") &&
                        (filter!="Videos" || row.mimeType.startsWith("video/")) && (query.isBlank() || row.name.contains(query,true) || row.mimeType.contains(query,true)) }
                    val activeSync=sync.find { it.bindingKey==currentKey }
                    if(activeSync?.running==true) Text("Updating Drive library…",Modifier.padding(horizontal=16.dp),style=MaterialTheme.typography.bodySmall)
                    if(activeSync?.error!=null) Text(activeSync.error ?: "",Modifier.padding(horizontal=16.dp),color=MaterialTheme.colorScheme.error,style=MaterialTheme.typography.bodySmall)
                    if(connection?.folderId!=null) TextButton(onClick=features::refreshCloud) { Text("Refresh Drive · ${remote.size} cloud items") }
                    if(local.isEmpty() && remote.isEmpty()) Box(Modifier.weight(1f).fillMaxWidth().padding(32.dp),contentAlignment=Alignment.Center) {
                        Text(if(photos.isEmpty()) "Add photos or connect a Drive folder in Storage to start your library." else "No items match your search.")
                    } else LazyVerticalGrid(columns=GridCells.Adaptive(110.dp),contentPadding=PaddingValues(8.dp),
                        horizontalArrangement=Arrangement.spacedBy(4.dp),verticalArrangement=Arrangement.spacedBy(4.dp),modifier=Modifier.weight(1f)) {
                        items(local,key={ it.id }) { photo ->
                            val cloudRow=cloud.find { it.photoId==photo.id }
                            PhotoTile(photo,features,cloudRow?.thumbnailUpdatedAt ?: 0,cloudRow) { selectPhoto(photo.id) }
                        }
                        items(remote,key={ "cloud:${it.key}" }) { row ->
                            LaunchedEffect(row.key,row.version,row.thumbnailUpdatedAt) {
                                com.myphotosanywhere.mpa.drive.ThumbnailWorker.request(context,row,connection)
                            }
                            Card(Modifier.aspectRatio(1f).clickable { features.download(row) }) {
                                Box(Modifier.fillMaxSize()) {
                                    AsyncImage(coil.request.ImageRequest.Builder(context).data(File(row.thumbnailPath))
                                        .memoryCacheKey("${row.key}:${row.thumbnailUpdatedAt}").build(),row.name,Modifier.fillMaxSize(),contentScale=ContentScale.Crop)
                                    Surface(Modifier.align(Alignment.BottomStart),color=MaterialTheme.colorScheme.surface.copy(alpha=.85f)) {
                                        Text(if(downloads.any { it.key==row.key && it.state in listOf("PENDING","DOWNLOADING","RETRYING") }) "Downloading…" else "☁ ${row.name}",maxLines=2,
                                            modifier=Modifier.padding(4.dp),style=MaterialTheme.typography.labelSmall)
                                    }
                                }
                            }
                        }
                    }
                }
                1 -> if(!activity) AlbumsScreen(features,albums,memberships,onAlbum={ albumId=it; personId=null; filter="All"; tab=0 },
                    onFavorites={ filter="Favorites"; albumId=null; personId=null; tab=0 },onArchive={ filter="Archive"; albumId=null; personId=null; tab=0 },onActivity={ activity=true })
                else {
                    TextButton(onClick={ activity=false }) { Text("Back to Library") }
                    Text("AI activity",style=MaterialTheme.typography.headlineSmall,modifier=Modifier.padding(16.dp))
                    LazyColumn(contentPadding=PaddingValues(16.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
                        if(handoffs.isEmpty()) item { Text("Open a photo and choose Edit with AI. Saved handoffs appear here.") }
                        items(handoffs,key={ it.id }) { handoff -> Card(Modifier.fillMaxWidth()) {
                            Column(Modifier.padding(12.dp)) {
                                Text(AiProvider.entries.find { it.name==handoff.provider }?.title ?: handoff.provider,style=MaterialTheme.typography.titleMedium)
                                Text(handoff.instructions,maxLines=3)
                                Row { Button(enabled=!busy,onClick={ pickResult(handoff) }) { Text("Import result") }
                                    TextButton(enabled=!busy,onClick={ model.dismiss(handoff) }) { Text("Dismiss") } }
                            }
                        } }
                    }
                }
                2 -> PeopleScreen(features,people,faces,settings.faceIndexing,onPerson={ personId=it; albumId=null; filter="All"; tab=0 },onPhoto=::selectPhoto)
                3 -> PlacesScreen(photos,settings.onlineMaps,{ features.updateSettings(settings.copy(onlineMaps=it)) },::selectPhoto)
                4 -> DriveStorageScreen(photos)
            }
        }
    }
    if(selected!=null && !showShare && editorId==null) PhotoDetails(selected,features,albums,memberships,people,faces,
        onClose={ selectedId=null },onEdit={ editorId=selected.id },onAI={ showShare=true },onExport={
            exportId=selected.id; export.launch(selected.displayName)
        },onResult=handoffs.firstOrNull { it.photoId==selected.id }?.let { h -> { pickResult(h) } })
    if(selected!=null && showShare) ShareDialog(busy,onDismiss={ showShare=false }) { provider,instructions ->
        model.share(selected,provider,instructions) { intent -> context.startActivity(intent); showShare=false }
    }
    editorId?.let { id -> EditorScreen(id,onClose={ editorId=null },onSaved={ saved -> editorId=null; selectedId=saved }) }
}

@Composable
private fun PhotoTile(photo: Photo,features: LibraryFeaturesModel,thumbnailVersion: Long,cloudRow: com.myphotosanywhere.mpa.drive.CloudPhoto?,onClick: ()->Unit) {
    val thumb by produceState<File?>(null,photo.id,photo.originalPath,thumbnailVersion) { value=runCatching { features.app.cache.thumbnail(photo) }.getOrNull() }
    LaunchedEffect(photo.id,cloudRow?.version,thumbnailVersion) {
        if(cloudRow!=null) com.myphotosanywhere.mpa.drive.ThumbnailWorker.request(features.app,cloudRow,features.connection.value)
    }
    Card(Modifier.aspectRatio(1f).testTag("photo:${photo.id}").clickable(onClick=onClick)) {
        Box(Modifier.fillMaxSize()) {
            AsyncImage(coil.request.ImageRequest.Builder(features.app).data(thumb ?: File(photo.thumbnailPath))
                .memoryCacheKey("${photo.id}:$thumbnailVersion:${photo.originalPath}").build(),photo.displayName,Modifier.fillMaxSize(),contentScale=ContentScale.Crop)
            val label=listOfNotNull(if(photo.favorite) "★" else null,if(photo.pinned) "↓" else null,
                if(photo.originalPath.isBlank()) "☁" else null,if(photo.mimeType.startsWith("video/")) "▶" else null,
                if(photo.parentId!=null) "Edit" else null).joinToString(" ")
            if(label.isNotBlank()) Surface(Modifier.align(Alignment.BottomEnd).padding(4.dp)) {
                Text(label,modifier=Modifier.padding(4.dp),style=MaterialTheme.typography.labelSmall)
            }
        }
    }
}

@Composable
private fun ShareDialog(busy: Boolean,onDismiss: ()->Unit,onShare: (AiProvider,String)->Unit) {
    var provider by rememberSaveable { mutableStateOf(AiProvider.CHATGPT.name) }
    var instructions by rememberSaveable { mutableStateOf("") }
    val selected=AiProvider.valueOf(provider)
    AlertDialog(onDismissRequest={ if(!busy) onDismiss() },title={ Text("Edit with your AI app") },text={
        Column(Modifier.verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            AiProvider.entries.forEach { option -> Row(Modifier.clickable { provider=option.name },verticalAlignment=Alignment.CenterVertically) {
                RadioButton(selected=provider==option.name,onClick={ provider=option.name },enabled=!busy); Text(option.title)
            } }
            Text(selected.guidance,style=MaterialTheme.typography.bodySmall)
            OutlinedTextField(instructions,{ if(it.length<=4000) instructions=it },label={ Text("Describe your edit") },minLines=3,enabled=!busy)
            Text("Shares a preview up to 2048 pixels and copies instructions to your clipboard. GPS is removed. Save the AI output and import it here afterward.",style=MaterialTheme.typography.bodySmall)
        }
    },confirmButton={ Button(enabled=!busy && instructions.isNotBlank(),onClick={ onShare(selected,instructions) }) { Text("Continue to share sheet") } },
        dismissButton={ TextButton(enabled=!busy,onClick=onDismiss) { Text("Cancel") } })
}

package com.myphotosanywhere.mpa.library

import android.content.Intent
import android.net.Uri
import android.widget.VideoView
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import coil.compose.AsyncImage
import com.myphotosanywhere.mpa.data.*
import java.io.File
import java.text.DateFormat
import java.util.Date

@Composable
fun PhotoDetails(photo: Photo,model: LibraryFeaturesModel,albums: List<Album>,memberships: List<AlbumPhoto>,
    people: List<Person>,faces: List<FaceRecord>,onClose: ()->Unit,onEdit: ()->Unit,onAI: ()->Unit,
    onExport: ()->Unit,onResult: (()->Unit)? = null) {
    val context=LocalContext.current
    val reader by produceState<AutoCloseable?>(null,photo.id) {
        value=model.app.cache.acquireReader(photo.id)
        awaitDispose { value?.close() }
    }
    val busy by model.busy.collectAsStateWithLifecycle()
    val cloud by model.cloud.collectAsStateWithLifecycle()
    val available=photo.originalPath.isNotBlank() && File(photo.originalPath).isFile
    var collections by remember { mutableStateOf(false) }
    var location by remember { mutableStateOf(false) }
    var place by remember(photo.id) { mutableStateOf(photo.placeLabel) }
    var latitude by remember(photo.id) { mutableStateOf(photo.latitude?.toString() ?: "") }
    var longitude by remember(photo.id) { mutableStateOf(photo.longitude?.toString() ?: "") }
    var locationError by remember { mutableStateOf<String?>(null) }
    var naming by remember { mutableStateOf<FaceRecord?>(null) }
    var name by remember { mutableStateOf("") }
    AlertDialog(onDismissRequest=onClose,title={ Text(photo.displayName) },text={
        Column(Modifier.verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(10.dp)) {
            if(photo.mimeType.startsWith("video/") && available && reader!=null) AndroidView(factory={
                VideoView(it).apply {
                    setVideoPath(photo.originalPath)
                    val controls=android.widget.MediaController(it); controls.setAnchorView(this); setMediaController(controls)
                    setOnPreparedListener { player -> player.isLooping=false; seekTo(1) }
                }
            },modifier=Modifier.fillMaxWidth().height(240.dp),onRelease={ it.stopPlayback() })
            else AsyncImage(if(available && reader!=null) File(photo.originalPath) else File(photo.thumbnailPath),photo.displayName,
                modifier=Modifier.fillMaxWidth().height(240.dp),contentScale=ContentScale.Fit)
            Text("${DateFormat.getDateTimeInstance().format(Date(photo.capturedAt ?: photo.importedAt))} · %.1f MiB".format(photo.bytes/(1024.0*1024)))
            Text(if(photo.parentId==null) "Original preserved" else "Edited copy · original preserved")
            if(!available) {
                Text("Original is stored in Drive. Download it to edit, share, or export.")
                cloud.find { it.key=="${photo.sourceBindingKey}:${photo.sourceRemoteId}" }?.let { row ->
                    Button(enabled=!busy && !row.removed,onClick={ model.download(row,photo.pinned) }) { Text("Download original") }
                }
            }
            Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                FilterChip(selected=photo.favorite,enabled=!busy,onClick={ model.updatePhoto(photo.id) { it.copy(favorite=!it.favorite) } },label={ Text("Favorite") })
                FilterChip(selected=photo.pinned,enabled=!busy,onClick={
                    model.updatePhoto(photo.id) { it.copy(pinned=!it.pinned) }
                },label={ Text("Keep offline") })
            }
            TextButton(onClick={ collections=true }) { Text("Albums") }
            TextButton(enabled=!busy,onClick={ model.updatePhoto(photo.id) { it.copy(archived=!it.archived) } }) { Text(if(photo.archived) "Unarchive" else "Archive") }
            if(photo.placeLabel.isNotBlank()) Text(photo.placeLabel,style=MaterialTheme.typography.titleMedium)
            if(LibrarySearch.validCoordinates(photo.latitude,photo.longitude)) {
                Text("GPS: %.5f, %.5f".format(photo.latitude,photo.longitude))
                TextButton(onClick={
                    try { context.startActivity(Intent(Intent.ACTION_VIEW,Uri.parse("geo:${photo.latitude},${photo.longitude}?q=${photo.latitude},${photo.longitude}"))) }
                    catch(_: android.content.ActivityNotFoundException) { model.message.value="Install a map app to open this location." }
                }) { Text("Open in Maps") }
            }
            TextButton(onClick={ place=photo.placeLabel; latitude=photo.latitude?.toString() ?: ""; longitude=photo.longitude?.toString() ?: ""; locationError=null; location=true }) { Text("Name or set location") }
            if(photo.labels.isNotBlank()) Text("Objects: ${photo.labels}",style=MaterialTheme.typography.bodySmall)
            val detected=faces.filter { it.photoId==photo.id }
            if(detected.isNotEmpty()) {
                Text("People",style=MaterialTheme.typography.titleMedium)
                detected.forEach { face ->
                    Row { AsyncImage(File(face.cropPath),"Face",Modifier.size(56.dp))
                        TextButton(onClick={ naming=face; name=people.find { it.id==face.personId }?.name ?: "" }) {
                            Text(people.find { it.id==face.personId }?.name ?: "Name or correct")
                        }
                    }
                }
            }
            if(available) {
                Row { TextButton(enabled=!busy,onClick={ model.share(photo) { context.startActivity(it) } }) { Text("Share") }
                    TextButton(enabled=!busy,onClick=onExport) { Text("Export original") } }
                Text("Image sharing removes GPS. Export original preserves its metadata.",style=MaterialTheme.typography.bodySmall)
                if(photo.mimeType.startsWith("image/")) OutlinedButton(enabled=!busy,onClick=onAI) { Text("Edit with AI") }
            }
            onResult?.let { OutlinedButton(onClick=it) { Text("Import AI result") } }
        }
    },confirmButton={ Button(enabled=available && !busy && photo.mimeType.startsWith("image/"),onClick=onEdit) { Text("Edit") } },
        dismissButton={ TextButton(onClick=onClose) { Text("Close") } })
    if(collections) AlertDialog(onDismissRequest={ collections=false },title={ Text("Albums") },text={
        Column(Modifier.verticalScroll(rememberScrollState())) {
            if(albums.isEmpty()) Text("Create an album in Library first.")
            albums.forEach { album ->
                val included=memberships.any { it.albumId==album.id && it.photoId==photo.id }
                Row { Checkbox(checked=included,enabled=!busy,onCheckedChange={
                    if(it) model.addToAlbum(album.id,photo.id) else model.removeFromAlbum(album.id,photo.id)
                }); Text(album.name,Modifier.padding(top=12.dp)) }
            }
        }
    },confirmButton={ TextButton(onClick={ collections=false }) { Text("Done") } })
    if(location) AlertDialog(onDismissRequest={ location=false },title={ Text("Photo location") },text={ Column(verticalArrangement=Arrangement.spacedBy(8.dp)) {
        OutlinedTextField(place,{ if(it.length<=200) place=it },label={ Text("Place name") })
        OutlinedTextField(latitude,{ latitude=it },label={ Text("Latitude (optional)") },singleLine=true)
        OutlinedTextField(longitude,{ longitude=it },label={ Text("Longitude (optional)") },singleLine=true)
        Text("Stored in your local library. Original file bytes are preserved.",style=MaterialTheme.typography.bodySmall)
        locationError?.let { Text(it,color=MaterialTheme.colorScheme.error) }
    } },confirmButton={ Button(onClick={
        val lat=latitude.toDoubleOrNull(); val lon=longitude.toDoubleOrNull()
        if((latitude.isNotBlank() || longitude.isNotBlank()) && !LibrarySearch.validCoordinates(lat,lon)) locationError="Enter latitude −90 to 90 and longitude −180 to 180, or leave both blank."
        else { model.updatePhoto(photo.id) { it.copy(placeLabel=place.trim(),latitude=lat,longitude=lon) }; location=false }
    }) { Text("Save") } },dismissButton={ TextButton(onClick={ location=false }) { Text("Cancel") } })
    naming?.let { face -> AlertDialog(onDismissRequest={ naming=null },title={ Text("Correct this face") },text={ Column {
        AsyncImage(File(face.cropPath),"Selected face",Modifier.size(100.dp))
        OutlinedTextField(name,{ if(it.length<=100) name=it },label={ Text("Person name") })
        people.forEach { person -> TextButton(onClick={ model.assign(listOf(face.id),person.name,person.id); naming=null }) { Text("Assign to ${person.name}") } }
        TextButton(onClick={ model.unassignFace(face.id); naming=null }) { Text("Keep unassigned; reject suggestions") }
    } },confirmButton={ Button(enabled=name.isNotBlank(),onClick={ model.assign(listOf(face.id),name); naming=null }) { Text("Create person") } },
        dismissButton={ TextButton(onClick={ naming=null }) { Text("Cancel") } }) }
}

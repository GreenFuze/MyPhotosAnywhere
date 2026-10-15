package com.myphotosanywhere.mpa.library

import androidx.core.net.toUri
import android.content.Intent
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalWindowInfo
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import kotlinx.coroutines.launch
import com.myphotosanywhere.mpa.R
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import coil.compose.AsyncImage
import com.myphotosanywhere.mpa.data.*
import java.io.File
import java.text.DateFormat
import java.util.Date

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PhotoDetails(photo: Photo,model: LibraryFeaturesModel,albums: List<Album>,memberships: List<AlbumPhoto>,
    people: List<Person>,faces: List<FaceRecord>,onClose: ()->Unit,onEdit: ()->Unit,onAI: ()->Unit,
    onExport: ()->Unit,onResult: (()->Unit)? = null,onView: (()->Unit)? = null,showPreview: Boolean=true) {
    val context=LocalContext.current
    val reader by produceState<AutoCloseable?>(null,photo.id) {
        value=model.app.cache.acquireReader(photo.id)
        awaitDispose { value?.close() }
    }
    val busy by model.busy.collectAsStateWithLifecycle()
    val cloud by model.cloud.collectAsStateWithLifecycle()
    val sources by model.sources.collectAsStateWithLifecycle()
    val factsRepository=remember(model) { PhotoFactsRepository(model.app.timeline,model.app.cache) }
    var factsFailure by remember(photo.id) { mutableStateOf(false) }
    var factsRetry by remember(photo.id) { mutableIntStateOf(0) }
    val linkedCapture=remember(cloud,photo.id,photo.sourceBindingKey,photo.sourceRemoteId) {
        cloud.asSequence().filter { it.photoId==photo.id && it.capturedAt!=null }
            .minWithOrNull(compareBy<com.myphotosanywhere.mpa.drive.CloudPhoto> {
                if(it.bindingKey==photo.sourceBindingKey && it.remoteId==photo.sourceRemoteId) 0 else 1
            }.thenBy { it.key })?.capturedAt
    }
    val facts by produceState<PhotoFacts?>(null,photo.id,photo.capturedAt,photo.archived,photo.originalPath,photo.cachedVersion,linkedCapture,factsRetry) {
        factsFailure=false
        try { value=factsRepository.read(photo) }
        catch(_: android.database.sqlite.SQLiteException) { factsFailure=true }
    }
    val shownMillis=facts?.millis ?: photo.capturedAt ?: photo.importedAt
    val available by produceState(false,photo.id,photo.originalPath,photo.cachedVersion,busy) {
        value=withContext(Dispatchers.IO) { photo.originalPath.isNotBlank() && File(photo.originalPath).isFile }
    }
    var collections by remember { mutableStateOf(false) }
    var location by remember { mutableStateOf(false) }
    var place by remember(photo.id) { mutableStateOf(photo.placeLabel) }
    var latitude by remember(photo.id) { mutableStateOf(photo.latitude?.toString() ?: "") }
    var longitude by remember(photo.id) { mutableStateOf(photo.longitude?.toString() ?: "") }
    var locationError by remember { mutableStateOf<String?>(null) }
    var naming by remember { mutableStateOf<FaceRecord?>(null) }
    var name by remember { mutableStateOf("") }
    val sheet=rememberModalBottomSheetState(skipPartiallyExpanded=true)
    val scope=rememberCoroutineScope()
    val close: ()->Unit={ scope.launch { sheet.hide();onClose() } }
    ModalBottomSheet(onDismissRequest=onClose,sheetState=sheet,modifier=Modifier.testTag("photo-details")) {
        Column(Modifier.fillMaxWidth().heightIn(max=with(LocalDensity.current) { LocalWindowInfo.current.containerSize.height.toDp()*.7f })) {
            FlowRow(Modifier.fillMaxWidth().padding(horizontal=16.dp),horizontalArrangement=Arrangement.SpaceBetween) {
                Text("Photo details",style=MaterialTheme.typography.titleLarge,modifier=Modifier.padding(vertical=12.dp).semantics { heading() })
                TextButton(modifier=Modifier.testTag("photo-edit").heightIn(min=48.dp),enabled=available && !busy && photo.mimeType.startsWith("image/"),onClick=onEdit) { Text("Edit") }
                TextButton(onClick=close,modifier=Modifier.heightIn(min=48.dp).testTag("photo-details-close")) { Text("Close") }
            }
            ViewerStatus(model)
            Column(Modifier.weight(1f,fill=false).verticalScroll(rememberScrollState()).padding(horizontal=20.dp).navigationBarsPadding(),verticalArrangement=Arrangement.spacedBy(10.dp)) {
            DetailSection("Date and file",R.drawable.ic_action_info) {
                Text(DateFormat.getDateInstance(DateFormat.FULL).format(Date(shownMillis)),style=MaterialTheme.typography.titleLarge)
                Text(DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(shownMillis)),style=MaterialTheme.typography.bodyMedium)
                when(facts?.provenance) {
                    com.myphotosanywhere.mpa.timeline.TimestampProvenance.IMPORT -> Text("Import date · capture date unavailable",style=MaterialTheme.typography.bodySmall)
                    com.myphotosanywhere.mpa.timeline.TimestampProvenance.LINKED_CAPTURE -> Text("Capture date from linked source",style=MaterialTheme.typography.bodySmall)
                    else -> Unit
                }
                facts?.dimensions?.let { (width,height) -> Text("$width × $height",style=MaterialTheme.typography.bodySmall,modifier=Modifier.testTag("details:dimensions")) }
                Text(photo.displayName,style=MaterialTheme.typography.bodyMedium)
                if(factsFailure) TextButton(onClick={ factsRetry++ }) { Text("Retry file details") }
                Text("${photo.mimeType} · %.1f MiB".format(photo.bytes/(1024.0*1024)),style=MaterialTheme.typography.bodySmall)
            }
            if(showPreview) AsyncImage(coil.request.ImageRequest.Builder(context)
                .data(File(if(photo.thumbnailPath.isNotBlank()) photo.thumbnailPath else if(available && reader!=null) photo.originalPath else ""))
                .size(480).build(),photo.displayName,modifier=Modifier.fillMaxWidth().height(160.dp),contentScale=ContentScale.Fit)
            if(available && onView!=null) TextButton(onClick=onView) { Text("View full screen") }
            DetailSection("Original and source",R.drawable.ic_shell_account) {

            Text(if(photo.parentId==null) "Original preserved" else "Edited copy · original preserved")

            Text(if(available) "Original available on this device" else "Original unavailable on this device",
                modifier=Modifier.testTag("details:availability"))
            val source=sources.find { it.key==photo.sourceBindingKey }
            Text(source?.let { "Source: ${it.folderName} · ${it.email}" }
                ?: if(photo.sourceBindingKey==null) "Source: Imported on this device" else "Source disconnected · ${photo.sourceBindingKey}",
                modifier=Modifier.testTag("details:source"))
            if(GalleryVisibility.removedFromDrive(photo,cloud,sources.map { it.key }.toSet()))
                Text("Removed from Drive · local copy preserved",color=MaterialTheme.colorScheme.error)
            if(!available) {
                Text(if(photo.sourceRemoteId!=null) "Download the Drive original to edit, share, or export." else "The original file is unavailable. Your indexed metadata is preserved.")
                cloud.find { it.key=="${photo.sourceBindingKey}:${photo.sourceRemoteId}" }?.let { row ->
                    Button(enabled=!busy && !row.removed,onClick={ model.download(row,photo.pinned) }) { Text("Download original") }
                }
            }
            }
            DetailSection("Organize",R.drawable.ic_shell_collections) {
            FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                FilterChip(modifier=Modifier.heightIn(min=48.dp).testTag("details:favorite"),selected=photo.favorite,enabled=!busy,onClick={ model.updatePhoto(photo.id) { it.copy(favorite=!it.favorite) } },label={ Text("Favorite") })
                FilterChip(modifier=Modifier.heightIn(min=48.dp),selected=photo.pinned,enabled=!busy,onClick={
                    model.pinPhoto(photo.id,!photo.pinned)
                },label={ Text("Keep offline") })
            }
            TextButton(onClick={ collections=true }) { Text("Albums") }
            TextButton(enabled=!busy,onClick={ model.updatePhoto(photo.id) { it.copy(archived=!it.archived) } }) { Text(if(photo.archived) "Unarchive" else "Archive") }
            }
            DetailSection("Location",R.drawable.ic_shell_places) {
            if(photo.placeLabel.isNotBlank()) Text(photo.placeLabel,style=MaterialTheme.typography.titleMedium)
            if(LibrarySearch.validCoordinates(photo.latitude,photo.longitude)) {
                Text("GPS: %.5f, %.5f".format(photo.latitude,photo.longitude))
                TextButton(onClick={
                    try { context.startActivity(Intent(Intent.ACTION_VIEW,"geo:${photo.latitude},${photo.longitude}?q=${photo.latitude},${photo.longitude}".toUri())) }
                    catch(_: android.content.ActivityNotFoundException) { model.message.value="Install a map app to open this location." }
                }) { Text("Open in Maps") }
            }
            TextButton(onClick={ place=photo.placeLabel; latitude=photo.latitude?.toString() ?: ""; longitude=photo.longitude?.toString() ?: ""; locationError=null; location=true }) { Text("Name or set location") }
            }
            if(photo.labels.isNotBlank() || photo.importedMetadata.isNotBlank()) DetailSection("Photo metadata",R.drawable.ic_action_info) {
            val importedNames=remember(photo.importedMetadata) { com.myphotosanywhere.mpa.people.ImportedPeopleCodec().names(photo.importedMetadata) }
            if(importedNames.isNotEmpty()) Text("Imported people labels: ${importedNames.joinToString()}",modifier=Modifier.testTag("details:imported-people"))
            if(photo.labels.isNotBlank()) Text("Objects: ${photo.labels}",style=MaterialTheme.typography.bodySmall)
            if(photo.importedMetadata.isNotBlank()) {
                val provider=remember(photo.importedMetadata) { runCatching { com.google.gson.JsonParser.parseString(photo.importedMetadata).asJsonObject }.getOrNull() }
                provider?.getAsJsonObject("googleTakeout")?.let { google ->
                    Text("Google Takeout metadata",style=MaterialTheme.typography.titleMedium)
                    google.get("description")?.asString?.takeIf { it.isNotBlank() }?.let { Text(it) }
                    google.getAsJsonArray("people")?.mapNotNull { it.asJsonObject.get("name")?.asString }?.takeIf { it.isNotEmpty() }?.let {
                        Text("People hints (unconfirmed): ${it.joinToString()}",style=MaterialTheme.typography.bodySmall)
                    }
                    Text("Existing MPA date and location take precedence. The full Google record is retained in MPA metadata.",style=MaterialTheme.typography.bodySmall)
                }
            }
            }
            val detected=faces.filter { it.photoId==photo.id }
            if(detected.isNotEmpty()) DetailSection("People",R.drawable.ic_shell_people) {
                detected.forEach { face ->
                    Row { AsyncImage(File(face.cropPath),"Face",Modifier.size(56.dp))
                        TextButton(onClick={ naming=face; name=people.find { it.id==face.personId }?.name ?: "" }) {
                            Text(people.find { it.id==face.personId }?.name ?: "Name or correct")
                        }
                    }
                }
            }
            if(available) DetailSection("Share and edit",R.drawable.ic_action_share) {
                FlowRow { TextButton(enabled=!busy,onClick={ model.share(photo) { context.startActivity(it) } }) { Text("Share") }
                    TextButton(enabled=!busy,onClick=onExport) { Text("Export original") } }
                Text("Image sharing removes GPS. Export original preserves its metadata.",style=MaterialTheme.typography.bodySmall)
                if(photo.mimeType.startsWith("image/")) OutlinedButton(enabled=!busy,onClick=onAI) { Text("Edit with AI") }
            }
            onResult?.let { OutlinedButton(onClick=it) { Text("Import AI result") } }
            Spacer(Modifier.height(20.dp))
            }
        }
    }
    if(collections) AlertDialog(onDismissRequest={ collections=false },title={ Text("Albums") },text={
        Column(Modifier.verticalScroll(rememberScrollState())) {
            if(albums.isEmpty()) Text("Create an album in Collections first.")
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

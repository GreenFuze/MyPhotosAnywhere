package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.shape.CircleShape
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.foundation.clickable
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.pluralStringResource
import com.myphotosanywhere.mpa.R
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import coil.compose.AsyncImage
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.people.FaceMatching
import com.myphotosanywhere.mpa.people.FaceReview
import com.myphotosanywhere.mpa.people.PeopleRepository
import java.io.File

@Composable
internal fun PeopleScreen(model: LibraryFeaturesModel,people: List<Person>,faces: List<FaceRecord>,enabled: Boolean,
    onPerson: (String)->Unit,onPhoto: (String)->Unit,onSettings: ()->Unit,
    listState: androidx.compose.foundation.lazy.LazyListState=androidx.compose.foundation.lazy.rememberLazyListState()) {
    val repository=remember(model) { com.myphotosanywhere.mpa.people.CanonicalPeopleRepository(model.app.timeline) }
    var retryPeople by remember { mutableIntStateOf(0) }
    val labelError by model.app.personLabels.error.collectAsStateWithLifecycle()
    val indexingLabels by model.app.personLabels.indexing.collectAsStateWithLifecycle()
    val state by produceState(com.myphotosanywhere.mpa.people.CanonicalPeopleState(),repository,retryPeople) {
        try { repository.changes().collect {
            value=value.copy(loading=!value.hasSnapshot,error=null)
            value=com.myphotosanywhere.mpa.people.CanonicalPeopleState(repository.groups(),loading=false,hasSnapshot=true)
        } } catch(cancelled: kotlinx.coroutines.CancellationException) { throw cancelled }
        catch(error: android.database.sqlite.SQLiteException) { value=value.copy(loading=false,error=error.message ?: "Cannot read People. Existing names are kept. Retry.") }
    }
    var naming by rememberSaveable { mutableStateOf<List<String>>(emptyList()) }
    var name by rememberSaveable { mutableStateOf("") }
    var deleting by remember { mutableStateOf(false) }
    var managingId by rememberSaveable { mutableStateOf<String?>(null) }
    val managing=people.firstOrNull { it.id==managingId }
    var rescanning by remember { mutableStateOf(false) }
    var explaining by remember { mutableStateOf<String?>(null) }
    var selected by rememberSaveable { mutableStateOf<Set<String>>(emptySet()) }
    var namingPersonId by rememberSaveable { mutableStateOf<String?>(null) }
    val namingPerson=namingPersonId?.let { id -> people.firstOrNull { it.id==id } ?: Person(id,name) }
    val reviewInput=remember(faces) { PeopleFaceReviewInput(faces.toList()) }
    val reviewed by produceState<PeopleFaceReviewSnapshot?>(null,reviewInput) {
        value=PeopleFaceReviewSnapshot(reviewInput,kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.Default) {
            FaceReview.analyze(reviewInput.faces,PeopleRepository.MODEL_VERSION)
        })
    }
    // A background result belongs to one exact observed input. A previous result
    // cannot declare the replacement input ready while its new effect is starting.
    val review=reviewed?.takeIf { it.input===reviewInput }?.result
    val byId=faces.associateBy { it.id }
    val pending=faces.filter { it.personId==null && people.any { person -> person.id==it.suggestedPersonId } }
    val pendingIds=pending.map { it.id }.toSet()
    val groups=review?.groups.orEmpty().map { ids -> ids.filter { it !in pendingIds } }.filter { it.isNotEmpty() }
    fun startNaming(ids: List<String>,person: Person?=null) {
        naming=ids; selected=ids.toSet(); namingPersonId=person?.id; name=person?.name ?: ""
    }
    // A temporary header-only loading list must not clamp the saved person-card anchor.
    val loadingList=androidx.compose.foundation.lazy.rememberLazyListState()
    LazyColumn(Modifier.fillMaxSize().testTag("people-list").semantics {
        contentDescription="Detected faces: ${faces.size}"
        stateDescription=when { state.error!=null -> "People could not update";!state.hasSnapshot -> "Loading people";review==null -> "Reviewing detected faces";indexingLabels -> "Updating saved names";else -> "People ready" }
    },state=if(!state.hasSnapshot) loadingList else listState,
        contentPadding=PaddingValues(20.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
        item(key="people:heading") { Text("People",style=MaterialTheme.typography.headlineSmall) }
        item(key="people:controls") {
            ScanActions(model)
            com.myphotosanywhere.mpa.management.GroupedMenu(listOf(com.myphotosanywhere.mpa.management.MenuEntry(
                "Recognition settings",if(enabled) "On-device face matching is enabled" else "Face recognition is optional",
                R.drawable.ic_action_settings,"people:settings",open=onSettings)))
            if(state.loading || review==null || indexingLabels) LinearProgressIndicator(Modifier.fillMaxWidth())
            if(indexingLabels) Text("Updating saved people names…",style=MaterialTheme.typography.bodySmall,modifier=Modifier.testTag("people:indexing"))
            state.error?.let { Text(it,color=MaterialTheme.colorScheme.error);TextButton(onClick={ retryPeople++ }) { Text("Retry People") } }
            labelError?.let { Text(it,color=MaterialTheme.colorScheme.error);TextButton(onClick={ model.app.personLabels.retry() }) { Text("Retry failed labels") } }
            if(!state.loading && review!=null && state.error==null && state.groups.isEmpty()) Text("People appear from your imported names and reviewed faces. Possible face matches need your confirmation.",style=MaterialTheme.typography.bodySmall)
        }
        items(state.groups,key={ "person:${it.person.id}" }) { group ->
            val person=group.person
            Card(onClick={ onPerson(person.id) },modifier=Modifier.fillMaxWidth().testTag("person:${person.id}")) {
                Row(Modifier.padding(12.dp),horizontalArrangement=Arrangement.spacedBy(12.dp),verticalAlignment=androidx.compose.ui.Alignment.CenterVertically) {
                    if(group.thumbnail.isNotBlank()) AsyncImage(rememberPreview(group.thumbnail,group.coverPhotoId),null,Modifier.size(72.dp).clip(CircleShape),contentScale=ContentScale.Crop)
                    else Box(Modifier.size(72.dp),contentAlignment=androidx.compose.ui.Alignment.Center) { com.myphotosanywhere.mpa.ui.ShellIcon(R.drawable.ic_shell_people) }
                    Column(Modifier.weight(1f).heightIn(min=48.dp)) {
                        Text(person.name,style=MaterialTheme.typography.titleMedium)
                        Text(pluralStringResource(R.plurals.ux_photo_count,group.photos,group.photos))
                        val candidates=pending.filter { it.suggestedPersonId==person.id }
                        if(candidates.isNotEmpty()) TextButton(onClick={ startNaming(candidates.map { it.id },person) },modifier=Modifier.testTag("person-review:${person.id}")) {
                            Text("Review ${candidates.size} possible matches")
                        }
                    }
                    IconButton(onClick={ managingId=person.id },modifier=Modifier.size(48.dp).testTag("manage-person:${person.id}")) {
                        com.myphotosanywhere.mpa.ui.ShellIcon(R.drawable.ic_action_more,"Manage ${person.name}")
                    }
                }
            }
        }
        item(key="people:unassigned") {
            Text("Unassigned faces · ${faces.count { it.personId==null }}",style=MaterialTheme.typography.titleMedium)
            val unavailable=review?.diagnostics?.values.orEmpty().count { it.status in listOf(FaceReview.Status.NO_EMBEDDING,FaceReview.Status.INVALID_EMBEDDING,FaceReview.Status.DIFFERENT_MODEL) }
            if(unavailable>0) Text("$unavailable unassigned faces lack usable matching data. Open Why separate? to check them.")
        }
        items(groups,key={ it.first() }) { ids ->
            val group=ids.mapNotNull { byId[it] }
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(12.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
                    Text(if(group.size>1) "Possible group · ${group.size} faces" else "Unknown face")
                    Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) { group.take(4).forEach { face ->
                        AsyncImage(File(face.cropPath),"Detected face",Modifier.size(64.dp).clickable { onPhoto(face.photoId) })
                    } }
                    TextButton(onClick={ startNaming(ids) },modifier=Modifier.testTag("name-faces:${ids.first()}")) { Text(if(group.size>1) "Review and name group" else "Name this person") }
                    group.forEach { face ->
                        TextButton(onClick={ explaining=face.id },modifier=Modifier.testTag("face-why:${face.id}")) { Text("Why separate?") }
                    }
                }
            }
        }
        if(faces.isNotEmpty()) item {
            TextButton(enabled=enabled,onClick={ rescanning=true },modifier=Modifier.heightIn(min=48.dp).testTag("rescan-faces")) { Text("Rescan all faces") }
            TextButton(onClick={ deleting=true },modifier=Modifier.heightIn(min=48.dp)) { Text("Clear face recognition data") }
        }
        if(enabled && faces.isEmpty()) item { Text("Enable indexing of connected Drive sources in Recognition settings, or download photos to scan them on this phone. Individual pet recognition is not yet included; cats and dogs are searchable object labels.") }
    }
    if(naming.isNotEmpty()) AlertDialog(onDismissRequest={ naming=emptyList() },title={ Text("Name these faces") },
        text={ LazyColumn(verticalArrangement=Arrangement.spacedBy(8.dp)) {
            item { Text("Select the faces that belong to the same person. Naming once applies to all selected appearances.") }
            items(naming,key={ it }) { id -> byId[id]?.let { face ->
                Row(verticalAlignment=androidx.compose.ui.Alignment.CenterVertically) {
                    Checkbox(checked=id in selected,onCheckedChange={ checked -> selected=if(checked) selected+id else selected-id })
                    AsyncImage(File(face.cropPath),"Face to review",Modifier.size(64.dp).clickable { onPhoto(face.photoId) })
                    if(namingPerson!=null) TextButton(onClick={ model.unassignFace(id); naming=naming-id; selected=selected-id }) { Text("Wrong match") }
                }
            } }
            item { OutlinedTextField(name,{ if(it.length<=200) name=it },readOnly=namingPerson!=null,label={ Text("Name") }) }
        items(people,key={ it.id }) { person -> TextButton(enabled=selected.isNotEmpty(),onClick={ model.assign(selected.toList(),person.name,person.id); naming=emptyList() }) { Text("Use ${person.name}") } }
        } },confirmButton={ Button(enabled=name.isNotBlank() && selected.isNotEmpty(),onClick={ model.assign(selected.toList(),name,namingPerson?.id); naming=emptyList() }) { Text("Confirm name") } },
        dismissButton={ TextButton(onClick={ naming=emptyList() }) { Text("Cancel") } })
    managing?.let { PersonManagementDialog(it,people,model) { managingId=null } }
    if(deleting) AlertDialog(onDismissRequest={ deleting=false },title={ Text("Clear face recognition data?") },
        text={ Text("This removes on-device face crops, embeddings and face assignments, and turns recognition off. People names, photo labels and original photos are preserved.") },
        confirmButton={ TextButton(onClick={ model.deletePeopleIndex(); deleting=false }) { Text("Clear recognition data") } },
        dismissButton={ TextButton(onClick={ deleting=false }) { Text("Cancel") } })
    if(rescanning) AlertDialog(onDismissRequest={ rescanning=false },title={ Text("Rescan all faces?") },
        text={ Text("Repeat face detection and matching across the library. Names and corrections are preserved. Connected originals may be downloaded again, using your network preferences. Processing runs on this phone and can be paused.") },
        confirmButton={ TextButton(onClick={ model.rescanFaces(); rescanning=false }) { Text("Rescan") } },
        dismissButton={ TextButton(onClick={ rescanning=false }) { Text("Cancel") } })
    explaining?.let { id ->
        val diagnostic by produceState<FaceReview.Diagnostic?>(null,id,faces) {
            value=kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.Default) { FaceReview.diagnose(id,faces,PeopleRepository.MODEL_VERSION) }
        }
        AlertDialog(onDismissRequest={ explaining=null },title={ Text("Face matching details") },text={ Column(Modifier.heightIn(max=440.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            byId[id]?.let { AsyncImage(File(it.cropPath),"Face being checked",Modifier.size(96.dp)) }
            diagnostic?.let { detail ->
                Text(when(detail.status) {
                    FaceReview.Status.NO_EMBEDDING -> "Face detected, but no usable recognition data was extracted. Pose, face size or missing landmarks may have prevented matching. Rescan to retry."
                    FaceReview.Status.INVALID_EMBEDDING -> "Stored recognition data is invalid. Rescan to rebuild it."
                    FaceReview.Status.DIFFERENT_MODEL -> "This face was indexed with a different model. Rescan before comparing it."
                    FaceReview.Status.MANUAL_ONLY -> "An earlier correction keeps this face available for manual naming. Automatic suggestions are disabled for it."
                    FaceReview.Status.NO_REFERENCES -> "No compatible named reference is available. Unnamed faces can still group by similarity. People already assigned in this photo are excluded from suggestions."
                    FaceReview.Status.WEAK_MATCH -> "The closest named person falls below the matching threshold."
                    FaceReview.Status.AMBIGUOUS -> "Two named people are too close in similarity to suggest one safely."
                    FaceReview.Status.SUGGESTED -> "A possible named match is available for review."
                })
                detail.named?.nearest?.let { (person,score) -> Text("Closest named person: ${people.find { it.id==person }?.name ?: "Unknown"} · ${scoreText(score)}") }
                detail.named?.runnerUp?.let { (person,score) -> Text("Next named person: ${people.find { it.id==person }?.name ?: "Unknown"} · ${scoreText(score)}") }
                detail.nearestFaceId?.let { nearest ->
                    Text("Most similar face in another photo: ${scoreText(requireNotNull(detail.nearestFaceScore))}")
                    byId[nearest]?.let { face -> AsyncImage(File(face.cropPath),"Most similar face",Modifier.size(96.dp).clickable { explaining=null; onPhoto(face.photoId) }) }
                }
                Text("Similarity threshold ${scoreText(FaceMatching.MIN_SIMILARITY)}; required lead over another named person ${scoreText(FaceMatching.MIN_MARGIN)}. Scores measure similarity, not probability. Groups require every pair to pass; separate faces in one photo stay separate.",style=MaterialTheme.typography.bodySmall)
            } ?: Text("Checking local recognition data…")
        } },confirmButton={ TextButton(onClick={ explaining=null }) { Text("Close") } })
    }
}

private class PeopleFaceReviewInput(val faces: List<FaceRecord>)
private data class PeopleFaceReviewSnapshot(val input: PeopleFaceReviewInput,val result: FaceReview.Result)

private fun scoreText(value: Float)=String.format(java.util.Locale.ROOT,"%.3f",value)

package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.clickable
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import coil.compose.AsyncImage
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.people.FaceMatching
import com.myphotosanywhere.mpa.people.FaceReview
import com.myphotosanywhere.mpa.people.PeopleRepository
import java.io.File

@Composable
fun PeopleScreen(model: LibraryFeaturesModel,people: List<Person>,faces: List<FaceRecord>,enabled: Boolean,
    onPerson: (String)->Unit,onPhoto: (String)->Unit) {
    var naming by remember { mutableStateOf<List<String>>(emptyList()) }
    var name by remember { mutableStateOf("") }
    var deleting by remember { mutableStateOf(false) }
    var managing by remember { mutableStateOf<Person?>(null) }
    var mergeTarget by remember { mutableStateOf<Person?>(null) }
    var rescanning by remember { mutableStateOf(false) }
    var explaining by remember { mutableStateOf<String?>(null) }
    var selected by remember { mutableStateOf<Set<String>>(emptySet()) }
    var namingPerson by remember { mutableStateOf<Person?>(null) }
    val review by produceState(FaceReview.Result(emptyList(),emptyMap()),faces) {
        value=kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.Default) { FaceReview.analyze(faces,PeopleRepository.MODEL_VERSION) }
    }
    val byId=faces.associateBy { it.id }
    val pending=faces.filter { it.personId==null && people.any { person -> person.id==it.suggestedPersonId } }
    val pendingIds=pending.map { it.id }.toSet()
    val groups=review.groups.map { ids -> ids.filter { it !in pendingIds } }.filter { it.isNotEmpty() }
    fun startNaming(ids: List<String>,person: Person?=null) {
        naming=ids; selected=ids.toSet(); namingPerson=person; name=person?.name ?: ""
    }
    LazyColumn(Modifier.fillMaxSize().testTag("people-list"),contentPadding=PaddingValues(20.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
        item {
            Text("People",style=MaterialTheme.typography.headlineSmall)
            ScanActions(model)
            Text("Matching runs on this phone. Face crops and embeddings stay here; names and reviewed regions can be backed up as XMP if enabled in Storage. Matches require your review.")
            Row {
                Switch(checked=enabled,onCheckedChange={ model.updateSettings(model.settings.value.copy(faceIndexing=it)) })
                Text("On-device face indexing",Modifier.padding(12.dp))
            }
            if(faces.any { it.cropPath.isBlank() }) Text("Some restored regions need a phone scan to rebuild their previews. Legacy assignments without geometry remain for manual review.",style=MaterialTheme.typography.bodySmall)
            IndexingControls(model)
            if(faces.isNotEmpty()) {
                val unavailable=review.diagnostics.values.count { it.status in listOf(FaceReview.Status.NO_EMBEDDING,FaceReview.Status.INVALID_EMBEDDING,FaceReview.Status.DIFFERENT_MODEL) }
                if(unavailable>0) Text("$unavailable unassigned faces lack usable matching data. Open Why separate? to check them.")
                TextButton(enabled=enabled,onClick={ rescanning=true },modifier=Modifier.testTag("rescan-faces")) { Text("Rescan all faces") }
                Text("Scan now checks new and failed photos. Rescan all faces repeats detection while preserving names and corrections.",style=MaterialTheme.typography.bodySmall)
            }
            if(faces.isNotEmpty() || people.isNotEmpty()) TextButton(onClick={ deleting=true }) { Text("Delete people index") }
        }
        items(people,key={ it.id }) { person ->
            Card(Modifier.fillMaxWidth()) {
                Row(Modifier.padding(12.dp),horizontalArrangement=Arrangement.SpaceBetween) {
                    Column(Modifier.weight(1f).clickable { onPerson(person.id) }) {
                        Text(person.name,style=MaterialTheme.typography.titleMedium)
                        Text("${faces.filter { it.personId==person.id }.map { it.photoId }.distinct().size} photos")
                        val candidates=pending.filter { it.suggestedPersonId==person.id }
                        if(candidates.isNotEmpty()) TextButton(onClick={ startNaming(candidates.map { it.id },person) }) {
                            Text("Review ${candidates.size} possible matches")
                        }
                    }
                    TextButton(onClick={ managing=person; name=person.name; mergeTarget=null }) { Text("Manage") }
                }
            }
        }
        item { Text("Unassigned faces · ${faces.count { it.personId==null }}",style=MaterialTheme.typography.titleMedium) }
        items(groups,key={ it.first() }) { ids ->
            val group=ids.mapNotNull { byId[it] }
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(12.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
                    Text(if(group.size>1) "Possible group · ${group.size} faces" else "Unknown face")
                    Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) { group.take(4).forEach { face ->
                        AsyncImage(File(face.cropPath),"Detected face",Modifier.size(64.dp).clickable { onPhoto(face.photoId) })
                    } }
                    TextButton(onClick={ startNaming(ids) }) { Text(if(group.size>1) "Review and name group" else "Name this person") }
                    group.forEach { face ->
                        TextButton(onClick={ explaining=face.id },modifier=Modifier.testTag("face-why:${face.id}")) { Text("Why separate?") }
                    }
                }
            }
        }
        if(enabled && faces.isEmpty()) item { Text("Enable indexing of connected Drive sources in Storage, or download photos to scan them on this phone. Individual pet recognition is not yet included; cats and dogs are searchable object labels.") }
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
            item { OutlinedTextField(name,{ if(it.length<=100) name=it },readOnly=namingPerson!=null,label={ Text("Name") }) }
            items(people,key={ it.id }) { person -> TextButton(enabled=selected.isNotEmpty(),onClick={ model.assign(selected.toList(),person.name,person.id); naming=emptyList() }) { Text("Use ${person.name}") } }
        } },confirmButton={ Button(enabled=name.isNotBlank() && selected.isNotEmpty(),onClick={ model.assign(selected.toList(),name,namingPerson?.id); naming=emptyList() }) { Text("Confirm name") } },
        dismissButton={ TextButton(onClick={ naming=emptyList() }) { Text("Cancel") } })
    managing?.let { person -> AlertDialog(onDismissRequest={ managing=null },title={ Text("Manage ${person.name}") },
        text={ Column {
            OutlinedTextField(name,{ if(it.length<=100) name=it },label={ Text("Name") })
            Text("Merge into an existing person:")
            people.filter { it.id!=person.id }.forEach { other ->
                FilterChip(selected=mergeTarget?.id==other.id,onClick={ mergeTarget=other },label={ Text(other.name) })
            }
            mergeTarget?.let { target -> TextButton(onClick={ model.mergePeople(person.id,target.id); managing=null }) { Text("Confirm merge into ${target.name}") } }
            TextButton(onClick={ model.deletePerson(person.id); managing=null }) { Text("Remove name; leave faces unassigned") }
        } },confirmButton={ Button(enabled=name.isNotBlank(),onClick={ model.renamePerson(person,name); managing=null }) { Text("Save name") } },
        dismissButton={ TextButton(onClick={ managing=null }) { Text("Close") } }) }
    if(deleting) AlertDialog(onDismissRequest={ deleting=false },title={ Text("Delete people index?") },
        text={ Text("This removes all local face crops, embeddings, names, and corrections, and turns face indexing off. Your photos are preserved.") },
        confirmButton={ TextButton(onClick={ model.deletePeopleIndex(); deleting=false }) { Text("Delete index") } },
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

private fun scoreText(value: Float)=String.format(java.util.Locale.ROOT,"%.3f",value)

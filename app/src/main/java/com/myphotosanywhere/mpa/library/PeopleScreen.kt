package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import coil.compose.AsyncImage
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.people.FaceMatching
import java.io.File

@Composable
fun PeopleScreen(model: LibraryFeaturesModel,people: List<Person>,faces: List<FaceRecord>,enabled: Boolean,
    onPerson: (String)->Unit,onPhoto: (String)->Unit) {
    var naming by remember { mutableStateOf<List<String>>(emptyList()) }
    var name by remember { mutableStateOf("") }
    var deleting by remember { mutableStateOf(false) }
    var managing by remember { mutableStateOf<Person?>(null) }
    var mergeTarget by remember { mutableStateOf<Person?>(null) }
    val groups by produceState<List<List<String>>>(emptyList(),faces) { value=kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.Default) {
        val unknown=faces.filter { it.personId==null }
        val matched=FaceMatching.clusters(unknown.mapNotNull { f -> if(f.corrected) null else f.embedding?.let { f.id to FaceMatching.decode(it) } })
        matched + unknown.filter { it.embedding==null || it.corrected }.map { listOf(it.id) }
    } }
    LazyColumn(Modifier.fillMaxSize(),contentPadding=PaddingValues(20.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
        item {
            Text("People",style=MaterialTheme.typography.headlineSmall)
            Text("Matching runs on this phone. Face crops and embeddings stay here; names and reviewed regions can be backed up as XMP if enabled in Storage. Matches require your review.")
            Row {
                Switch(checked=enabled,onCheckedChange={ model.updateSettings(model.settings.value.copy(faceIndexing=it)) })
                Text("On-device face indexing",Modifier.padding(12.dp))
            }
            IndexingControls(model)
            if(faces.isNotEmpty() || people.isNotEmpty()) TextButton(onClick={ deleting=true }) { Text("Delete people index") }
        }
        items(people,key={ it.id }) { person ->
            Card(Modifier.fillMaxWidth()) {
                Row(Modifier.padding(12.dp),horizontalArrangement=Arrangement.SpaceBetween) {
                    Column(Modifier.weight(1f).clickable { onPerson(person.id) }) {
                        Text(person.name,style=MaterialTheme.typography.titleMedium)
                        Text("${faces.filter { it.personId==person.id }.map { it.photoId }.distinct().size} photos")
                    }
                    TextButton(onClick={ managing=person; name=person.name; mergeTarget=null }) { Text("Manage") }
                }
            }
        }
        item { Text("Unassigned faces · ${faces.count { it.personId==null }}",style=MaterialTheme.typography.titleMedium) }
        items(groups,key={ it.first() }) { ids ->
            val group=ids.mapNotNull { id -> faces.find { it.id==id } }
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(12.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
                    Text(if(group.size>1) "Possible group · ${group.size} faces" else "Unknown face")
                    Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) { group.take(4).forEach { face ->
                        AsyncImage(File(face.cropPath),"Detected face",Modifier.size(64.dp).clickable { onPhoto(face.photoId) })
                    } }
                    TextButton(onClick={ naming=ids; name="" }) { Text(if(group.size>1) "Review and name group" else "Name this person") }
                    group.forEach { face ->
                        val suggestion=people.find { it.id==face.suggestedPersonId }
                        if(suggestion!=null) Row {
                            TextButton(onClick={ model.acceptFace(face.id,suggestion) }) { Text("Confirm ${suggestion.name}") }
                            TextButton(onClick={ model.unassignFace(face.id) }) { Text("Wrong match") }
                        }
                    }
                }
            }
        }
        if(enabled && faces.isEmpty()) item { Text("Enable indexing of connected Drive sources in Storage, or download photos to scan them on this phone. Individual pet recognition is not yet included; cats and dogs are searchable object labels.") }
    }
    if(naming.isNotEmpty()) AlertDialog(onDismissRequest={ naming=emptyList() },title={ Text("Name these faces") },
        text={ Column {
            Text("Confirm that these faces belong to the same person. You can correct individual faces from photo details.")
            OutlinedTextField(name,{ if(it.length<=100) name=it },label={ Text("Name") })
            people.forEach { person -> TextButton(onClick={ model.assign(naming,person.name,person.id); naming=emptyList() }) { Text("Use ${person.name}") } }
        } },confirmButton={ Button(enabled=name.isNotBlank(),onClick={ model.assign(naming,name); naming=emptyList() }) { Text("Confirm name") } },
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
}

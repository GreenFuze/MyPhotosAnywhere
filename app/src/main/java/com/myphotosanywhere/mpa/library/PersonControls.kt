package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import com.myphotosanywhere.mpa.R
import com.myphotosanywhere.mpa.data.Person
import com.myphotosanywhere.mpa.management.GroupedMenu
import com.myphotosanywhere.mpa.management.MenuEntry
import com.myphotosanywhere.mpa.management.PreferenceChoice
import androidx.lifecycle.compose.collectAsStateWithLifecycle

/** Shared identity controls, regardless of whether a name began in Takeout or face review. */
@Composable
internal fun PersonManagementDialog(person: Person,people: List<Person>,model: LibraryFeaturesModel,onClose: ()->Unit) {
    val busy by model.busy.collectAsStateWithLifecycle()
    var page by rememberSaveable(person.id) { mutableStateOf("menu") }
    var name by rememberSaveable(person.id) { mutableStateOf(person.name) }
    var target by rememberSaveable(person.id) { mutableStateOf<String?>(null) }
    var search by rememberSaveable(person.id) { mutableStateOf("") }
    val survivor=people.firstOrNull { it.id==target && it.id!=person.id }
    AlertDialog(onDismissRequest=onClose,modifier=Modifier.testTag("person:management"),title={ Text(when(page) {
        "rename"->"Rename person";"merge"->"Merge people";"remove"->"Remove this person?";else->person.name
    }) },text={ LazyColumn(Modifier.heightIn(max=440.dp).testTag("person:choices"),verticalArrangement=Arrangement.spacedBy(12.dp)) {
        when(page) {
            "menu"->item { GroupedMenu(listOf(
                MenuEntry("Rename","Change the name across your library",R.drawable.ic_shell_edit,"person:rename") { page="rename" },
                MenuEntry("Merge people","Combine appearances and names into one person",R.drawable.ic_shell_people,"person:merge",people.any { it.id!=person.id }) { page="merge" },
                MenuEntry("Remove person","Keep photos and remove this person's associations",R.drawable.ic_action_trash,"person:remove") { page="remove" }
            )) }
            "rename"->item {
                OutlinedTextField(name,{ if(it.length<=200) name=it },label={ Text("Name") },modifier=Modifier.fillMaxWidth().testTag("person:name"))
                Text("Photos, face corrections and original imported metadata are preserved.",style=MaterialTheme.typography.bodySmall)
            }
            "merge"->{
                item {
                    Text("Choose the person to keep. ${person.name} and their appearances will use that name. Imported names continue to resolve to the same person.")
                    OutlinedTextField(search,{ if(it.length<=200) search=it },label={ Text("Find person") },modifier=Modifier.fillMaxWidth().testTag("person:find"))
                }
                items(people.filter { it.id!=person.id && it.name.contains(search,ignoreCase=true) },key={ it.id }) { other ->
                    PreferenceChoice(other.name,"",target==other.id,"person:merge-target:${other.id}") { target=other.id }
                }
            }
            "remove"->item { Text("This removes the name and its photo associations. Photos and original metadata are kept. The retained imported name will not recreate this person on the next import.") }
            else->check(page=="menu")
        }
    } },confirmButton={ when(page) {
        "rename"->Button(enabled=!busy && name.isNotBlank(),onClick={ model.renamePerson(person,name);onClose() },modifier=Modifier.testTag("person:save")) { Text("Save name") }
        "merge"->Button(enabled=!busy && survivor!=null,onClick={ survivor?.let { model.mergePeople(person.id,it.id);onClose() } },modifier=Modifier.testTag("person:confirm-merge")) { Text("Merge") }
        "remove"->TextButton(enabled=!busy,onClick={ model.deletePerson(person.id);onClose() },modifier=Modifier.testTag("person:confirm-remove")) { Text("Remove person") }
        else->TextButton(onClick=onClose) { Text("Done") }
    } },dismissButton={ if(page!="menu") TextButton(onClick={ page="menu" }) { Text("Back") } else TextButton(onClick=onClose) { Text("Close") } })
}

/** Scrollable chooser avoids unbounded face menus on large libraries and at large font sizes. */
@Composable
internal fun FacePersonDialog(face: com.myphotosanywhere.mpa.data.FaceRecord,people: List<Person>,model: LibraryFeaturesModel,onClose: ()->Unit) {
    var page by rememberSaveable(face.id) { mutableStateOf("menu") }
    var name by rememberSaveable(face.id) { mutableStateOf("") }
    var search by rememberSaveable(face.id) { mutableStateOf("") }
    AlertDialog(onDismissRequest=onClose,modifier=Modifier.testTag("face:person-dialog"),title={ Text(when(page) {
        "existing"->"Choose person";"new"->"Name this person";else->"Correct this face"
    }) },text={ LazyColumn(Modifier.heightIn(max=440.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
        item { coil.compose.AsyncImage(previewSource(face.cropPath),"Selected face",Modifier.size(100.dp)) }
        when(page) {
            "menu"->item { GroupedMenu(listOf(
                MenuEntry("Choose person","Use a person already in your library",R.drawable.ic_shell_people,"face:choose",people.isNotEmpty()) { page="existing" },
                MenuEntry("New person","Add a name for this face",R.drawable.ic_shell_add,"face:new") { page="new" },
                MenuEntry("Not this person","Keep unassigned and reject automatic suggestions",R.drawable.ic_search_clear,"face:unassign") { model.unassignFace(face.id);onClose() }
            )) }
            "existing"->{
                item { OutlinedTextField(search,{ if(it.length<=200) search=it },label={ Text("Find person") },modifier=Modifier.fillMaxWidth()) }
                items(people.filter { it.name.contains(search,ignoreCase=true) },key={ it.id }) { person ->
                    GroupedMenu(listOf(MenuEntry(person.name,"",R.drawable.ic_shell_people,"face:assign:${person.id}") { model.assign(listOf(face.id),person.name,person.id);onClose() }))
                }
            }
            "new"->item { OutlinedTextField(name,{ if(it.length<=200) name=it },label={ Text("Person name") },modifier=Modifier.fillMaxWidth()) }
            else->check(page=="menu")
        }
    } },confirmButton={ if(page=="new") Button(enabled=name.isNotBlank(),onClick={ model.assign(listOf(face.id),name);onClose() }) { Text("Create person") }
        else TextButton(onClick=onClose) { Text("Done") }
    },dismissButton={ if(page!="menu") TextButton(onClick={ page="menu" }) { Text("Back") } else TextButton(onClick=onClose) { Text("Close") } })
}

package com.myphotosanywhere.mpa.drive

import android.accounts.AccountManager
import android.app.Activity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.ui.Modifier
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.produceState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.Alignment
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.google.android.gms.common.AccountPicker
import com.myphotosanywhere.mpa.data.Photo
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.ui.semantics.heading
import com.myphotosanywhere.mpa.management.GroupedMenu
import com.myphotosanywhere.mpa.management.MenuEntry
import com.myphotosanywhere.mpa.management.PreferenceCard
import com.myphotosanywhere.mpa.management.PreferenceToggle
import com.myphotosanywhere.mpa.R
import com.myphotosanywhere.mpa.ui.ShellIcon
import com.myphotosanywhere.mpa.ui.MotionVisibility
import com.myphotosanywhere.mpa.ui.MotionEdge

@Composable
fun DriveStorageScreen(photos: List<Photo>,onActivity: ()->Unit,onImports: ()->Unit,onSettings: ()->Unit,model: DriveViewModel = viewModel(),onFreeSpace: (()->Unit)?=null) {
    val active by model.connection.collectAsStateWithLifecycle()
    val sources by model.sources.collectAsStateWithLifecycle()
    val backups by model.backups.collectAsStateWithLifecycle()
    val browser by model.browser.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val message by model.message.collectAsStateWithLifecycle()
    var disconnecting by rememberSaveable { mutableStateOf(false) }
    var managing by rememberSaveable { mutableStateOf(false) }
    BackHandler(managing && !disconnecting && browser==null) { managing=false }
    var modeName by rememberSaveable { mutableStateOf(DriveMode.APP_FILES.name) }
    var folderName by rememberSaveable { mutableStateOf("MyPhotosAnywhere") }
    var transferSettings by rememberSaveable { mutableStateOf<String?>(null) }
    var sourceHelp by rememberSaveable { mutableStateOf(false) }
    val resolution = rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) {
        model.authorizationResult(it.resultCode,it.data)
    }
    val accountPicker = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        val email = it.data?.getStringExtra(AccountManager.KEY_ACCOUNT_NAME)
        if (it.resultCode == Activity.RESULT_OK && !email.isNullOrBlank()) {
            model.authorize(email, DriveMode.valueOf(modeName)) { pending ->
                resolution.launch(IntentSenderRequest.Builder(pending.intentSender).build())
            }
        }
    }
    fun connect() {
        try {
            accountPicker.launch(AccountPicker.newChooseAccountIntent(AccountPicker.AccountChooserOptions.Builder()
                .setAllowableAccountsTypes(listOf("com.google")).build()))
        } catch (_: android.content.ActivityNotFoundException) { model.report("Google account selection is unavailable. Install or update Google Play services.") }
        catch (_: SecurityException) { model.report("Android blocked Google account selection. Check this device’s account restrictions.") }
    }
    val current = active
    val currentBackups = backups.filter { it.bindingKey == current?.binding()?.key }
    val problemIds=currentBackups.filter { it.errorMessage!=null }.take(5).map { it.photoId }
    val problemNames by produceState(emptyMap<String,String>(),problemIds) {
        try { value=model.photoNames(problemIds) }
        catch(error: android.database.sqlite.SQLiteException) { model.report(error.message ?: "Cannot read backup filenames. Transfer errors remain available.") }
    }
    Column(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.surfaceContainerHigh).verticalScroll(rememberScrollState()).padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)) {
        Text("MyPhotosAnywhere",style=MaterialTheme.typography.headlineLarge,modifier=Modifier.padding(vertical=16.dp).semantics { heading() })
        Text("All connected folders contribute to your photo library.",style=MaterialTheme.typography.bodyLarge)
        GroupedMenu(listOf(
            MenuEntry("Activity","Transfers, progress and recovery",R.drawable.ic_shell_activity,"management:Activity",open=onActivity),
            MenuEntry("Import photos","Google Photos and Takeout",R.drawable.ic_shell_add,"management:Import photos",open=onImports),
            MenuEntry("Settings","Preferences, storage and privacy",R.drawable.ic_action_settings,"management:Settings",open=onSettings))+
            listOfNotNull(onFreeSpace?.let { MenuEntry("Free up space","Clear verified downloaded originals",R.drawable.ic_shell_offline,"management:Free up space",open=it) }))
        com.myphotosanywhere.mpa.device.DevicePhotoAccessMenu()
        if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        message?.let {
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(12.dp)) {
                    Text(it)
                    GoogleSetupDetails()
                    TextButton(onClick = model::clearMessage) { Text("Dismiss") }
                }
            }
        }
        if(sources.isNotEmpty()) {
            Text("Connected Drive sources",style=MaterialTheme.typography.titleMedium)
            sources.forEach { source ->
                PreferenceCard(source.folderName,source.email+"\n"+when {
                    source.backupEnabled -> "Default for new imports and backups"
                    sources.size==1 -> "Default for new imports (only connected folder)"
                    else -> "Connected Drive folder"
                },"source:manage:${source.key}") { if(!busy) { model.selectSource(source);managing=true } }
            }
            GroupedMenu(listOf(MenuEntry("Connect another account","Add a Google Drive account or folder",R.drawable.ic_shell_add,"source:add",enabled=!busy) { managing=true;model.addAccount() }))
        }
        MotionVisibility(managing || sources.isEmpty(),MotionEdge.TOP) {
        Column(verticalArrangement=Arrangement.spacedBy(14.dp)) {
        if (current == null) {
            Text("Connect your Google Drive", style = MaterialTheme.typography.titleMedium)
            DriveMode.entries.forEach { mode ->
                Row(Modifier.fillMaxWidth().heightIn(min=48.dp).selectable(selected=modeName==mode.name,enabled=!busy,role=Role.RadioButton,onClick={ modeName=mode.name }),
                    verticalAlignment = Alignment.CenterVertically) {
                    RadioButton(selected = modeName == mode.name, onClick = null, enabled = !busy)
                    Text(mode.title)
                }
            }
            Text(DriveMode.valueOf(modeName).explanation, style = MaterialTheme.typography.bodySmall)
            Button(enabled = !busy, onClick = { connect() }) { Text("Choose Google account") }
            Text("Sign in with the Google account that stores your photos.",
                style = MaterialTheme.typography.bodySmall)
        } else {
            Card(Modifier.fillMaxWidth(),shape=RoundedCornerShape(24.dp)) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(current.email, style = MaterialTheme.typography.titleMedium)
                    Text(current.folderName ?: "No photo folder selected")
                    Text(DriveMode.valueOf(current.mode).title, style = MaterialTheme.typography.bodySmall)
                    OutlinedButton(enabled = !busy, onClick = { model.browse() },modifier=Modifier.heightIn(min=48.dp)) {
                        ShellIcon(R.drawable.ic_shell_add);Text("Add Drive folder",Modifier.padding(start=8.dp))
                    }
                    if (current.folderId != null) {
                        OutlinedButton(enabled = !busy, onClick = model::browseSelected,modifier=Modifier.heightIn(min=48.dp)) {
                            ShellIcon(R.drawable.ic_shell_photos);Text("Browse Drive photos",Modifier.padding(start=8.dp))
                        }
                        Button(enabled = !busy, onClick = if (current.backupEnabled) model::pauseBackup else model::enableBackup) {
                            Text(if (current.backupEnabled) "Pause backup" else "Use as default import and backup destination")
                        }
                    }
                    val settingsKey=current.binding()?.key ?: current.accountId
                    val expanded=transferSettings==settingsKey
                    val expandedState=stringResource(if(expanded) R.string.ux_expanded else R.string.ux_collapsed)
                    OutlinedButton(enabled=!busy,onClick={ transferSettings=if(expanded) null else settingsKey },
                        modifier=Modifier.heightIn(min=48.dp).testTag("source:transfer-settings").semantics { stateDescription=expandedState }) {
                        ShellIcon(R.drawable.ic_action_settings);Text(stringResource(R.string.ux_transfer_settings),Modifier.padding(start=8.dp))
                    }
                    MotionVisibility(expanded,MotionEdge.TOP) {
                        Column(verticalArrangement=Arrangement.spacedBy(8.dp)) {
                            PreferenceToggle("Transfer on unmetered networks only","Applies to transfers for this source",current.unmeteredOnly,{ if(!busy) model.unmeteredOnly(it) })
                            Text("${currentBackups.count { it.state == "COMPLETE" }} backed up · ${currentBackups.count { it.state != "COMPLETE" }} pending")
                            if(current.backupEnabled) TextButton(enabled=!busy,onClick=model::retryBackup,modifier=Modifier.heightIn(min=48.dp)) { Text("Retry pending backups") }
                        }
                    }
                }
            }
            val problems = currentBackups.filter { it.errorMessage != null }.take(5)
            problems.forEach { backup ->
                Text("${problemNames[backup.photoId] ?: "Photo"}: ${backup.errorMessage}",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                TextButton(enabled = !busy, onClick = {
                    modeName = current.mode
                    model.authorize(current.email, DriveMode.valueOf(current.mode)) { pending ->
                        resolution.launch(IntentSenderRequest.Builder(pending.intentSender).build())
                    }
                }) { Text("Reconnect") }
                TextButton(enabled = !busy, onClick = { disconnecting=true },modifier=Modifier.testTag("source:remove")) { Text("Remove this source") }
            }
        }
        }
        }
        TextButton(onClick={ sourceHelp=!sourceHelp },modifier=Modifier.heightIn(min=48.dp).testTag("source:help")) {
            ShellIcon(R.drawable.ic_action_info);Text(stringResource(R.string.ux_source_help),Modifier.padding(start=8.dp))
        }
        MotionVisibility(sourceHelp,MotionEdge.TOP) {
            Column(verticalArrangement=Arrangement.spacedBy(8.dp)) {
                Text("Removing a source stops its transfers. Other sources continue syncing; local photos and people are preserved. Google permissions can be revoked in your account settings.",style=MaterialTheme.typography.bodySmall)
                Text("Keep your source copies. Uninstalling MPA removes this device's private library.",style=MaterialTheme.typography.bodySmall)
                GoogleSetupDetails()
            }
        }
    }
    if(disconnecting && current!=null) AlertDialog(onDismissRequest={ disconnecting=false },title={ Text("Disconnect ${current.folderName ?: current.email}?") },text={
        Text("This stops access and transfers for ${current.email} / ${current.folderName ?: "this source"}. Its remote-only entries leave the connected gallery; existing local photos, annotations and saved import jobs are preserved. Jobs keep this destination and may require reconnection. Drive originals are not deleted. Other sources continue working.")
    },confirmButton={ TextButton(enabled=!busy,onClick={ disconnecting=false;model.disconnect(current.binding()?.key) }) { Text("Disconnect source") } },dismissButton={ TextButton(onClick={ disconnecting=false }) { Text("Keep source") } })
    val listing = browser
    if (listing != null) {
        AlertDialog(onDismissRequest = model::closeBrowser, title = { Text(listing.parentName) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
                    if (listing.parentId != "root") TextButton(enabled = !busy, onClick = model::back) { Text("Parent folder") }
                    LazyColumn(Modifier.heightIn(max = 260.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        items(listing.folders, key = { "folder:${it.id}" }) { folder ->
                            OutlinedButton(enabled = !busy, onClick = { model.browse(folder) }, modifier = Modifier.fillMaxWidth()) {
                                Text(folder.name)
                            }
                        }
                        if (listing.nextFolders != null) item {
                            TextButton(enabled = !busy, onClick = model::moreFolders) { Text("More folders") }
                        }
                        if (listing.parentId == current?.folderId) {
                            items(listing.images, key = { "image:${it.id}" }) { image ->
                                Column {
                                    Text(image.name, style = MaterialTheme.typography.bodySmall)
                                    TextButton(enabled = !busy, onClick = { model.importImage(image) }) { Text("Make available offline") }
                                }
                            }
                            if (listing.nextImages != null) item {
                                TextButton(enabled = !busy, onClick = model::moreImages) { Text("More photos") }
                            }
                        }
                    }
                    OutlinedTextField(value = folderName, onValueChange = { if (it.length <= 200) folderName = it },
                        label = { Text("New folder name") }, enabled = !busy, singleLine = true)
                    TextButton(enabled = !busy && folderName.isNotBlank(), onClick = { model.createFolder(folderName) }) { Text("Create folder here") }
                    if (listing.parentId != "root") Text("Adding a folder preserves other sources. Backups use one explicitly chosen default destination.",
                        style = MaterialTheme.typography.bodySmall)
                }
            }, confirmButton = {
                Button(enabled = !busy && listing.parentId != "root", onClick = model::useFolder) { Text("Use this folder") }
            }, dismissButton = { TextButton(enabled = !busy, onClick = model::closeBrowser) { Text("Close") } })
    }
}

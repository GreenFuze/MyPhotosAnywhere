package com.myphotosanywhere.mpa.drive

import android.accounts.AccountManager
import android.app.Activity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
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
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.google.android.gms.common.AccountPicker
import com.myphotosanywhere.mpa.data.Photo

@Composable
fun DriveStorageScreen(photos: List<Photo>, model: DriveViewModel = viewModel()) {
    val active by model.connection.collectAsStateWithLifecycle()
    val sources by model.sources.collectAsStateWithLifecycle()
    val backups by model.backups.collectAsStateWithLifecycle()
    val browser by model.browser.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val message by model.message.collectAsStateWithLifecycle()
    var modeName by rememberSaveable { mutableStateOf(DriveMode.APP_FILES.name) }
    var folderName by rememberSaveable { mutableStateOf("MyPhotosAnywhere") }
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
        } catch (_: Exception) { model.report("Google account selection is unavailable. Install or update Google Play services.") }
    }
    val current = active
    val currentBackups = backups.filter { it.bindingKey == current?.binding()?.key }
    Column(Modifier.verticalScroll(rememberScrollState()).padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Text("Storage & backup", style = MaterialTheme.typography.headlineSmall)
        com.myphotosanywhere.mpa.library.ScanActions(androidx.lifecycle.viewmodel.compose.viewModel())
        com.myphotosanywhere.mpa.library.DriveSyncActions()
        Text("${photos.size} photos · %.1f MiB saved on this device".format(photos.filter { it.originalPath.isNotBlank() }.sumOf { it.bytes } / (1024.0 * 1024)))
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
                Card(Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(12.dp)) {
                        Text(source.folderName)
                        Text(source.email,style=MaterialTheme.typography.bodySmall)
                        if(source.backupEnabled) Text("Default backup destination",style=MaterialTheme.typography.labelMedium)
                        TextButton(enabled=!busy,onClick={ model.selectSource(source) }) { Text("Manage source") }
                    }
                }
            }
            Text("People and recognition are shared across every connected source. Transfers keep their own account and folder.",style=MaterialTheme.typography.bodySmall)
            TextButton(enabled=!busy,onClick=model::addAccount) { Text("Add another Google account") }
        }
        if (current == null) {
            Text("Connect your Google Drive", style = MaterialTheme.typography.titleMedium)
            DriveMode.entries.forEach { mode ->
                Row(Modifier.fillMaxWidth().clickable(enabled = !busy) { modeName = mode.name },
                    verticalAlignment = Alignment.CenterVertically) {
                    RadioButton(selected = modeName == mode.name, onClick = { modeName = mode.name }, enabled = !busy)
                    Text(mode.title)
                }
            }
            Text(DriveMode.valueOf(modeName).explanation, style = MaterialTheme.typography.bodySmall)
            Button(enabled = !busy, onClick = { connect() }) { Text("Choose Google account") }
            Text("Sign in with the Google account that stores your photos.",
                style = MaterialTheme.typography.bodySmall)
        } else {
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(current.email, style = MaterialTheme.typography.titleMedium)
                    Text(current.folderName ?: "No photo folder selected")
                    Text(DriveMode.valueOf(current.mode).title, style = MaterialTheme.typography.bodySmall)
                    OutlinedButton(enabled = !busy, onClick = { model.browse() }) { Text("Add Drive folder") }
                    if (current.folderId != null) {
                        OutlinedButton(enabled = !busy, onClick = model::browseSelected) { Text("Browse Drive photos") }
                        Button(enabled = !busy, onClick = if (current.backupEnabled) model::pauseBackup else model::enableBackup) {
                            Text(if (current.backupEnabled) "Pause backup" else "Use as default backup destination")
                        }
                    }
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Switch(checked = current.unmeteredOnly, enabled = !busy, onCheckedChange = model::unmeteredOnly)
                        Text("Transfer on unmetered networks only", style = MaterialTheme.typography.bodySmall)
                    }
                    Text("${currentBackups.count { it.state == "COMPLETE" }} backed up · ${currentBackups.count { it.state != "COMPLETE" }} pending")
                    if (current.backupEnabled) TextButton(enabled = !busy, onClick = model::retryBackup) { Text("Retry pending backups") }
                }
            }
            val problems = currentBackups.filter { it.errorMessage != null }.take(5)
            problems.forEach { backup ->
                Text("${photos.find { it.id == backup.photoId }?.displayName ?: "Photo"}: ${backup.errorMessage}",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                TextButton(enabled = !busy, onClick = {
                    modeName = current.mode
                    model.authorize(current.email, DriveMode.valueOf(current.mode)) { pending ->
                        resolution.launch(IntentSenderRequest.Builder(pending.intentSender).build())
                    }
                }) { Text("Reconnect") }
                TextButton(enabled = !busy, onClick = model::disconnect) { Text("Remove this source") }
            }
        }
        com.myphotosanywhere.mpa.library.StorageControls(photos)
        Text("Removing a source stops its transfers. Other sources continue syncing; local photos and people are preserved. Google permissions can be revoked in your account settings.", style = MaterialTheme.typography.bodySmall)
        Text("Keep your source copies. Uninstalling MPA removes this device's private library.", style = MaterialTheme.typography.bodySmall)
        GoogleSetupDetails()
    }
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

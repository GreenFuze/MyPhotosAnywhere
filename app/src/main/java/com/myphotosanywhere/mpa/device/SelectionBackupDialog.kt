package com.myphotosanywhere.mpa.device

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.myphotosanywhere.mpa.library.LibraryFeaturesModel
import com.myphotosanywhere.mpa.management.PreferenceChoice
import com.myphotosanywhere.mpa.selection.*

@Composable
internal fun SelectionBackupDialog(selection: SelectionViewModel,features: LibraryFeaturesModel,onClose: ()->Unit,onSources: ()->Unit) {
    val sources by features.sources.collectAsStateWithLifecycle()
    var chosen by rememberSaveable { mutableStateOf<String?>(null) }
    LaunchedEffect(sources) { if(chosen==null) chosen=sources.firstOrNull { it.backupEnabled }?.key ?: sources.singleOrNull()?.key }
    AlertDialog(onDismissRequest=onClose,modifier=Modifier.testTag("selection:backup-dialog"),title={ Text("Back up selected photos") },
        text={ LazyColumn(Modifier.heightIn(max=420.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
            item { Text("Choose a folder for new backup jobs. Saved jobs keep their captured accounts and folders. Photos already verified in Drive are kept. Camera originals stay on this device.") }
            item { BackupMetadataNotice() }
            if(sources.isEmpty()) item {
                Text("Connect a writable Google Drive folder first.")
                TextButton(onClick={ onClose();onSources() },modifier=Modifier.heightIn(min=48.dp)) { Text("Open Sources") }
            }
            sources.forEach { source -> item(key=source.key) {
                PreferenceChoice(source.folderName,source.email,chosen==source.key,"selection:backup-source:${source.key}") { chosen=source.key }
            } }
        } },confirmButton={ Button(enabled=sources.any { it.key==chosen },onClick={ chosen?.let {
            onClose();selection.execute(BulkAction(BulkKind.BACKUP,bindingKey=it))
        } },modifier=Modifier.testTag("selection:backup-confirm")) { Text("Back up") } },
        dismissButton={ TextButton(onClick=onClose) { Text("Cancel") } })
}

/** One item's durable transfer status; does not subscribe the gallery to progress ticks. */
@Composable
internal fun DeviceBackupStatus(photoId: String,model: LibraryFeaturesModel) {
    val jobs by produceState(emptyList<com.myphotosanywhere.mpa.drive.DriveBackup>(),photoId) {
        model.app.database.drive().observeBackupsForPhoto(photoId).collect { value=it }
    }
    val job=jobs.filter { it.state!="COMPLETE" }.sortedBy { it.id }.firstOrNull()
    job?.let { task ->
        val text=when(task.state) {
            "AUTH_REQUIRED"->"Backup needs Google authorization. Reconnect its account in Sources."
            "FAILED"->task.errorMessage ?: "Backup needs retry. The device original is preserved."
            "RETRYING"->"Backup interrupted. Retrying the saved transfer."
            "PAUSED"->task.errorMessage ?: "Backup paused. Resume when ready."
            "UPLOADING"->"Backing up · ${task.uploadedBytes} bytes sent"
            "METADATA_PENDING"->"Original verified in Drive · backing up photo information"
            else->"Backup queued"
        }
        Text(text,style=MaterialTheme.typography.bodySmall,modifier=Modifier.testTag("device:backup-status"))
        if(task.state in listOf("FAILED","AUTH_REQUIRED","PAUSED")) TextButton(onClick={
            model.reviewDeviceBackup(photoId)
        },modifier=Modifier.heightIn(min=48.dp).testTag("device:backup-retry")) { Text("Review backup") }
    }
}

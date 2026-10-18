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

internal data class DeviceBackupRequest(val photoId: String,val name: String,val capturedBinding: String?,val capturedEmail: String?)

@Composable
internal fun DeviceBackupDialog(model: LibraryFeaturesModel,onSources: ()->Unit) {
    val request by model.deviceBackupRequest.collectAsStateWithLifecycle()
    val sources by model.sources.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    request?.let { item ->
        var chosen by rememberSaveable(item.photoId) { mutableStateOf(item.capturedBinding) }
        LaunchedEffect(sources,item.photoId) {
            if(chosen==null) chosen=sources.firstOrNull { it.backupEnabled }?.key ?: sources.singleOrNull()?.key
        }
        val options=if(item.capturedBinding==null) sources else sources.filter { it.key==item.capturedBinding }
        AlertDialog(onDismissRequest={ if(!busy) model.dismissDeviceBackup() },modifier=Modifier.testTag("device:backup-dialog"),
            title={ Text(if(item.capturedBinding==null) "Back up to Google Drive" else "Resume this backup") },
            text={ LazyColumn(Modifier.heightIn(max=420.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
                item { Text(item.name);Text("Only this photo is queued. Its camera original is kept. Other device photos are not uploaded automatically.") }
                item { BackupMetadataNotice() }
                if(item.capturedBinding!=null) item { Text("This saved job keeps its captured account and folder (${item.capturedEmail}).") }
                if(options.isEmpty()) item {
                    Text(if(item.capturedBinding==null) "Connect a writable Drive folder to choose a destination." else "Reconnect the captured account and folder to resume this job.")
                    TextButton(onClick={ model.dismissDeviceBackup();onSources() },modifier=Modifier.heightIn(min=48.dp).testTag("device:backup-sources")) { Text("Open Sources") }
                }
                options.forEach { source -> item(key=source.key) {
                    PreferenceChoice(source.folderName,source.email,chosen==source.key,"device:backup-source:${source.key}",enabled=!busy) { chosen=source.key }
                } }
            } },confirmButton={ Button(enabled=!busy && options.any { it.key==chosen },onClick={ chosen?.let { model.backupDevicePhoto(item.photoId,it) } },modifier=Modifier.testTag("device:backup-confirm")) {
                Text(if(item.capturedBinding==null) "Back up" else "Resume backup")
            } },dismissButton={ TextButton(enabled=!busy,onClick=model::dismissDeviceBackup) { Text("Cancel") } })
    }
}

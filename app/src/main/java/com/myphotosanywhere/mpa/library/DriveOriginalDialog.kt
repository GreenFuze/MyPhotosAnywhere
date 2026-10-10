package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import coil.compose.AsyncImage
import com.myphotosanywhere.mpa.drive.CloudPhoto
import java.io.File

/** Open immediately; show a preview and transfer state until the verified original is ready. */
@Composable
fun DriveOriginalDialog(row: CloudPhoto?,model: LibraryFeaturesModel,onClose: ()->Unit,onReady: (String)->Unit) {
    val tasks by model.downloads.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val task=tasks.find { it.key==row?.key }
    val message by model.message.collectAsStateWithLifecycle()
    Dialog(onDismissRequest=onClose,properties=DialogProperties(usePlatformDefaultWidth=false)) {
        Surface(Modifier.fillMaxSize()) {
            Column(Modifier.fillMaxSize().safeDrawingPadding().padding(16.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
                TextButton(onClick=onClose) { Text("Close") }
                Text(row?.name ?: "Drive photo",style=MaterialTheme.typography.titleMedium)
                AsyncImage(row?.let { File(it.thumbnailPath) },"Drive preview",Modifier.fillMaxWidth().weight(1f),contentScale=ContentScale.Fit)
                if(row==null || row.removed) Text("This photo is no longer available in its Drive source.")
                else {
                    if(task?.state in listOf("FAILED","AUTH_REQUIRED")) {
                        Text(task?.error ?: "Original download failed.",color=MaterialTheme.colorScheme.error)
                        Button(enabled=!busy,onClick={ model.download(row,retry=true) }) { Text("Retry original") }
                    } else {
                        LinearProgressIndicator(Modifier.fillMaxWidth())
                        Text(if(task?.state=="DOWNLOADING") "Downloading and verifying original…" else "Waiting for original download…")
                        Text("Preview quality until the original is ready. Your Drive source’s network preference applies; connect to Wi-Fi if unmetered transfers are enabled.",style=MaterialTheme.typography.bodySmall)
                        if(task==null) { message?.let { Text(it,color=MaterialTheme.colorScheme.error) }
                            TextButton(enabled=!busy,onClick={ model.download(row,retry=true) }) { Text("Download original") } }
                    }
                }
            }
        }
    }
    val photos by model.app.repository.photos.collectAsStateWithLifecycle(initialValue=emptyList())
    val ready=photos.find { it.id==row?.photoId && it.originalPath.isNotBlank() && File(it.originalPath).isFile }
    LaunchedEffect(ready?.id,ready?.originalPath,row?.removed) { if(ready!=null && row?.removed==false) onReady(ready.id) }
}

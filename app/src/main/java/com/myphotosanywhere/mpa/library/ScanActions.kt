package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle

/** Primary actions stay above the account list and schedule settings on small screens. */
@Composable
fun ScanActions(model: LibraryFeaturesModel) {
    val settings by model.settings.collectAsStateWithLifecycle()
    val progress by model.indexProgress.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    var consent by remember { mutableStateOf(false) }
    var faces by remember { mutableStateOf(true) }
    var objects by remember { mutableStateOf(true) }
    var cloud by remember { mutableStateOf(true) }
    Column(verticalArrangement=Arrangement.spacedBy(4.dp)) {
        Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            Button(modifier=Modifier.testTag("scan-now"),enabled=!busy,onClick={
                if(settings.faceIndexing || settings.objectIndexing) model.scanFaces() else consent=true
            }) { Text(if(settings.indexingPaused && (settings.faceIndexing || settings.objectIndexing)) "Resume / scan now" else "Scan now") }
            TextButton(onClick=model::pauseIndexing) { Text("Pause scans") }
        }
        Text(if(!settings.faceIndexing && !settings.objectIndexing) "Choose people and/or object detection when you start."
            else "${progress.status} · ${progress.done}/${progress.total} this batch",style=MaterialTheme.typography.bodySmall)
    }
    if(consent) AlertDialog(onDismissRequest={ consent=false },title={ Text("Enable on-device scanning") },
        text={ Column {
            Text("Photos are analysed on your phone. Face crops and recognition data stay here. Drive originals are downloaded using each source’s network setting. You can pause at any time.")
            Row { Checkbox(faces,{ faces=it }); Text("People and faces",Modifier.padding(top=12.dp)) }
            Row { Checkbox(objects,{ objects=it }); Text("Objects, scenes, cats and dogs",Modifier.padding(top=12.dp)) }
            Row { Checkbox(cloud,{ cloud=it }); Text("Include all connected Drive sources",Modifier.padding(top=12.dp)) }
        } },confirmButton={ Button(enabled=faces || objects,onClick={
            model.updateSettings(settings.copy(faceIndexing=faces,objectIndexing=objects,indexCloud=cloud,indexingPaused=false))
            model.scanFaces();consent=false
        }) { Text("Start scan") } },dismissButton={ TextButton(onClick={ consent=false }) { Text("Cancel") } })
}

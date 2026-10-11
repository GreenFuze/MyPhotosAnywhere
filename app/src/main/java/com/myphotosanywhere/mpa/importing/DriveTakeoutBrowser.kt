package com.myphotosanywhere.mpa.importing

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import kotlinx.coroutines.launch
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.myphotosanywhere.mpa.drive.DriveGateway

@Composable
fun DriveTakeoutBrowser(model: ImportModel, existing: String?, close: ()->Unit) {
    val sources by model.sources.collectAsState()
    var source by remember { mutableStateOf(sources.singleOrNull()?.key) }
    var folders by remember { mutableStateOf(emptyList<Pair<String,String>>()) }
    var page by remember { mutableStateOf<String?>(null) }
    var listing by remember { mutableStateOf<com.myphotosanywhere.mpa.drive.DrivePage?>(null) }
    var loading by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val scope=rememberCoroutineScope()
    LaunchedEffect(source,folders,page) {
        listing=null; error=null
        source?.let {
            loading=true
            try { listing=model.app.imports.driveArchives(it,folders.lastOrNull()?.first,page) }
            catch(e: kotlinx.coroutines.CancellationException) { throw e }
            catch(e: Exception) { error=e.message ?: "Cannot list Drive ZIPs. Reconnect this source." }
            finally { loading=false }
        }
    }
    AlertDialog(onDismissRequest=close,title={ Text("Takeout ZIP in Drive") },text={
        Column(Modifier.fillMaxWidth().heightIn(max=440.dp).verticalScroll(rememberScrollState())) {
            Text("Select a connected folder containing your Takeout export. Reads ZIP ranges without saving the whole archive. Individual photos are uploaded to the import destination; the ZIP stays in Drive.")
            if(source==null) sources.forEach { candidate ->
                TextButton(onClick={ source=candidate.key }) { Text("${candidate.folderName} (${candidate.email})") }
            } else {
                Text(sources.find { it.key==source }?.folderName.orEmpty()+folders.joinToString("") { " / ${it.second}" })
                TextButton(enabled=!loading,onClick={ if(folders.isEmpty()) source=null else folders=folders.dropLast(1); page=null }) { Text("Back") }
                if(loading) LinearProgressIndicator(Modifier.fillMaxWidth())
                listing?.files?.forEach { file ->
                    if(file.mimeType==DriveGateway.FOLDER_MIME) TextButton(enabled=!loading,onClick={ folders=folders+(file.id to file.name); page=null }) { Text("Folder: ${file.name}") }
                    else if(file.name.endsWith(".zip",true)) TextButton(enabled=!loading,onClick={
                        loading=true; error=null
                        scope.launch {
                            try { model.app.imports.addDriveTakeout(requireNotNull(source),file.id,existing); close() }
                            catch(e: kotlinx.coroutines.CancellationException) { throw e }
                            catch(e: Exception) { error=e.message ?: "Drive ZIP import failed. Source preserved." }
                            finally { loading=false }
                        }
                    }) { Text(file.name) }
                }
                if(listing?.files?.isEmpty()==true) Text("No ZIPs or subfolders here. Connect your Takeout export folder in Storage first.")
                listing?.nextPage?.let { next -> TextButton(enabled=!loading,onClick={ page=next }) { Text("Next page") } }
            }
            error?.let { Text(it,color=MaterialTheme.colorScheme.error) }
        }
    },confirmButton={ TextButton(enabled=!loading,onClick=close) { Text("Close") } })
}

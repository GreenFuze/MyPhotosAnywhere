package com.myphotosanywhere.mpa.device

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.myphotosanywhere.mpa.R
import com.myphotosanywhere.mpa.library.*
import com.myphotosanywhere.mpa.timeline.TimelineQuery
import kotlinx.coroutines.CancellationException

@Composable
internal fun DeviceFoldersScreen(model: LibraryFeaturesModel,state: LazyGridState,onFolder: (DeviceFolderKey)->Unit,onAll: ()->Unit) {
    val repository=remember(model) { CollectionsRepository(model.app.database,model.app.timeline) }
    var retry by remember { mutableIntStateOf(0) }
    var problem by remember { mutableStateOf<String?>(null) }
    val folders by produceState<List<DeviceFolderSummary>?>(null,model,retry) {
        problem=null
        try { model.app.database.deviceMedia().folders().collect { value=it } }
        catch(cancelled: CancellationException) { throw cancelled }
        catch(error: android.database.sqlite.SQLiteException) { problem=error.message ?: "Cannot read device folders. Retry." }
    }
    LazyVerticalGrid(GridCells.Adaptive(148.dp),state=state,modifier=Modifier.fillMaxSize().testTag("device:folders"),
        contentPadding=PaddingValues(16.dp),verticalArrangement=Arrangement.spacedBy(16.dp),horizontalArrangement=Arrangement.spacedBy(12.dp)) {
        item(span={ GridItemSpan(maxLineSpan) }) { DevicePhotoAccessMenu() }
        item(span={ GridItemSpan(maxLineSpan) }) {
            TextButton(onClick=onAll,modifier=Modifier.heightIn(min=48.dp).testTag("device:all")) { Text("All device photos") }
        }
        problem?.let { error -> item(span={ GridItemSpan(maxLineSpan) }) {
            Text(error,color=MaterialTheme.colorScheme.error)
            TextButton(onClick={ retry++ },modifier=Modifier.heightIn(min=48.dp)) { Text("Retry folders") }
        } }
        if(folders==null && problem==null) item(span={ GridItemSpan(maxLineSpan) }) { LinearProgressIndicator(Modifier.fillMaxWidth()) }
        if(folders?.isEmpty()==true) item(span={ GridItemSpan(maxLineSpan) }) {
            Text("Folders appear here after Android grants access and device discovery finishes. Camera files stay on this device until you choose Back up.")
        }
        items(folders.orEmpty(),key={ it.identity.key }) { folder ->
            Column {
                CollectionCard(folder.title,R.drawable.ic_device_folder,repository,TimelineQuery(deviceFolder=folder.identity),
                    { onFolder(folder.identity) },"device:folder:${folder.identity.key}",countLabel="${folder.count} items")
                Text(if(folder.folderPath.startsWith("bucket:")) folder.volume else "${folder.folderPath} · ${folder.volume}",
                    style=MaterialTheme.typography.bodySmall,modifier=Modifier.padding(horizontal=8.dp))
            }
        }
    }
}

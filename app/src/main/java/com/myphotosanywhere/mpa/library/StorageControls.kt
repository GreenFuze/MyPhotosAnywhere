package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.drive.binding
import java.io.File

@Composable
fun StorageControls(photos: List<Photo>,model: LibraryFeaturesModel = viewModel()) {
    val settings by model.settings.collectAsStateWithLifecycle()
    val downloads by model.downloads.collectAsStateWithLifecycle()
    val sync by model.sync.collectAsStateWithLifecycle()
    val sources by model.sources.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val keys=sources.map { it.key }.toSet()
    val queued=downloads.filter { it.bindingKey in keys }
    val states=sync.filter { it.bindingKey in keys }
    Text("Offline storage",style=MaterialTheme.typography.titleLarge)
    val disposable=photos.filter { it.sourceRemoteId!=null && it.originalPath.isNotBlank() }.sumOf { it.bytes }
    val protected=photos.filter { it.sourceRemoteId==null && it.originalPath.isNotBlank() }.sumOf { it.bytes }
    Text("%.1f MiB protected imports and edits · %.1f MiB downloaded originals".format(protected/(1024.0*1024),disposable/(1024.0*1024)))
    Text("Disposable original budget",style=MaterialTheme.typography.titleMedium)
    Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
        listOf(512L,2048L,5120L).forEach { mib -> FilterChip(selected=settings.originalBudget==mib*1024*1024,
            onClick={ model.updateSettings(settings.copy(originalBudget=mib*1024*1024)) },label={ Text(if(mib<1024) "$mib MiB" else "${mib/1024} GiB") }) }
    }
    Text("Thumbnail budget",style=MaterialTheme.typography.titleMedium)
    Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
        listOf(64L,256L,512L).forEach { mib -> FilterChip(selected=settings.thumbnailBudget==mib*1024*1024,
            onClick={ model.updateSettings(settings.copy(thumbnailBudget=mib*1024*1024)) },label={ Text("$mib MiB") }) }
    }
    Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
        OutlinedButton(enabled=!busy,onClick={ model.trimCache() }) { Text("Apply budget") }
        TextButton(enabled=!busy,onClick={ model.trimCache(true) }) { Text("Clear downloads") }
    }
    Text("Imports, edited copies, pinned albums, open photos, and pending AI work are protected. MPA rechecks the remote revision and checksum before removing a disposable download. Offline or changed files are kept.",style=MaterialTheme.typography.bodySmall)
    Row { Switch(checked=settings.objectIndexing,onCheckedChange={ model.updateSettings(settings.copy(objectIndexing=it)) }); Text("On-device object and scene labels",Modifier.padding(12.dp)) }
    Text("Search objects such as dogs, cats, food, and landscapes after indexing. No photo data is sent to an AI provider.",style=MaterialTheme.typography.bodySmall)
    IndexingControls(model)
    MetadataControls(model)
    if(sources.isNotEmpty()) {
        Text("Cloud library",style=MaterialTheme.typography.titleLarge)
        if(states.any { it.running }) LinearProgressIndicator(Modifier.fillMaxWidth())
        states.filter { it.error!=null }.forEach { state -> Text("${sources.find { it.key==state.bindingKey }?.folderName}: ${state.error}",color=MaterialTheme.colorScheme.error) }
        Text("Drive refresh runs approximately every 15 minutes when network constraints permit. External deletions hide cloud entries; local files are preserved.",style=MaterialTheme.typography.bodySmall)
        TextButton(enabled=!busy,onClick=model::refreshCloud) { Text("Refresh Drive now") }
        Text("${queued.count { it.state=="COMPLETE" }} downloads completed · ${queued.count { it.state!="COMPLETE" }} pending")
        queued.filter { it.error!=null }.take(5).forEach { Text(it.error ?: "",style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.error) }
        TextButton(enabled=!busy,onClick=model::retryDownloads) { Text("Retry downloads") }
    }
}

package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.*
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel

@Composable
internal fun StorageControls(model: LibraryFeaturesModel = viewModel(),storage: OfflineStorageModel=viewModel()) {
    val settings by model.settings.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val summary by storage.summary.collectAsStateWithLifecycle()
    val working by storage.working.collectAsStateWithLifecycle()
    val loading by storage.loading.collectAsStateWithLifecycle()
    val message by storage.message.collectAsStateWithLifecycle()
    val progress by storage.progress.collectAsStateWithLifecycle()
    var confirm by androidx.compose.runtime.saveable.rememberSaveable { mutableStateOf<Long?>(null) }
    LaunchedEffect(storage) { storage.refresh() }
    Text("Offline storage",style=MaterialTheme.typography.titleLarge,modifier=Modifier.semantics { heading() })
    Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(16.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text("Free up space",style=MaterialTheme.typography.titleMedium)
        if(loading) { LinearProgressIndicator(Modifier.fillMaxWidth());Text("Measuring downloaded originals…") }
        summary?.let { estimate ->
            Text("Up to %.1f MiB in %d downloads".format(estimate.removableBytes/(1024.0*1024),estimate.removable),modifier=Modifier.testTag("offline:estimate"))
            Text("${estimate.protected} protected local files kept. Camera files, local-only imports, edits and offline pins are preserved.")
            Button(enabled=!busy && !working && !loading && estimate.removable>0,onClick={ confirm=estimate.cutoff },modifier=Modifier.heightIn(min=48.dp).testTag("offline:clear")) {
                Text("Clear downloaded originals")
            }
        }
        if(working) {
            LinearProgressIndicator(Modifier.fillMaxWidth())
            Text("%d removed · %.1f MiB freed".format(progress.removed,progress.freedBytes/(1024.0*1024)),modifier=Modifier.testTag("offline:progress").semantics { liveRegion=LiveRegionMode.Polite })
            TextButton(onClick=storage::stop,modifier=Modifier.heightIn(min=48.dp).testTag("offline:stop")) { Text("Stop cleanup") }
        } else TextButton(enabled=!loading,onClick=storage::refresh,modifier=Modifier.heightIn(min=48.dp).testTag("offline:refresh")) { Text("Refresh estimate") }
        message?.let { Text(it,modifier=Modifier.testTag("offline:result").semantics { liveRegion=LiveRegionMode.Polite }) }
        Text("Thumbnails and photo information stay available. MPA rechecks Drive before removing each download; an internet connection is required.",style=MaterialTheme.typography.bodySmall)
    } }
    confirm?.let { cutoff -> AlertDialog(onDismissRequest={ confirm=null },title={ Text("Clear downloaded originals?") },
        text={ Text("Remove only MPA downloads that still have a verified Drive original. Keep camera files, local-only originals, offline pins, drafts and active transfers. Thumbnails and metadata stay on this device. Downloads used after this estimate are kept.") },
        confirmButton={ TextButton(onClick={ confirm=null;storage.clear(cutoff) },modifier=Modifier.heightIn(min=48.dp).testTag("offline:confirm")) { Text("Free up space") } },
        dismissButton={ TextButton(onClick={ confirm=null },modifier=Modifier.heightIn(min=48.dp).testTag("offline:cancel")) { Text("Cancel") } }) }
    Text("Disposable original budget",style=MaterialTheme.typography.titleMedium)
    FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
        listOf(512L,2048L,5120L).forEach { mib -> FilterChip(selected=settings.originalBudget==mib*1024*1024,
            onClick={ model.updateSettings(settings.copy(originalBudget=mib*1024*1024)) },label={ Text(if(mib<1024) "$mib MiB" else "${mib/1024} GiB") }) }
    }
    Text("Thumbnail budget",style=MaterialTheme.typography.titleMedium)
    FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
        listOf(64L,256L,512L).forEach { mib -> FilterChip(selected=settings.thumbnailBudget==mib*1024*1024,
            onClick={ model.updateSettings(settings.copy(thumbnailBudget=mib*1024*1024)) },label={ Text("$mib MiB") }) }
    }
    FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
        OutlinedButton(enabled=!busy,onClick={ model.trimCache() }) { Text("Apply budget") }
    }
    Text("Imports, edited copies, pinned albums, open photos, and pending AI work are protected. MPA rechecks the remote revision and checksum before removing a disposable download. Offline or changed files are kept.",style=MaterialTheme.typography.bodySmall)
}

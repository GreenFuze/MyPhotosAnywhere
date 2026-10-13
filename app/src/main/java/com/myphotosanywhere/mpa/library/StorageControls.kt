package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.myphotosanywhere.mpa.data.Photo

@Composable
fun StorageControls(photos: List<Photo>,model: LibraryFeaturesModel = viewModel()) {
    val settings by model.settings.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    Text("Offline storage",style=MaterialTheme.typography.titleLarge)
    val disposable=photos.filter { it.sourceRemoteId!=null && it.originalPath.isNotBlank() }.sumOf { it.bytes }
    val protected=photos.filter { it.sourceRemoteId==null && it.originalPath.isNotBlank() }.sumOf { it.bytes }
    Text("%.1f MiB protected imports and edits · %.1f MiB downloaded originals".format(protected/(1024.0*1024),disposable/(1024.0*1024)))
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
        TextButton(enabled=!busy,onClick={ model.trimCache(true) }) { Text("Clear downloads") }
    }
    Text("Imports, edited copies, pinned albums, open photos, and pending AI work are protected. MPA rechecks the remote revision and checksum before removing a disposable download. Offline or changed files are kept.",style=MaterialTheme.typography.bodySmall)
}

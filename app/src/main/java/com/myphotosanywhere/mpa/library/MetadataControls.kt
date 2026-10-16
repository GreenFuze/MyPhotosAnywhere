package com.myphotosanywhere.mpa.library

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle

@Composable
fun MetadataControls(model: LibraryFeaturesModel) {
    val settings by model.settings.collectAsStateWithLifecycle()
    val sources by model.sources.collectAsStateWithLifecycle()
    val states by model.metadata.collectAsStateWithLifecycle()
    val preview by model.metadataPreview.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    var exporting by remember { mutableStateOf<String?>(null) }
    val export=rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/rdf+xml")) { uri ->
        val key=exporting; if(uri!=null && key!=null) model.exportMetadata(key,uri); exporting=null
    }
    Text("Portable annotations",style=MaterialTheme.typography.titleLarge)
    Text("MPA uses .mpa-catalog.xmp in each selected Drive folder. It stores names, reviewed face regions, albums, favorites, locations and edit recipes. Anyone with access to that folder can read them. Face crops and biometric vectors stay on this phone.",style=MaterialTheme.typography.bodySmall)
    Row { Switch(checked=settings.sidecarBackup,onCheckedChange={ model.updateSettings(settings.copy(sidecarBackup=it)) })
        Text("Back up annotations to connected folders",Modifier.padding(12.dp)) }
    Text("Each catalogue contains only that source’s annotations, sharing global person IDs across accounts. Read-only sources keep a local overlay; you can export it below. Original media and its embedded EXIF are preserved. Catalogues currently have a 2 MiB limit.",style=MaterialTheme.typography.bodySmall)
    if(settings.sidecarBackup) TextButton(enabled=!busy,onClick={ model.backupMetadata() }) { Text("Back up XMP now") }
    sources.forEach { source ->
        Text("${source.folderName} · ${source.email}",style=MaterialTheme.typography.titleSmall)
        val state=states.find { it.bindingKey==source.key }
        state?.error?.let { Text(it,color=MaterialTheme.colorScheme.error,style=MaterialTheme.typography.bodySmall) }
        if(state!=null) Text(if(state.generation>state.uploadedGeneration) "Annotation changes pending" else "Annotations checkpointed",style=MaterialTheme.typography.bodySmall)
        Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            TextButton(enabled=!busy,onClick={ model.previewMetadata(source.key) }) { Text("Preview recovery") }
            TextButton(enabled=!busy,onClick={ exporting=source.key; export.launch("MPA-${source.folderName.replace(Regex("[^A-Za-z0-9_-]"),"_")}.xmp") }) { Text("Export XMP") }
        }
    }
    preview?.let { result -> AlertDialog(onDismissRequest={ model.metadataPreview.value=null },title={ Text("Recover annotations?") },
        text={ Column { Text("${result.matching} downloaded photos match the catalogue’s file IDs, sizes and SHA-256 checksums. Their favorites, locations, albums, edit drafts, reviewed faces and photo-person associations will be replaced when included. ${result.people} people and ${result.albums} albums are included. ${result.globalNameChanges} shared names or alias mappings will change across sources. ${result.unavailable} unavailable or changed photos will be skipped; download them and preview again later. Face crops and embeddings are rebuilt on your phone. Photos are preserved.")
            result.nameChanges.take(20).forEach { Text(it,style=MaterialTheme.typography.bodySmall) }
        } },
        confirmButton={ Button(enabled=!busy && result.matching>0,onClick={ model.recoverMetadata() }) { Text("Recover matching photos") } },
        dismissButton={ TextButton(onClick={ model.metadataPreview.value=null }) { Text("Cancel") } }) }
}

package com.myphotosanywhere.mpa.management

import android.content.Intent
import android.provider.Settings
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.drive.DriveStorageScreen
import com.myphotosanywhere.mpa.importing.*
import com.myphotosanywhere.mpa.library.*
import com.myphotosanywhere.mpa.ui.ShellDestination

/** Focused destinations share existing state holders. Their workers, persistence and captured destinations are unchanged. */
@Composable
internal fun ManagementScreen(destination: ShellDestination,photos: List<Photo>,features: LibraryFeaturesModel,onOpen: (ShellDestination)->Unit) {
    val holder=rememberSaveableStateHolder()
    holder.SaveableStateProvider(destination.id) {
        if(destination==ShellDestination.STORAGE) DriveStorageScreen(photos,{ onOpen(ShellDestination.ACTIVITY) },{ onOpen(ShellDestination.IMPORTS) },{ onOpen(ShellDestination.SETTINGS) })
        else Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(20.dp).testTag("management:${destination.id}"),verticalArrangement=Arrangement.spacedBy(14.dp)) {
            when(destination) {
                ShellDestination.ACTIVITY -> {
                    ManagementLink(ShellDestination.IMPORTS,onOpen);ManagementLink(ShellDestination.IMPORT_HISTORY,onOpen)
                    ScanActions(features)
                    DriveSyncActions(features)
                    val downloads by features.downloads.collectAsStateWithLifecycle()
                    if(downloads.any { it.state in listOf("FAILED","AUTH_REQUIRED") }) TextButton(onClick=features::retryDownloads) { Text("Retry downloads") }
                    ImportControls(ImportSurface.ACTIVITY)
                }
                ShellDestination.IMPORTS -> ImportControls(ImportSurface.NEW)
                ShellDestination.IMPORT_HISTORY -> ImportControls(ImportSurface.HISTORY)
                ShellDestination.SETTINGS -> {
                    ManagementSettingsMenu(onOpen)
                }
                ShellDestination.CACHE -> StorageControls(photos,features)
                ShellDestination.RECOGNITION -> {
                    val settings by features.settings.collectAsStateWithLifecycle()
                    ScanActions(features)
                    Text("Recognition is optional and runs on this phone. Enabling it may download connected originals. Imported Takeout names remain photo labels; they are not assigned to faces automatically.")
                    Row { Switch(checked=settings.faceIndexing,onCheckedChange={ features.updateSettings(settings.copy(faceIndexing=it)) });Text("On-device face indexing",Modifier.padding(12.dp)) }
                    Row { Switch(checked=settings.objectIndexing,onCheckedChange={ features.updateSettings(settings.copy(objectIndexing=it)) });Text("On-device object and scene labels",Modifier.padding(12.dp)) }
                    IndexingControls(features)
                }
                ShellDestination.ANNOTATIONS -> MetadataControls(features)
                ShellDestination.PRIVACY -> {
                    val settings by features.settings.collectAsStateWithLifecycle()
                    val context=LocalContext.current
                    Text("Online maps are optional. Opening an online map sends map requests to its provider. Place photos and indexed locations remain browsable without it.")
                    Row { Switch(checked=settings.onlineMaps,onCheckedChange={ features.updateSettings(settings.copy(onlineMaps=it)) });Text("Allow online maps",Modifier.padding(12.dp)) }
                    TextButton(onClick={ context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,android.net.Uri.fromParts("package",context.packageName,null))) },modifier=Modifier.heightIn(min=48.dp)) { Text("Android permissions and notifications") }
                    Text("Face crops and embeddings stay on this phone. Annotation backup can include names and reviewed regions in your selected Drive folders; configure it under Backup and metadata.")
                }
                ShellDestination.ABOUT -> {
                    val context=LocalContext.current
                    val info=remember(context) { com.myphotosanywhere.mpa.drive.GoogleSetupInfo.installed(context) }
                    Text("MyPhotosAnywhere",style=MaterialTheme.typography.titleLarge,modifier=Modifier.semantics { heading() })
                    Text("A familiar photo experience. Your Google Drive storage. Your control.")
                    Text(info.text())
                }
                else -> error("Not a management destination: $destination")
            }
        }
    }
}

@Composable
internal fun ManagementSettingsMenu(onOpen: (ShellDestination)->Unit) {
    Column(verticalArrangement=Arrangement.spacedBy(14.dp)) {
        listOf(ShellDestination.CACHE,ShellDestination.RECOGNITION,ShellDestination.ANNOTATIONS,ShellDestination.PRIVACY,ShellDestination.ABOUT).forEach { ManagementLink(it,onOpen) }
    }
}

@Composable
private fun ManagementLink(destination: ShellDestination,onOpen: (ShellDestination)->Unit) {
    OutlinedButton(onClick={ onOpen(destination) },modifier=Modifier.fillMaxWidth().heightIn(min=48.dp).testTag("management:${destination.title}")) { Text(destination.title) }
}

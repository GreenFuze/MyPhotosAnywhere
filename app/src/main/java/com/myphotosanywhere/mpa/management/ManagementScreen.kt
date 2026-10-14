package com.myphotosanywhere.mpa.management

import android.content.Intent
import android.provider.Settings
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.toggleable
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.Role
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
import com.myphotosanywhere.mpa.ui.ShellIcon
import com.myphotosanywhere.mpa.R
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.Alignment

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
                    SettingsToggle("On-device face indexing",settings.faceIndexing) { features.updateSettings(settings.copy(faceIndexing=it)) }
                    SettingsToggle("On-device object and scene labels",settings.objectIndexing) { features.updateSettings(settings.copy(objectIndexing=it)) }
                    IndexingControls(features)
                }
                ShellDestination.ANNOTATIONS -> MetadataControls(features)
                ShellDestination.PRIVACY -> {
                    val settings by features.settings.collectAsStateWithLifecycle()
                    val context=LocalContext.current
                    Text("Online maps are optional. Opening an online map sends map requests to its provider. Place photos and indexed locations remain browsable without it.")
                    SettingsToggle("Allow online maps",settings.onlineMaps) { features.updateSettings(settings.copy(onlineMaps=it)) }
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

/** Menu ownership stays in presentation; opening a section cannot mutate settings. */
private object SettingsMenu {
    val groups=listOf(
        "Library" to listOf(ShellDestination.CACHE,ShellDestination.RECOGNITION),
        "Backup and privacy" to listOf(ShellDestination.ANNOTATIONS,ShellDestination.PRIVACY),
        "Application" to listOf(ShellDestination.ABOUT)
    )
    fun icon(destination: ShellDestination)=when(destination) {
        ShellDestination.CACHE -> R.drawable.ic_shell_offline
        ShellDestination.RECOGNITION -> R.drawable.ic_shell_people
        ShellDestination.ANNOTATIONS -> R.drawable.ic_action_backup
        ShellDestination.PRIVACY -> R.drawable.ic_action_privacy
        ShellDestination.ABOUT -> R.drawable.ic_action_info
        ShellDestination.IMPORTS -> R.drawable.ic_shell_add
        ShellDestination.IMPORT_HISTORY -> R.drawable.ic_shell_archive
        else -> error("No menu icon for $destination")
    }
}

@Composable
internal fun ManagementSettingsMenu(onOpen: (ShellDestination)->Unit) {
    Column(verticalArrangement=Arrangement.spacedBy(20.dp)) {
        SettingsMenu.groups.forEach { (heading,destinations) ->
            Column(verticalArrangement=Arrangement.spacedBy(8.dp)) {
                Text(heading,style=MaterialTheme.typography.titleSmall,modifier=Modifier.semantics { heading() })
                Surface(shape=RoundedCornerShape(24.dp),color=MaterialTheme.colorScheme.surfaceContainer) {
                    Column {
                        destinations.forEachIndexed { index,destination ->
                            ManagementLink(destination,onOpen)
                            if(index<destinations.lastIndex) HorizontalDivider(Modifier.padding(start=56.dp,end=16.dp))
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun ManagementLink(destination: ShellDestination,onOpen: (ShellDestination)->Unit) {
    Surface(onClick={ onOpen(destination) },modifier=Modifier.fillMaxWidth().heightIn(min=64.dp)
        .testTag("management:${destination.title}").semantics { role=Role.Button },color=MaterialTheme.colorScheme.surfaceContainer) {
        Row(Modifier.padding(16.dp),verticalAlignment=Alignment.CenterVertically,horizontalArrangement=Arrangement.spacedBy(16.dp)) {
            ShellIcon(SettingsMenu.icon(destination))
            Text(destination.title,Modifier.weight(1f),style=MaterialTheme.typography.bodyLarge)
            ShellIcon(R.drawable.ic_action_chevron)
        }
    }
}

@Composable
private fun SettingsToggle(label: String,checked: Boolean,onChange: (Boolean)->Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min=48.dp).toggleable(checked,role=Role.Switch,onValueChange=onChange).padding(vertical=8.dp),verticalAlignment=androidx.compose.ui.Alignment.CenterVertically,horizontalArrangement=Arrangement.spacedBy(12.dp)) {
        Switch(checked=checked,onCheckedChange=null)
        Text(label,Modifier.weight(1f))
    }
}

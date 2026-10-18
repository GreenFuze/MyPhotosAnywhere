package com.myphotosanywhere.mpa.management

import android.content.Intent
import android.provider.Settings
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.background
import androidx.compose.ui.res.stringResource
import com.myphotosanywhere.mpa.ui.MotionSwap
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.paneTitle
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

/** Focused destinations share existing state holders. Their workers, persistence and captured destinations are unchanged. */
@Composable
internal fun ManagementScreen(destination: ShellDestination,photos: List<Photo>,features: LibraryFeaturesModel,onOpen: (ShellDestination)->Unit) {
    val holder=rememberSaveableStateHolder()
    MotionSwap(destination) { target ->
    holder.SaveableStateProvider(target.id) {
        if(target==ShellDestination.STORAGE) DriveStorageScreen(photos,{ onOpen(ShellDestination.ACTIVITY) },{ onOpen(ShellDestination.IMPORTS) },{ onOpen(ShellDestination.SETTINGS) },onFreeSpace={ onOpen(ShellDestination.CACHE) })
        else Column(Modifier.fillMaxSize().background(if(target in listOf(ShellDestination.SETTINGS,ShellDestination.PREFERENCES)) MaterialTheme.colorScheme.surfaceContainerHigh else MaterialTheme.colorScheme.surface).verticalScroll(rememberScrollState()).padding(20.dp).testTag("management:${target.id}").semantics { paneTitle=target.title },verticalArrangement=Arrangement.spacedBy(14.dp)) {
            if(target in listOf(ShellDestination.SETTINGS,ShellDestination.PREFERENCES))
                Text(target.title,style=MaterialTheme.typography.headlineLarge,modifier=Modifier.padding(vertical=16.dp).semantics { heading() })
            when(target) {
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
                ShellDestination.PREFERENCES -> {
                    PreferenceCard(stringResource(R.string.menu_photos_view),stringResource(R.string.menu_photos_view_summary),"management:Photos view") { onOpen(ShellDestination.PHOTO_VIEW) }
                    PreferenceCard(stringResource(R.string.menu_recognition),stringResource(R.string.menu_recognition_summary),"management:Recognition") { onOpen(ShellDestination.RECOGNITION) }
                }
                ShellDestination.PHOTO_VIEW -> PhotosViewPreferences()
                ShellDestination.CACHE -> StorageControls(features)
                ShellDestination.RECOGNITION -> {
                    val settings by features.settings.collectAsStateWithLifecycle()
                    Text("People names are shared across your library. Face recognition is optional and runs on this phone. Possible matches need your review. Scanning connected originals follows your download preferences.")
                    PreferenceToggle(stringResource(R.string.menu_group_faces),stringResource(R.string.menu_group_faces_summary),settings.faceIndexing,
                        { features.updateSettings(settings.copy(faceIndexing=it)) },Modifier.testTag("preference:face-indexing"))
                    PreferenceToggle(stringResource(R.string.menu_object_labels),stringResource(R.string.menu_object_labels_summary),settings.objectIndexing,
                        { features.updateSettings(settings.copy(objectIndexing=it)) },Modifier.testTag("preference:object-indexing"))
                    ScanActions(features)
                    IndexingControls(features)
                }
                ShellDestination.ANNOTATIONS -> {
                    GroupedMenu(listOf(MenuEntry(stringResource(R.string.menu_backup_destination),stringResource(R.string.menu_backup_destination_summary),R.drawable.ic_shell_account,"management:Backup destination") { onOpen(ShellDestination.STORAGE) }))
                    MetadataControls(features)
                }
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
                else -> error("Not a management destination: $target")
            }
        }
    }
    }
}

/** Menu ownership stays in presentation; opening a section cannot mutate settings. */
private object SettingsMenu {
    val destinations=listOf(ShellDestination.PREFERENCES,ShellDestination.ANNOTATIONS,ShellDestination.CACHE,ShellDestination.PRIVACY,ShellDestination.ABOUT)
    fun summary(destination: ShellDestination)=when(destination) {
        ShellDestination.PREFERENCES -> R.string.menu_preferences_summary
        ShellDestination.CACHE -> R.string.menu_storage_summary
        ShellDestination.ANNOTATIONS -> R.string.menu_annotations_summary
        ShellDestination.PRIVACY -> R.string.menu_privacy_summary
        ShellDestination.ABOUT -> R.string.menu_about_summary
        else -> error("No settings summary for $destination")
    }
    fun icon(destination: ShellDestination)=when(destination) {
        ShellDestination.PREFERENCES -> R.drawable.ic_action_settings
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
    GroupedMenu(SettingsMenu.destinations.map { destination ->
        MenuEntry(destination.title,stringResource(SettingsMenu.summary(destination)),SettingsMenu.icon(destination),"management:${destination.title}") { onOpen(destination) }
    })
}

@Composable
private fun ManagementLink(destination: ShellDestination,onOpen: (ShellDestination)->Unit) {
    GroupedMenu(listOf(MenuEntry(destination.title,"",SettingsMenu.icon(destination),"management:${destination.title}") { onOpen(destination) }))
}

@Composable
private fun SettingsToggle(label: String,checked: Boolean,onChange: (Boolean)->Unit) {
    PreferenceToggle(label,"",checked,onChange)
}

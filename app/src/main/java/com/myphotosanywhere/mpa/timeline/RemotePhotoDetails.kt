package com.myphotosanywhere.mpa.timeline

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalWindowInfo
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.myphotosanywhere.mpa.library.LibraryFeaturesModel
import com.myphotosanywhere.mpa.library.ViewerStatus
import com.myphotosanywhere.mpa.library.DetailSection
import com.myphotosanywhere.mpa.R
import kotlinx.coroutines.launch
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.text.DateFormat
import java.util.Date

/** Remote metadata is inspectable without creating a local identity or downloading original bytes. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun RemotePhotoDetails(entry: TimelineEntry?,media: TimelineMedia?,features: LibraryFeaturesModel,onClose: ()->Unit,onSource: ()->Unit) {
    val sources by features.sources.collectAsStateWithLifecycle()
    val busy by features.busy.collectAsStateWithLifecycle()
    val downloads by features.downloads.collectAsStateWithLifecycle()
    val available by produceState(false,media) {
        value=withContext(Dispatchers.IO) { media?.photo?.originalPath?.let { it.isNotBlank() && File(it).isFile }==true }
    }
    val sheet=rememberModalBottomSheetState(skipPartiallyExpanded=true)
    val scope=rememberCoroutineScope()
    ModalBottomSheet(onDismissRequest=onClose,sheetState=sheet,modifier=Modifier.testTag("photo-details")) {
        Column(Modifier.fillMaxWidth().heightIn(max=with(LocalDensity.current) { LocalWindowInfo.current.containerSize.height.toDp()*.7f }).verticalScroll(rememberScrollState()).padding(horizontal=20.dp).navigationBarsPadding(),
            verticalArrangement=Arrangement.spacedBy(12.dp)) {
            FlowRow(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween) {
                Text("Photo details",style=MaterialTheme.typography.titleLarge,modifier=Modifier.semantics { heading() })
                TextButton(onClick={ scope.launch { sheet.hide();onClose() } },modifier=Modifier.heightIn(min=48.dp).testTag("photo-details-close")) { Text("Close") }
            }
            ViewerStatus(features)
            if(entry==null) Text("Restoring this photo's details…") else {
                DetailSection("Date and file",R.drawable.ic_action_info) {
                Text(entry.millis?.let { DateFormat.getDateInstance(DateFormat.FULL).format(Date(it)) } ?: "Unknown date",style=MaterialTheme.typography.titleLarge)
                entry.millis?.let { Text(DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(it))) }
                Text(entry.name,style=MaterialTheme.typography.bodyMedium)
                if(entry.provenance==TimestampProvenance.DRIVE_MODIFIED) Text("Date inferred from Drive modification time")
                Text("Type: ${entry.mimeType}")
                Text("Size: %.1f MiB".format(entry.bytes/(1024.0*1024)))
                }
                DetailSection("Original and source",R.drawable.ic_shell_account) {
                val source=sources.find { it.key==entry.binding }
                Text(source?.let { "Source: ${it.folderName} · ${it.email}" } ?: "Source disconnected · ${entry.binding}",modifier=Modifier.testTag("details:source"))
                Text(if(available) "Original available on this device" else "Original unavailable on this device",modifier=Modifier.testTag("details:availability"))
                val task=downloads.find { it.key==entry.cloudKey }
                when(task?.state) {
                    "AUTH_REQUIRED" -> TextButton(onClick=onSource) { Text("Reconnect source") }
                    "PENDING","DOWNLOADING","RETRYING" -> Text("Loading verified original…")
                    else -> media?.cloud?.let { cloud -> if(!available) TextButton(enabled=!busy,onClick={ features.download(cloud,retry=true) }) { Text("Download original") } }
                }
                if(available) Text("Close Details to use the downloaded original's editing and sharing actions.")
                }
            }
            Spacer(Modifier.height(20.dp))
        }
    }
}

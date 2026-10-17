package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle

@Composable
fun IndexingControls(model: LibraryFeaturesModel) {
    val settings by model.settings.collectAsStateWithLifecycle()
    val focus=LocalFocusManager.current
    val keyboard=LocalSoftwareKeyboardController.current
    val progress by model.indexProgress.collectAsStateWithLifecycle()
    var start by remember(settings.nightStart) { mutableStateOf("%02d:%02d".format(java.util.Locale.US,settings.nightStart/60,settings.nightStart%60)) }
    var end by remember(settings.nightEnd) { mutableStateOf("%02d:%02d".format(java.util.Locale.US,settings.nightEnd/60,settings.nightEnd%60)) }
    fun minute(text: String): Int? {
        if(!text.matches(Regex("[0-2][0-9]:[0-5][0-9]"))) return null
        val parts=text.split(':'); val hour=parts[0].toInt(); return if(hour<24) hour*60+parts[1].toInt() else null
    }
    Text("Phone indexing",style=MaterialTheme.typography.titleMedium)
    Text("${progress.status} · ${progress.done}/${progress.total} this batch · ${progress.errors} errors",style=MaterialTheme.typography.bodySmall)
    Row { Switch(checked=settings.indexCloud,onCheckedChange={ model.updateSettings(settings.copy(indexCloud=it)) })
        Text("Include all connected Drive sources",Modifier.padding(12.dp)) }
    Text("Downloads verified originals in small batches using each source’s network preference. Files waiting for an enabled scan are protected from cache eviction; after scanning, ordinary cache limits apply.",style=MaterialTheme.typography.bodySmall)
    Row { Switch(modifier=Modifier.testTag("index-night-switch"),checked=settings.overnightIndexing,onCheckedChange={ model.updateSettings(settings.copy(overnightIndexing=it)) })
        Text("Automatically scan while charging at night",Modifier.padding(12.dp)) }
    if(!settings.overnightIndexing) Text("Enabled indexing runs when the battery is not low; use Pause scans to stop background inference.",style=MaterialTheme.typography.bodySmall)
    if(settings.overnightIndexing) {
        Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(start,{ if(it.length<=5) start=it },label={ Text("Start HH:mm") },singleLine=true,modifier=Modifier.weight(1f).testTag("index-start"))
            OutlinedTextField(end,{ if(it.length<=5) end=it },label={ Text("End HH:mm") },singleLine=true,modifier=Modifier.weight(1f).testTag("index-end"))
        }
        val from=minute(start); val to=minute(end)
        TextButton(enabled=from!=null && to!=null && from!=to,onClick={
            model.updateSettings(settings.copy(nightStart=requireNotNull(from),nightEnd=requireNotNull(to)))
            focus.clearFocus(force=true);keyboard?.hide()
        }) { Text("Save window") }
        Text("Uses your phone’s local time. Android may delay background jobs. Scans stop between photos outside this window, when unplugged, or when the phone is hot. Scan now runs on demand outside the night window.",style=MaterialTheme.typography.bodySmall)
    }
}

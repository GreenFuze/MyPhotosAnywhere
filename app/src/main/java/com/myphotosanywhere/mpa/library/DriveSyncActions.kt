package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import java.text.DateFormat
import java.util.Date

/** Primary refresh/progress controls stay above the long storage and indexing settings. */
@Composable
fun DriveSyncActions(model: LibraryFeaturesModel = viewModel()) {
    val sources by model.sources.collectAsStateWithLifecycle()
    val states by model.sync.collectAsStateWithLifecycle()
    val downloads by model.downloads.collectAsStateWithLifecycle()
    if (sources.isEmpty()) return
    Text("Drive synchronization", style = MaterialTheme.typography.titleMedium)
    sources.forEach { source ->
        val state = states.find { it.bindingKey == source.key }
        val transfers = downloads.filter { it.bindingKey == source.key }
        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text("${source.folderName} · ${source.email}", Modifier.weight(1f))
                    TextButton(onClick = { model.refreshSource(source.key) }) {
                        Text(if (state?.error != null) "Retry refresh" else "Refresh Drive")
                    }
                }
                Text(when (state?.status) {
                    "AUTH_REQUIRED" -> "Reconnect this source using Manage source below."
                    "FAILED" -> "Refresh failed; saved progress is preserved."
                    "DISCOVERING" -> "Discovering folder contents"
                    "RECONCILING" -> "Checking Drive changes"
                    "IDLE" -> "Up to date at last successful sync"
                    else -> "Waiting for refresh / permitted network"
                }, style = MaterialTheme.typography.bodySmall)
                if (state?.scanGeneration != null) Text("${state.scanPages} folder pages saved; discovery will resume.")
                Text(if (state == null || state.lastSuccess == 0L) "No completed sync yet"
                    else "Last successful sync: ${DateFormat.getDateTimeInstance().format(Date(state.lastSuccess))}",
                    style = MaterialTheme.typography.bodySmall)
                state?.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                Text("${transfers.count { it.state == "COMPLETE" }} downloads completed · ${transfers.count { it.state !in listOf("COMPLETE", "FAILED", "AUTH_REQUIRED") }} pending",
                    style = MaterialTheme.typography.bodySmall)
            }
        }
    }
    Text("Background sync is eventual and follows each source's network preference. Discovery lists media without downloading originals.",
        style = MaterialTheme.typography.bodySmall)
}

package com.myphotosanywhere.mpa.importing

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp

/** Room checkpoints drive both this panel and the background notification. */
@Composable
fun ImportJobProgress(id: String, progress: ImportProgress) {
    Column(Modifier.fillMaxWidth().testTag("import-progress:$id"),verticalArrangement=Arrangement.spacedBy(6.dp)) {
        val name=if(progress.kind=="TAKEOUT") "Takeout" else "Photos Picker"
        val state=if(progress.state=="WAITING_FOR_APP") "waiting to resume" else progress.state.lowercase()
        Row(verticalAlignment=Alignment.CenterVertically) {
            com.myphotosanywhere.mpa.ui.ShellIcon(if(progress.state=="FAILED") com.myphotosanywhere.mpa.R.drawable.ic_shell_warning else com.myphotosanywhere.mpa.R.drawable.ic_shell_activity)
            Text("$name: $state",style=MaterialTheme.typography.titleSmall,modifier=Modifier.padding(start=8.dp))
        }
        Text("${progress.complete}/${progress.total} verified in Drive",modifier=Modifier.testTag("import-count:$id"))
        val bar=Modifier.fillMaxWidth().testTag("import-bar:$id")
        if(progress.total>0) {
            LinearProgressIndicator(progress={ progress.complete.toFloat()/progress.total },modifier=bar)
        } else if(progress.state in listOf("RUNNING","DOWNLOADING")) {
            LinearProgressIndicator(modifier=bar)
        }
        Text(progress.detail,modifier=Modifier.testTag("import-detail:$id"),
            color=if(progress.state=="FAILED" || progress.state=="WAITING_FOR_APP") MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurface,
            style=MaterialTheme.typography.bodyMedium)
    }
}

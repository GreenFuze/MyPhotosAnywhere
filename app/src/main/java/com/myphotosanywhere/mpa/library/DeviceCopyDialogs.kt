package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.myphotosanywhere.mpa.R

@Composable
internal fun DeviceCopyDialogs(model: LibraryFeaturesModel,onClosing: ()->Unit) {
    val request by model.deviceCopyRequest.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val sources by model.sources.collectAsStateWithLifecycle()
    request?.let { item -> AlertDialog(onDismissRequest=model::dismissRemoveDownload,
        title={ Text(stringResource(R.string.ux_remove_download_question)) },
        text={ Column(Modifier.heightIn(max=400.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(12.dp)) {
            Text(item.name,style=MaterialTheme.typography.titleMedium)
            sources.find { it.key==item.identity.binding }?.let { Text("${it.folderName} · ${it.email}",style=MaterialTheme.typography.bodySmall) }
            Text(stringResource(R.string.ux_remove_download_explanation))
        } },
        confirmButton={ TextButton(enabled=!busy,onClick={ model.confirmRemoveDownload(onClosing) },modifier=Modifier.heightIn(min=48.dp).testTag("device-copy:confirm")) {
            Text(stringResource(R.string.ux_remove_download))
        } },
        dismissButton={ TextButton(enabled=!busy,onClick=model::dismissRemoveDownload,modifier=Modifier.heightIn(min=48.dp).testTag("device-copy:cancel")) { Text(stringResource(R.string.ux_cancel)) } }) }
}

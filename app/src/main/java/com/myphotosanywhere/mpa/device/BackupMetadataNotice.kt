package com.myphotosanywhere.mpa.device

import android.Manifest
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.heightIn
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp

@Composable
internal fun BackupMetadataNotice() {
    val context=LocalContext.current
    val permissions=remember(context) { DevicePhotoPermissions(context) }
    var location by remember { mutableStateOf(permissions.location()) }
    val launcher=rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { location=permissions.location() }
    Text("Includes the original and available photo information: dates, location, names, labels, albums and edits. Face embeddings and private face crops stay on this device.")
    if(Build.VERSION.SDK_INT>=29 && !location) {
        Text("Android hides camera location without separate permission. Backup can continue with the metadata Android makes available. Previously prepared originals are kept unchanged.")
        TextButton(onClick={ launcher.launch(Manifest.permission.ACCESS_MEDIA_LOCATION) },modifier=Modifier.heightIn(min=48.dp).testTag("device:backup-location")) {
            Text("Allow original location metadata")
        }
    }
}

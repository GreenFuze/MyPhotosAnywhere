package com.myphotosanywhere.mpa.drive

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import java.security.MessageDigest

internal data class GoogleSetupInfo(val version: String,val packageName: String,val sha1: List<String>) {
    fun text()="MPA $version\nPackage: $packageName\nSigning SHA-1:\n${sha1.joinToString("\n")}\n"+
        "Existing-folder scope: ${DriveMode.EXISTING_LIBRARY.scope}\nNew-folder scope: ${DriveMode.APP_FILES.scope}"
    companion object {
        @Suppress("DEPRECATION")
        fun installed(context: Context): GoogleSetupInfo {
            val flags=if(Build.VERSION.SDK_INT>=28) PackageManager.GET_SIGNING_CERTIFICATES else PackageManager.GET_SIGNATURES
            val info=context.packageManager.getPackageInfo(context.packageName,flags)
            val signatures=if(Build.VERSION.SDK_INT>=28) info.signingInfo?.apkContentsSigners.orEmpty() else info.signatures.orEmpty()
            val sha1=signatures.map { signature -> MessageDigest.getInstance("SHA-1").digest(signature.toByteArray())
                .joinToString(":") { "%02X".format(it.toInt() and 255) } }
            val code=if(Build.VERSION.SDK_INT>=28) info.longVersionCode else info.versionCode.toLong()
            return GoogleSetupInfo("${info.versionName ?: "unknown"} ($code)",context.packageName,sha1)
        }
    }
}

@Composable
internal fun GoogleSetupDetails() {
    val context=LocalContext.current
    val info=remember(context) { runCatching { GoogleSetupInfo.installed(context) }.getOrNull() }
    var open by remember { mutableStateOf(false) }
    info?.let { Text("MyPhotosAnywhere · ${it.version}",style=MaterialTheme.typography.labelMedium) }
    TextButton(onClick={ open=true }) { Text("Google setup details") }
    if(open) AlertDialog(onDismissRequest={ open=false },title={ Text("Google setup for this installed app") },
        text={ Column(Modifier.verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(10.dp)) {
            Text(info?.text() ?: "Installed certificate unavailable.")
            Text("In the same Google Cloud project: enable Drive API; add an Android OAuth client matching this package and SHA-1; keep the app in testing and add your account as a test user. Existing folders need the full Drive scope listed above.")
            Text("After reinstalling, reconnect your Drive sources. If the previous app used another signer, keep its OAuth client too. An account or organisation policy can also block authorization; check the error Google displays.")
        } },confirmButton={ TextButton(onClick={ open=false }) { Text("Close") } },dismissButton={
            TextButton(enabled=info!=null,onClick={ (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager)
                .setPrimaryClip(ClipData.newPlainText("MPA Google setup",info?.text().orEmpty())) }) { Text("Copy setup details") }
        })
}

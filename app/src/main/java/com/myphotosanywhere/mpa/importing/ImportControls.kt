package com.myphotosanywhere.mpa.importing

import android.accounts.AccountManager
import android.app.Activity
import android.app.Application
import android.content.Intent
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.google.android.gms.auth.api.identity.Identity
import com.google.android.gms.common.AccountPicker
import com.myphotosanywhere.mpa.MpaApplication
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*

class ImportModel(application: Application): AndroidViewModel(application) {
    val app=application as MpaApplication
    val jobs=app.database.imports().observeJobs().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val items=app.database.imports().observeItems().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val sidecars=app.database.imports().observeSidecars().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val sources=app.database.drive().observeSources().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val busy=MutableStateFlow(false)
    val message=MutableStateFlow<String?>(null)
    private var photosEmail: String?=null
    fun run(action: suspend () -> Unit) {
        if(busy.value) return
        viewModelScope.launch { busy.value=true
            try { action() } catch(e: CancellationException) { throw e }
            catch(e: Exception) { message.value=e.message ?: "Import failed. Source files are preserved." }
            finally { busy.value=false }
        }
    }
    fun add(uris: List<Uri>,tree: Boolean,existing: String?=null) = run {
        for(uri in uris) app.contentResolver.takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION)
        app.imports.addTakeout(uris,tree,existing)
    }
    fun authorize(email: String, resolve: (IntentSenderRequest)->Unit, open: (String)->Unit) = run {
        photosEmail=email
        val result=PhotosAuthorization(app).request(email)
        if(result.hasResolution()) resolve(IntentSenderRequest.Builder(requireNotNull(result.pendingIntent).intentSender).build())
        else open(app.imports.createPicker(email).pickerUri ?: error("Google did not return a picker."))
    }
    fun authorized(intent: Intent?,open: (String)->Unit) = run {
        require(intent!=null) { "Google Photos authorization was cancelled. Try again." }
        val result=Identity.getAuthorizationClient(app).getAuthorizationResultFromIntent(intent)
        require(PhotosAuthorization.SCOPE in result.grantedScopes && !result.hasResolution()) { "Google Photos consent was not granted." }
        val email=photosEmail ?: error("Account selection expired. Choose Google Photos again.")
        open(app.imports.createPicker(email).pickerUri ?: error("Google did not return a picker."))
    }
}

@Composable
fun ImportControls(model: ImportModel=viewModel()) {
    val context=LocalContext.current
    val jobs by model.jobs.collectAsStateWithLifecycle()
    val items by model.items.collectAsStateWithLifecycle()
    val sidecars by model.sidecars.collectAsStateWithLifecycle()
    val sources by model.sources.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val message by model.message.collectAsStateWithLifecycle()
    val settings by model.app.settings.state.collectAsStateWithLifecycle()
    var preview by remember { mutableStateOf<String?>(null) }
    var targetJob by rememberSaveable { mutableStateOf<String?>(null) }
    fun open(uri: String) {
        try { context.startActivity(Intent(Intent.ACTION_VIEW,Uri.parse(uri))) }
        catch(_: Exception) { model.message.value="Install a browser to open Google Photos, then use Open picker below." }
    }
    val resolution=rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) {
        model.authorized(it.data,::open)
    }
    val account=rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        val email=it.data?.getStringExtra(AccountManager.KEY_ACCOUNT_NAME)
        if(it.resultCode==Activity.RESULT_OK && !email.isNullOrBlank()) model.authorize(email,{ request -> resolution.launch(request) },::open)
    }
    val zip=rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { if(it.isNotEmpty()) model.add(it,false,targetJob) }
    val folder=rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { if(it!=null) model.add(listOf(it),true,targetJob) }
    Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(12.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text("Import Google Photos",style=MaterialTheme.typography.titleMedium)
        Text("Imports go to the current default Drive destination. Sources remain untouched. Media limit: 250 MiB per file; catalogue: 10,000 assets / 2 MiB.")
        Text(if(settings.sidecarBackup) "Imported metadata uses XMP annotation backup; check its backup status below."
            else "Imported metadata is local only until you enable XMP annotation backup below.",style=MaterialTheme.typography.bodySmall)
        Button(enabled=!busy,onClick={
            try { account.launch(AccountPicker.newChooseAccountIntent(AccountPicker.AccountChooserOptions.Builder().setAllowableAccountsTypes(listOf("com.google")).build())) }
            catch(_: Exception) { model.message.value="Google account selection requires Google Play services." }
        },modifier=Modifier.testTag("import-photos-picker")) { Text("Choose from Google Photos") }
        OutlinedButton(enabled=!busy,onClick={ targetJob=null; zip.launch(arrayOf("application/zip","application/x-zip-compressed")) },modifier=Modifier.testTag("import-takeout")) { Text("Import Takeout ZIP parts") }
        TextButton(enabled=!busy,onClick={ targetJob=null; folder.launch(null) }) { Text("Import extracted Takeout folder") }
        Text("For ZIPs, use local/USB storage. Cloud document providers may download the archive internally. Picker omits location metadata and may transcode videos.",style=MaterialTheme.typography.bodySmall)
        if(busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        message?.let { Text(it,color=MaterialTheme.colorScheme.error); TextButton(onClick={ model.message.value=null }) { Text("Dismiss") } }
        jobs.take(10).forEach { job ->
            val entries=items.filter { it.job==job.id }
            Text("${if(job.kind=="TAKEOUT") "Takeout" else "Photos Picker"}: ${job.state.lowercase()} · ${entries.count { it.state=="COMPLETE" }}/${entries.count { it.selected }} imported")
            Text("Destination: ${sources.find { it.key==job.binding }?.let { "${it.folderName} (${it.email})" } ?: "Disconnected original source"}",style=MaterialTheme.typography.bodySmall)
            job.error?.let { Text(it,color=MaterialTheme.colorScheme.error) }
            if(job.kind=="TAKEOUT" && job.state !in listOf("RUNNING","CANCELLED"))
                TextButton(enabled=!busy,onClick={ targetJob=job.id; zip.launch(arrayOf("application/zip","application/x-zip-compressed")) }) { Text("Add ZIP parts to this import") }
            if(job.state=="PICKING") {
                Text(job.email)
                TextButton(enabled=!busy,onClick={ job.pickerUri?.let { open(it) } }) { Text("Open picker") }
                Button(enabled=!busy,onClick={ model.run { model.app.imports.checkPicker(job.id) } }) { Text("Check completed selection") }
            } else TextButton(enabled=!busy,onClick={ preview=job.id }) { Text("Preview selection and metadata") }
            if(job.state !in listOf("RUNNING","PICKING","COMPLETE","CANCELLED"))
                Button(enabled=!busy,onClick={ model.run { model.app.imports.start(job.id) } }) { Text("Import selected / Resume") }
            if(job.state=="RUNNING") TextButton(enabled=!busy,onClick={ model.run { model.app.imports.pause(job.id) } }) { Text("Pause import") }
            if(job.state !in listOf("COMPLETE","CANCELLED")) TextButton(enabled=!busy,onClick={ model.run { model.app.imports.pause(job.id,true) } }) { Text("Cancel remaining import") }
        }
        if(sidecars.isNotEmpty()) {
            Text("Google metadata found in Drive",style=MaterialTheme.typography.titleMedium)
            Text("Conversion keeps existing MPA values, backs up the full Google record in XMP, then moves verified JSON files to Drive trash. Enable XMP annotation backup below first. Ambiguous or conflicting JSON stays in Drive.")
            sources.filter { source -> sidecars.any { it.binding==source.key } }.forEach { source ->
                var enabled by remember(source.key) { mutableStateOf(model.app.takeoutMetadata.enabled(source.key)) }
                Row { Checkbox(enabled,{
                    val value=it; model.run { model.app.takeoutMetadata.enable(source.key,value); enabled=value }
                }); Text("Convert and clean up Google metadata in ${source.folderName}") }
                val records=sidecars.filter { it.binding==source.key }
                Text("${records.count { it.state=="COMPLETE" }} converted · ${records.count { it.state!="COMPLETE" && it.state!="GONE" }} pending/review")
                records.filter { it.error!=null }.take(3).forEach { Text("${it.name}: ${it.error}",style=MaterialTheme.typography.bodySmall) }
                TextButton(enabled=!busy && enabled,onClick={ model.run { model.app.takeoutMetadata.retry(source.key) } }) { Text("Retry metadata conversion") }
            }
        }
    } }
    preview?.let { jobId ->
        val job=jobs.find { it.id==jobId }
        val entries=items.filter { it.job==jobId }
        AlertDialog(onDismissRequest={ preview=null },title={ Text("Import preview") },text={
            LazyColumn(Modifier.heightIn(max=480.dp)) {
                item {
                Text("${entries.count { it.selected }} selected · %.1f MiB known media size".format(entries.filter { it.selected }.sumOf { it.size }/(1024.0*1024)))
                Text("Picker sizes are unknown until downloaded. Exact duplicates are checked against verified bytes in this destination before upload.")
                }
                items(entries,key={ it.id }) { item ->
                    Row { Checkbox(item.selected,{ selected -> model.run { model.app.imports.select(item.id,selected) } },
                        enabled=!busy && job?.state in listOf("PREVIEW","PAUSED","FAILED") && item.state !in listOf("COMPLETE","UNSUPPORTED","MATCHED","UNRESOLVED"))
                        Column { Text(item.path.take(250)); Text("${item.state.lowercase()} · ${if(item.metadata!=null) "metadata present" else "no supplemental metadata"}",style=MaterialTheme.typography.bodySmall) }
                    }
                    item.error?.let { Text(it,color=MaterialTheme.colorScheme.error) }
                    if(item.mime.startsWith("image/") || item.mime.startsWith("video/")) item.metadata?.let { raw ->
                        if(job?.kind=="TAKEOUT") runCatching { TakeoutCodec().parse(raw) }.getOrNull()?.let { metadata ->
                            Text(metadata.description.take(500)); Text("Incoming date: ${metadata.captured?.let { java.util.Date(it) } ?: "absent"}; GPS: ${metadata.latitude}, ${metadata.longitude}")
                            if(metadata.people.isNotEmpty()) Text("People hints (unconfirmed): ${metadata.people.joinToString()}")
                        }
                    }
                }
            }
        },confirmButton={ TextButton(onClick={ preview=null }) { Text("Close") } })
    }
}

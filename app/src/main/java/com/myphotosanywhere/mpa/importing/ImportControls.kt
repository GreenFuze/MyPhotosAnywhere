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
    val downloads=app.database.imports().observeDownloads().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
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
    val downloads by model.downloads.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val message by model.message.collectAsStateWithLifecycle()
    val settings by model.app.settings.state.collectAsStateWithLifecycle()
    var preview by remember { mutableStateOf<String?>(null) }
    var targetJob by rememberSaveable { mutableStateOf<String?>(null) }
    var chooseZip by rememberSaveable { mutableStateOf(false) }
    var showUrl by rememberSaveable { mutableStateOf(false) }
    // Signed download links must not enter Android's saved-state bundle.
    var downloadUrl by remember { mutableStateOf("") }
    var replaceLink by rememberSaveable { mutableStateOf<String?>(null) }
    var autoImport by rememberSaveable { mutableStateOf(true) }
    var discardZip by rememberSaveable { mutableStateOf<String?>(null) }
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
        Text("Imports go to the default Drive destination. Your only connected folder is chosen automatically. User-selected source files remain untouched. Media limit: 250 MiB per file; catalogue: 10,000 assets / 2 MiB.")
        val destination=sources.find { it.backupEnabled } ?: sources.singleOrNull()
        Text(destination?.let { "New import destination: ${it.folderName} (${it.email})" }
            ?: "Connect a Drive folder, or choose a default when several folders are connected.",style=MaterialTheme.typography.bodySmall)
        Text(if(settings.sidecarBackup) "Imported metadata uses XMP annotation backup; check its backup status below."
            else "Imported metadata is local only until you enable XMP annotation backup below.",style=MaterialTheme.typography.bodySmall)
        Button(enabled=!busy,onClick={
            try { account.launch(AccountPicker.newChooseAccountIntent(AccountPicker.AccountChooserOptions.Builder().setAllowableAccountsTypes(listOf("com.google")).build())) }
            catch(_: Exception) { model.message.value="Google account selection requires Google Play services." }
        },modifier=Modifier.testTag("import-photos-picker")) { Text("Choose from Google Photos") }
        OutlinedButton(enabled=!busy,onClick={ targetJob=null; chooseZip=true },modifier=Modifier.testTag("import-takeout")) { Text("Import Takeout ZIP parts") }
        TextButton(enabled=!busy,onClick={ targetJob=null; folder.launch(null) }) { Text("Import extracted Takeout folder") }
        Text("For ZIPs, use local/USB storage. Cloud document providers may download the archive internally. Picker omits location metadata and may transcode videos.",style=MaterialTheme.typography.bodySmall)
        if(busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        message?.let { Text(it,color=MaterialTheme.colorScheme.error); TextButton(onClick={ model.message.value=null }) { Text("Dismiss") } }
        jobs.take(10).forEach { job ->
            val entries=items.filter { it.job==job.id }
            val download=downloads.find { it.job==job.id }
            Text("${if(job.kind=="TAKEOUT") "Takeout" else "Photos Picker"}: ${job.state.lowercase()} · ${entries.count { it.state=="COMPLETE" }}/${entries.count { it.selected }} imported")
            Text("Destination: ${sources.find { it.key==job.binding }?.let { "${it.folderName} (${it.email})" } ?: "Disconnected original source"}",style=MaterialTheme.typography.bodySmall)
            job.error?.let { Text(it,color=MaterialTheme.colorScheme.error) }
            download?.let {
                Text(when(it.state) {
                    "CLEANED" -> "Downloaded ZIP removed from this phone."
                    "CLEANING" -> "Verified import complete; phone ZIP cleanup pending."
                    "READY" -> "ZIP on phone: %.1f MiB. Removed automatically after all media is verified in Drive.".format(it.bytes/(1024.0*1024))
                    else -> "ZIP download: %.1f MiB / %s".format(it.bytes/(1024.0*1024),if(it.total>0) "%.1f MiB".format(it.total/(1024.0*1024)) else "unknown size")
                },style=MaterialTheme.typography.bodySmall)
                if(job.state=="DOWNLOADING") LinearProgressIndicator(Modifier.fillMaxWidth())
                if(it.state!="CLEANED" && job.state=="COMPLETE") {
                    if(TakeoutArchiveStore.canClean(entries)) TextButton(enabled=!busy,onClick={ model.run { model.app.imports.retryCleanup(job.id) } }) { Text("Retry phone ZIP cleanup") }
                    else Text("ZIP retained for skipped or unsupported files. Review them before deleting it.",style=MaterialTheme.typography.bodySmall)
                }
                if(it.state!="CLEANED" && job.state !in listOf("RUNNING","DOWNLOADING")) {
                    if(entries.isEmpty() && job.state!="CANCELLED") TextButton(enabled=!busy,onClick={ replaceLink=job.id; downloadUrl=""; showUrl=true }) { Text("Paste a fresh download link") }
                    TextButton(enabled=!busy,onClick={ discardZip=job.id }) { Text("Delete downloaded ZIP from phone") }
                }
            }
            if(job.kind=="TAKEOUT" && job.state !in listOf("RUNNING","DOWNLOADING","CANCELLED"))
                TextButton(enabled=!busy,onClick={ targetJob=job.id; zip.launch(arrayOf("application/zip","application/x-zip-compressed")) }) { Text("Add ZIP parts to this import") }
            if(job.state=="PICKING") {
                Text(job.email)
                TextButton(enabled=!busy,onClick={ job.pickerUri?.let { open(it) } }) { Text("Open picker") }
                Button(enabled=!busy,onClick={ model.run { model.app.imports.checkPicker(job.id) } }) { Text("Check completed selection") }
            } else if(entries.isNotEmpty()) TextButton(enabled=!busy,onClick={ preview=job.id }) { Text("Preview selection and metadata") }
            if(job.state !in listOf("RUNNING","DOWNLOADING","PICKING","COMPLETE","CANCELLED"))
                Button(enabled=!busy,onClick={ model.run { model.app.imports.start(job.id) } }) { Text("Import selected / Resume") }
            if(job.state in listOf("RUNNING","DOWNLOADING")) TextButton(enabled=!busy,onClick={ model.run { model.app.imports.pause(job.id) } }) { Text("Pause import") }
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
    if(chooseZip) AlertDialog(onDismissRequest={ chooseZip=false },title={ Text("Import Takeout ZIP") },text={
        Column {
            Text("Choose ZIP files on your phone or USB, or let MPA download a ZIP from a URL.")
            TextButton(onClick={ chooseZip=false; zip.launch(arrayOf("application/zip","application/x-zip-compressed")) },modifier=Modifier.testTag("takeout-local-zip")) { Text("Choose local / USB ZIP files") }
            TextButton(onClick={ chooseZip=false; replaceLink=null; downloadUrl=""; showUrl=true },modifier=Modifier.testTag("takeout-url")) { Text("Download ZIP from URL") }
        }
    },confirmButton={ TextButton(onClick={ chooseZip=false }) { Text("Close") } })
    if(showUrl) AlertDialog(onDismissRequest={ showUrl=false; downloadUrl="" },title={ Text("Download Takeout ZIP") },text={
        Column(Modifier.verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(downloadUrl,{ downloadUrl=it },label={ Text("Direct HTTPS ZIP URL") },singleLine=true,modifier=Modifier.testTag("takeout-url-input"))
            Text("Use a link that downloads ZIP bytes directly. Google's emailed Takeout page may need browser sign-in; download that in your browser and choose the local ZIP instead.")
            Text("MPA needs space for the entire ZIP plus one media file. Maximum ZIP: 50 GiB. Your Drive source's network preference applies.")
            if(replaceLink==null) Row { Checkbox(autoImport,{ autoImport=it }); Text("Import automatically after download") }
            Text("MPA removes its downloaded ZIP after all media is verified in Drive. Skipped or unsupported media keeps the ZIP for review. Google Photos and user-selected ZIP files stay untouched.")
        }
    },confirmButton={
        Button(enabled=downloadUrl.isNotBlank() && !busy,onClick={
            val link=downloadUrl; val replacing=replaceLink; val start=autoImport
            downloadUrl=""; showUrl=false
            model.run { if(replacing==null) model.app.imports.addTakeoutUrl(link,start) else model.app.imports.replaceDownloadLink(replacing,link) }
        },modifier=Modifier.testTag("takeout-url-submit")) { Text(if(replaceLink!=null) "Restart download" else if(autoImport) "Download and import" else "Download and preview") }
    },dismissButton={ TextButton(onClick={ showUrl=false; downloadUrl="" }) { Text("Cancel") } })
    discardZip?.let { id -> AlertDialog(onDismissRequest={ discardZip=null },title={ Text("Delete downloaded ZIP?") },text={
        Text("This removes MPA's downloaded ZIP and unfinished transfer copies from this phone. Verified imports remain in Drive. Skipped or unfinished files will need the source ZIP again; an expired link may require a new export.")
    },confirmButton={ TextButton(onClick={ discardZip=null; model.run { model.app.imports.discardDownloadedZip(id) } }) { Text("Delete phone ZIP") } },
        dismissButton={ TextButton(onClick={ discardZip=null }) { Text("Keep ZIP") } }) }
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
                        enabled=!busy && job?.state in listOf("PREVIEW","PAUSED","FAILED") && item.state !in listOf("COMPLETE","UNSUPPORTED","MATCHED","UNRESOLVED","INFORMATION"))
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

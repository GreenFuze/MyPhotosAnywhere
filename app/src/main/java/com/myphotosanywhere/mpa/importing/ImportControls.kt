package com.myphotosanywhere.mpa.importing

import androidx.core.net.toUri
import android.accounts.AccountManager
import android.app.Activity
import android.app.Application
import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.provider.Settings
import androidx.core.content.ContextCompat
import android.content.Intent
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
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
    val history=ImportHistory(app)
    val dismissed=history.observeDismissed().stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptySet())
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
    fun add(selection: TakeoutSelection,tree: Boolean,existing: String?=null) = run {
        TakeoutAccess(app).retain(selection)
        app.imports.addTakeout(selection.uris,tree,existing)
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

enum class ImportSurface { NEW, ACTIVITY, HISTORY }

@Composable
fun ImportControls(surface: ImportSurface=ImportSurface.NEW,model: ImportModel=viewModel()) {
    val context=LocalContext.current
    val savedJobs by model.jobs.collectAsStateWithLifecycle()
    val dismissed by model.dismissed.collectAsStateWithLifecycle()
    var showDismissed by rememberSaveable { mutableStateOf(false) }
    var historyLimit by rememberSaveable { mutableIntStateOf(10) }
    val jobs=savedJobs.filter { showDismissed || it.id !in dismissed || !ImportHistory.canDismiss(it.state) }
    val items by model.items.collectAsStateWithLifecycle()
    val sidecars by model.sidecars.collectAsStateWithLifecycle()
    val sources by model.sources.collectAsStateWithLifecycle()
    val downloads by model.downloads.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val message by model.message.collectAsStateWithLifecycle()
    val settings by model.app.settings.state.collectAsStateWithLifecycle()
    val notices=remember(context) { ImportNotifications(context) }
    var notificationsEnabled by remember { mutableStateOf(notices.enabled()) }
    var startAfterPermission by rememberSaveable { mutableStateOf<String?>(null) }
    androidx.lifecycle.compose.LifecycleEventEffect(androidx.lifecycle.Lifecycle.Event.ON_RESUME) {
        notificationsEnabled=notices.enabled()
    }
    val notificationPermission=rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        notificationsEnabled=notices.enabled()
        if(!granted) model.message.value="Import continues. Allow MPA notifications in Android Settings to see background progress."
        startAfterPermission?.let { id -> model.run { model.app.imports.start(id) } }
        startAfterPermission=null
    }
    fun requestNotifications() { if(Build.VERSION.SDK_INT>=33) notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS) }
    fun startImport(id: String) {
        if(Build.VERSION.SDK_INT>=33 && ContextCompat.checkSelfPermission(context,Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED) {
            startAfterPermission=id; requestNotifications()
        } else model.run { model.app.imports.start(id) }
    }
    var expandedJob by rememberSaveable { mutableStateOf<String?>(null) }
    var preview by remember { mutableStateOf<String?>(null) }
    var targetJob by rememberSaveable { mutableStateOf<String?>(null) }
    var driveZip by rememberSaveable { mutableStateOf(false) }
    var discardZip by rememberSaveable { mutableStateOf<String?>(null) }
    fun open(uri: String) {
        try { context.startActivity(Intent(Intent.ACTION_VIEW,uri.toUri())) }
        catch(_: Exception) { model.message.value="Install a browser to open Google Photos, then use Open picker below." }
    }
    val resolution=rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) {
        model.authorized(it.data,::open)
    }
    val account=rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        val email=it.data?.getStringExtra(AccountManager.KEY_ACCOUNT_NAME)
        if(it.resultCode==Activity.RESULT_OK && !email.isNullOrBlank()) model.authorize(email,{ request -> resolution.launch(request) },::open)
    }
    val zip=rememberLauncherForActivityResult(TakeoutSelectionContract()) { if(it!=null) model.add(it,false,targetJob) }
    val folder=rememberLauncherForActivityResult(TakeoutSelectionContract(tree=true)) { if(it!=null) model.add(it,true,targetJob) }
    val jobControls: @Composable (ImportJob) -> Unit = { job ->
        val entries=items.filter { it.job==job.id }
        val download=downloads.find { it.job==job.id }
        val progress=ImportProgress.from(job,entries)
        ImportJobProgress(job.id,progress)
        job.error?.takeIf { it!=progress.detail }?.let { Text(it,color=MaterialTheme.colorScheme.error) }
        Text("Destination: ${sources.find { it.key==job.binding }?.let { "${it.folderName} (${it.email})" } ?: "Disconnected original source"}",style=MaterialTheme.typography.bodySmall)
        TextButton(onClick={ expandedJob=if(expandedJob==job.id) null else job.id },modifier=Modifier.heightIn(min=48.dp).testTag("import:details:${job.id}")) { Text(if(expandedJob==job.id) "Hide details" else "View details") }
        if(ImportErrorDismissal.canDismiss(job) && ImportErrorDismissal.hasErrors(job,entries,download))
            TextButton(enabled=!busy,onClick={ model.run { model.app.imports.dismissErrors(job.id) } },
                modifier=Modifier.testTag("dismiss-import-errors:${job.id}")) { Text("Dismiss errors") }
        if(ImportHistory.canDismiss(job.state)) {
            if(job.id in dismissed) TextButton(enabled=!busy,onClick={ model.run { model.history.restore(job.id) } },
                modifier=Modifier.testTag("restore-import:${job.id}")) { Text("Restore import") }
            else TextButton(enabled=!busy,onClick={ model.run { model.history.dismiss(model.app.database,job.id) } },
                modifier=Modifier.testTag("dismiss-import:${job.id}")) { Text("Dismiss import") }
        }
        if(expandedJob==job.id) {
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
            if(it.state!="CLEANED" && job.state !in listOf("RUNNING","DOWNLOADING","WAITING_FOR_APP")) {
                TextButton(enabled=!busy,onClick={ discardZip=job.id }) { Text("Delete downloaded ZIP from phone") }
            }
        }
        if(job.kind=="TAKEOUT" && job.state !in listOf("RUNNING","DOWNLOADING","WAITING_FOR_APP","CANCELLED")) {
            TextButton(enabled=!busy,onClick={ targetJob=job.id; zip.launch(Unit) }) { Text("Add ZIP parts to this import") }
            TextButton(enabled=!busy && sources.isNotEmpty(),onClick={ targetJob=job.id; driveZip=true }) { Text("Add Drive ZIP part") }
        }
        if(job.state=="PICKING") {
            Text(job.email)
            TextButton(enabled=!busy,onClick={ job.pickerUri?.let { open(it) } }) { Text("Open picker") }
            Button(enabled=!busy,onClick={ model.run { model.app.imports.checkPicker(job.id) } }) { Text("Check completed selection") }
        } else if(entries.isNotEmpty()) TextButton(enabled=!busy,onClick={ preview=job.id }) { Text("Preview selection and metadata") }
        if(job.state !in listOf("RUNNING","DOWNLOADING","PICKING","COMPLETE","CANCELLED"))
            Button(enabled=!busy,onClick={ startImport(job.id) }) { Text("Import selected / Resume") }
        if(job.state in listOf("RUNNING","DOWNLOADING","WAITING_FOR_APP")) TextButton(enabled=!busy,onClick={ model.run { model.app.imports.pause(job.id) } }) { Text("Pause import") }
        if(job.state !in listOf("COMPLETE","CANCELLED")) TextButton(enabled=!busy,onClick={ model.run { model.app.imports.pause(job.id,true) } }) { Text("Cancel remaining import") }
    }
    }
    val activeStates=listOf("RUNNING","DOWNLOADING","WAITING_FOR_APP")
    Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(12.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text(when(surface) { ImportSurface.NEW -> "Import Google Photos";ImportSurface.ACTIVITY -> "Imports";ImportSurface.HISTORY -> "Import history" },style=MaterialTheme.typography.titleMedium)
        if(surface==ImportSurface.NEW) {
        val destination=sources.find { it.backupEnabled } ?: sources.singleOrNull()
        Text(destination?.let { "New import destination: ${it.folderName} (${it.email})" }
            ?: "Connect a Drive folder, or choose a default when several folders are connected.",style=MaterialTheme.typography.bodySmall)
        Button(enabled=!busy,onClick={
            try { account.launch(AccountPicker.newChooseAccountIntent(AccountPicker.AccountChooserOptions.Builder().setAllowableAccountsTypes(listOf("com.google")).build())) }
            catch(_: Exception) { model.message.value="Google account selection requires Google Play services." }
        },modifier=Modifier.testTag("import-photos-picker")) { Text("Choose from Google Photos") }
        OutlinedButton(enabled=!busy,onClick={ targetJob=null; zip.launch(Unit) },modifier=Modifier.testTag("import-takeout")) { Text("Import Takeout ZIP parts") }
        TextButton(enabled=!busy && sources.isNotEmpty(),onClick={ targetJob=null; driveZip=true }) { Text("Import Takeout ZIP from Drive") }
        TextButton(enabled=!busy,onClick={ targetJob=null; folder.launch(Unit) }) { Text("Import extracted Takeout folder") }
        Text(if(settings.sidecarBackup) "Imported metadata uses XMP annotation backup; check Settings > Backup and metadata."
            else "Imported metadata is local only until you enable Settings > Backup and metadata.",style=MaterialTheme.typography.bodySmall)
        var explain by rememberSaveable { mutableStateOf(false) }
        TextButton(onClick={ explain=!explain },modifier=Modifier.heightIn(min=48.dp)) { Text(if(explain) "Hide import information" else "How imports work") }
        if(explain) {
        Text("Takeout ZIPs are read locally. Importing uploads the selected files to your Drive destination and requires an internet connection.",style=MaterialTheme.typography.bodySmall)
        Text("Imports continue in the background with live progress here and in notifications. Android may pause work for network or system limits; checkpoints are preserved.",style=MaterialTheme.typography.bodySmall)
        if(!notificationsEnabled) {
            Text("Background progress notifications are off.")
            if(Build.VERSION.SDK_INT>=33 && ContextCompat.checkSelfPermission(context,Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)
                TextButton(onClick={ requestNotifications() }) { Text("Allow import notifications") }
            TextButton(onClick={ context.startActivity(Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                .putExtra(Settings.EXTRA_APP_PACKAGE,context.packageName)) }) { Text("Open notification settings") }
        }
        Text("Imports go to the default Drive destination. Your only connected folder is chosen automatically. User-selected source files remain untouched. Media limit: 250 MiB per file; catalogue: 10,000 assets / 2 MiB.")
        Text("For ZIPs, use local/USB storage. Cloud document providers may download the archive internally. Picker omits location metadata and may transcode videos.",style=MaterialTheme.typography.bodySmall)
        }
        }
        if(busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        message?.let { Text(it,color=MaterialTheme.colorScheme.error); TextButton(onClick={ model.message.value=null }) { Text("Dismiss") } }
        if(surface!=ImportSurface.NEW && savedJobs.any { it.id in dismissed && ImportHistory.canDismiss(it.state) })
            TextButton(onClick={ showDismissed=!showDismissed },modifier=Modifier.testTag("toggle-dismissed-imports")) {
                Text(if(showDismissed) "Hide dismissed imports" else "Show dismissed imports")
            }
        val historyJobs=when(surface) {
            ImportSurface.NEW -> jobs.filter { it.state in listOf("PREVIEW","PICKING") }
            ImportSurface.ACTIVITY -> jobs.sortedByDescending { it.state in activeStates }
            ImportSurface.HISTORY -> jobs.filter { ImportHistory.canDismiss(it.state) }
        }
        if(historyJobs.isEmpty()) Text(when(surface) { ImportSurface.NEW -> "Choose a source to start a new import.";ImportSurface.ACTIVITY -> "No imports need attention.";ImportSurface.HISTORY -> "No finished imports." })
        historyJobs.take(historyLimit).forEach { job ->
            key(job.id) { jobControls(job) }
        }
        if(historyJobs.size>historyLimit) TextButton(onClick={ historyLimit+=10 },modifier=Modifier.testTag("more-import-history")) {
            Text("Show more imports")
        }
        if(surface==ImportSurface.NEW && sidecars.isNotEmpty()) {
            Text("Google metadata found in Drive",style=MaterialTheme.typography.titleMedium)
            Text("Conversion keeps existing MPA values, backs up the full Google record in XMP, then moves verified JSON files to Drive trash. Enable Settings > Backup and metadata first. Ambiguous or conflicting JSON stays in Drive.")
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
    if(driveZip) DriveTakeoutBrowser(model,targetJob) { driveZip=false }
    discardZip?.let { id -> AlertDialog(onDismissRequest={ discardZip=null },title={ Text("Delete downloaded ZIP?") },text={
        Text("This removes MPA's downloaded ZIP and unfinished transfer copies from this phone. Verified imports remain in Drive. Skipped or unfinished files will need the source ZIP again; an expired link may require a new export.")
    },confirmButton={ TextButton(onClick={ discardZip=null; model.run { model.app.imports.discardDownloadedZip(id) } }) { Text("Delete phone ZIP") } },
        dismissButton={ TextButton(onClick={ discardZip=null }) { Text("Keep ZIP") } }) }
    preview?.let { jobId ->
        val job=jobs.find { it.id==jobId }
        val entries=items.filter { it.job==jobId }
        ImportPreviewDialog(job,entries,busy,
            onSelect={ id,selected -> model.run { model.app.imports.select(id,selected) } },onClose={ preview=null })
    }
}

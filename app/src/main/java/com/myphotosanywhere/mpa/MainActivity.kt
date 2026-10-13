package com.myphotosanywhere.mpa

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.activity.compose.BackHandler
import androidx.activity.enableEdgeToEdge
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.clickable
import androidx.compose.ui.semantics.*
import com.myphotosanywhere.mpa.selection.*
import com.myphotosanywhere.mpa.search.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.ui.text.style.TextOverflow
import com.myphotosanywhere.mpa.ui.*
import com.myphotosanywhere.mpa.timeline.*
import java.time.Instant
import java.time.ZoneOffset
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.myphotosanywhere.mpa.core.AiProvider
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.binding
import com.myphotosanywhere.mpa.drive.DriveStorageScreen
import com.myphotosanywhere.mpa.edit.EditorScreen
import com.myphotosanywhere.mpa.library.*

class MainActivity: ComponentActivity() {
    private var importNavigation by mutableIntStateOf(0)
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        if(intent.action==com.myphotosanywhere.mpa.importing.ImportNotifications.ACTION_OPEN) importNavigation++
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        if(intent.action==com.myphotosanywhere.mpa.importing.ImportNotifications.ACTION_OPEN) importNavigation++
        setContent {
            val colors=if(isSystemInDarkTheme()) darkColorScheme(primary=Color(0xFF8BD5BE),secondary=Color(0xFFB1CCC3),
                background=Color(0xFF101412),surface=Color(0xFF101412))
            else lightColorScheme(primary=Color(0xFF166D5B),secondary=Color(0xFF49675F),background=Color.White,surface=Color.White)
            MaterialTheme(colorScheme=colors) { LibraryScreen(importNavigation=importNavigation) }
        }
    }
}
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun LibraryScreen(importNavigation: Int=0,model: LibraryViewModel = viewModel(),features: LibraryFeaturesModel = viewModel(),timeline: TimelineViewModel = viewModel(),selection: SelectionViewModel = viewModel(),search: SearchViewModel = viewModel(),searchTimeline: TimelineViewModel = viewModel(key="search-timeline")) {
    val searchDraft by search.draft.collectAsStateWithLifecycle()
    val searchApplied by search.applied.collectAsStateWithLifecycle()
    val searchRecents by search.recents.collectAsStateWithLifecycle()
    val searchNotice by search.notice.collectAsStateWithLifecycle()
    val searchViewer by searchTimeline.viewer.collectAsStateWithLifecycle()
    val searchMessage by searchTimeline.message.collectAsStateWithLifecycle()
    val selectedTargets by selection.targets.collectAsStateWithLifecycle()
    val selectionBusy by selection.busy.collectAsStateWithLifecycle()
    val selectionResult by selection.result.collectAsStateWithLifecycle()
    val selectionTotal by selection.total.collectAsStateWithLifecycle()
    val selectionProgress by selection.progress.collectAsStateWithLifecycle()
    val selectionNotice by selection.notice.collectAsStateWithLifecycle()
    val shareIntent by selection.share.collectAsStateWithLifecycle()
    var selectionAlbums by rememberSaveable { mutableStateOf(false) }
    val selecting=selectedTargets.isNotEmpty()
    val selectionLocked=selectionBusy || selectionResult!=null
    val photos by model.photos.collectAsStateWithLifecycle()
    val handoffs by model.handoffs.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val featureBusy by features.busy.collectAsStateWithLifecycle()
    val message by model.message.collectAsStateWithLifecycle()
    val featureMessage by features.message.collectAsStateWithLifecycle()
    val albums by features.albums.collectAsStateWithLifecycle()
    val memberships by features.memberships.collectAsStateWithLifecycle()
    val people by features.people.collectAsStateWithLifecycle()
    val faces by features.faces.collectAsStateWithLifecycle()
    val cloud by features.cloud.collectAsStateWithLifecycle()
    val sources by features.sources.collectAsStateWithLifecycle()
    val sync by features.sync.collectAsStateWithLifecycle()
    val settings by features.settings.collectAsStateWithLifecycle()
    val navigation=rememberSaveable(saver=ShellNavigation.Saver) { ShellNavigation() }
    val tab=navigation.destination.id
    LaunchedEffect(importNavigation) { if(importNavigation>0) navigation.open(ShellDestination.STORAGE) }
    var selectedId by rememberSaveable { mutableStateOf<String?>(null) }
    var resultHandoffId by rememberSaveable { mutableStateOf<String?>(null) }
    var showShare by rememberSaveable { mutableStateOf(false) }
    var viewerDetailsOpen by rememberSaveable { mutableStateOf(false) }
    var viewerId by rememberSaveable { mutableStateOf<String?>(null) }
    var pendingViewKey by rememberSaveable { mutableStateOf<String?>(null) }
    var editorId by rememberSaveable { mutableStateOf<String?>(null) }
    var filter by rememberSaveable { mutableStateOf("All") }
    var albumId by rememberSaveable { mutableStateOf<String?>(null) }
    var sourceKey by rememberSaveable { mutableStateOf<String?>(null) }
    var personId by rememberSaveable { mutableStateOf<String?>(null) }
    var showFilters by rememberSaveable { mutableStateOf(false) }
    val timelineViewer by timeline.viewer.collectAsStateWithLifecycle()
    val timelineMessage by timeline.message.collectAsStateWithLifecycle()
    LaunchedEffect(tab,selectedId,viewerId,pendingViewKey,editorId) {
        val legacy=tab !in listOf(0,1,5) || selectedId!=null || viewerId!=null || pendingViewKey!=null || editorId!=null
        model.legacyLoading(tab !in listOf(0,1,5) || viewerId!=null || pendingViewKey!=null);features.legacyLoading(legacy)
    }
    var jumpToDate by rememberSaveable { mutableStateOf(false) }
    val photosGrid=rememberLazyGridState()
    val searchGrid=rememberLazyGridState()
    val searchSuggestions=rememberLazyGridState()
    val collectionGrid=rememberLazyGridState()
    val collectionsHubGrid=rememberLazyGridState()
    var exportId by rememberSaveable { mutableStateOf<String?>(null) }
    val snackbar=remember { SnackbarHostState() }
    val context=LocalContext.current
    val keyboard=LocalSoftwareKeyboardController.current
    val focusManager=LocalFocusManager.current
    val addPhotos=rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(100)) { model.importPhotos(it) }
    val importResult=rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if(uri!=null) resultHandoffId?.let { model.importResult(uri,it) }; resultHandoffId=null
    }
    val export=rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        if(uri!=null) exportId?.let { features.export(it,uri) }; exportId=null
    }
    fun pickResult(handoff: Handoff) { resultHandoffId=handoff.id; importResult.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) }
    fun selectPhoto(id: String) { selectedId=id; showShare=false }
    androidx.lifecycle.compose.LifecycleEventEffect(androidx.lifecycle.Lifecycle.Event.ON_RESUME) { features.resumeForegroundImports(); features.refreshCloudAutomatically() }
    LaunchedEffect(sources.map { it.key }) { if(sources.isNotEmpty()) features.refreshCloudAutomatically() }
    val presence=remember(cloud,sources,sourceKey) { GalleryVisibility.Index(cloud.filter { sourceKey==null || it.bindingKey==sourceKey },sources.map { it.key }.toSet()) }
    var selectedAttempt by remember(selectedId) { mutableIntStateOf(0) }
    var selectedProblem by remember(selectedId) { mutableStateOf<String?>(null) }
    val selectedState=produceState<Photo?>(null,selectedId,selectedAttempt) {
        val id=selectedId
        selectedProblem=null
        if(id!=null) try {
            features.app.timeline.photo(id).collect {
                value=it
                selectedProblem=if(it==null) "This photo is no longer available." else null
            }
        } catch(cancelled: kotlinx.coroutines.CancellationException) { throw cancelled }
        catch(error: android.database.sqlite.SQLiteException) {
            selectedProblem=error.message ?: "Cannot read this photo. Retry."
        }
    }
    val selected=selectedState.value?.takeIf { it.id==selectedId }
    val imeVisible=WindowInsets.ime.getBottom(LocalDensity.current)>0
    fun submitSearch() { search.submit();focusManager.clearFocus(force=true);keyboard?.hide() }
    LaunchedEffect(selecting) { if(selecting) { focusManager.clearFocus(force=true);keyboard?.hide() } }
    LaunchedEffect(searchNotice) { searchNotice?.let { snackbar.showSnackbar(it);search.clearNotice() } }
    LaunchedEffect(searchMessage) { searchMessage?.let { snackbar.showSnackbar(it);searchTimeline.clearMessage() } }
    LaunchedEffect(timelineMessage) { timelineMessage?.let { snackbar.showSnackbar(it);timeline.clearMessage() } }
    LaunchedEffect(message) { message?.let { snackbar.showSnackbar(it); model.clearMessage() } }
    LaunchedEffect(featureMessage) { featureMessage?.let { snackbar.showSnackbar(it); features.clearMessage() } }
    LaunchedEffect(selectionNotice) { selectionNotice?.let { snackbar.showSnackbar(it);selection.clearNotice() } }
    LaunchedEffect(shareIntent) { shareIntent?.let { context.startActivity(it);selection.shareLaunched() } }
    BackHandler(enabled=selecting && !selectionBusy && selectionResult==null && !selectionAlbums) { selection.clear() }
    fun back() {
        if(tab==0 && navigation.primary==ShellDestination.COLLECTIONS) {
            albumId=null;personId=null;filter="All"
        }
        navigation.back()
    }
    BackHandler(enabled=!selecting && selectedId==null && viewerId==null && editorId==null && pendingViewKey==null &&
        !showFilters && navigation.canGoBack,onBack=::back)
    BackHandler(enabled=tab==5 && imeVisible && !selecting && !showFilters && searchViewer==null) { focusManager.clearFocus(force=true);keyboard?.hide() }
    val title=when(navigation.destination) {
        ShellDestination.PHOTOS -> albums.find { it.id==albumId }?.name ?: people.find { it.id==personId }?.name
            ?: if(filter=="All") "Photos" else filter
        else -> navigation.destination.title
    }
    var navigationHeight by remember { mutableIntStateOf(0) }
    val navigationClearance=with(LocalDensity.current) { navigationHeight.toDp() }
    val scrollClearance=if(selecting) 0.dp else navigationClearance
    val primaryBrand=navigation.destination==navigation.primary && tab in listOf(0,1)
    Scaffold(topBar={ if(selecting) SelectionTopBar(selectedTargets.size,selectionBusy,selection::clear) else if(tab==5) SearchTopBar(searchDraft.text,searchDraft.filtered,search::text,::submitSearch,
        onBack={ if(imeVisible) { focusManager.clearFocus(force=true);keyboard?.hide() } else back() },onFilters={ showFilters=true }) else ShellTopBar(title,navigation.canGoBack && !primaryBrand,::back,canAddPhotos=!busy,
        onAddPhotos={ if(!busy) addPhotos.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo)) },
        onStorage={ navigation.open(ShellDestination.STORAGE) },onActivity={ navigation.open(ShellDestination.STORAGE) },
        onFilters={ showFilters=true },showFilters=tab==0 || tab==5,
        filtersActive=filter!="All" || sourceKey!=null,needsAttention=sync.any { it.error!=null },
        showTitle=!primaryBrand || filter!="All" || albumId!=null || personId!=null)
    },snackbarHost={ SnackbarHost(snackbar,Modifier.padding(bottom=navigationClearance)) }) { padding ->
        Box(Modifier.fillMaxSize().padding(padding).consumeWindowInsets(padding).imePadding()) {
        // Browsing scrolls beneath floating navigation. Selection reserves the opaque toolbar viewport.
        Column(Modifier.fillMaxSize().then(if(selecting || tab !in listOf(0,1,5)) Modifier.padding(bottom=navigationClearance) else Modifier)) {
            if(busy || featureBusy) LinearProgressIndicator(Modifier.fillMaxWidth())
            when(tab) {
                0 -> {
                    ShellImportActivity(onOpen={ navigation.open(ShellDestination.STORAGE) })
                    val activeSync=sync.filter { sourceKey==null || it.bindingKey==sourceKey }
                    if(activeSync.any { it.running || it.error!=null }) TextButton(
                        onClick={ navigation.open(ShellDestination.STORAGE) },modifier=Modifier.fillMaxWidth().testTag("shell:sync")) {
                        Text(if(activeSync.any { it.error!=null }) "Drive sync needs attention" else "Updating photo library…",
                            maxLines=1,overflow=TextOverflow.Ellipsis)
                    }
                    TimelineGrid(TimelineQuery(filter,sourceKey,albumId,personId),timeline,features,
                        if(albumId!=null || personId!=null) collectionGrid else photosGrid,scrollClearance,
                        modifier=Modifier.weight(1f).fillMaxWidth(),selection=selectedTargets,onToggle=selection::toggle,selectionLocked=selectionLocked,hasSources=sources.isNotEmpty(),
                        onConnect={ navigation.open(ShellDestination.STORAGE) },
                        onClearFilters={ filter="All";sourceKey=null;albumId=null;personId=null },onRefresh=features::refreshCloud)
                }
                5 -> {
                    ShellImportActivity(onOpen={ navigation.open(ShellDestination.STORAGE) })
                    SearchScreen(searchDraft,searchApplied,searchRecents,search,searchTimeline,features,searchGrid,searchSuggestions,
                        albums,sources,scrollClearance,selectedTargets,selection::toggle,selectionLocked,onFilters={ showFilters=true },
                        onPeople={ navigation.open(ShellDestination.PEOPLE) },onPlaces={ navigation.open(ShellDestination.PLACES) },
                        onAlbums={ navigation.open(ShellDestination.COLLECTIONS) },onSubmit=::submitSearch,
                        onConnect={ navigation.open(ShellDestination.STORAGE) },modifier=Modifier.weight(1f).fillMaxWidth())
                }
                1 -> AlbumsScreen(features,albums,collectionsHubGrid,onAlbum={ albumId=it;personId=null;filter="All";navigation.open(ShellDestination.PHOTOS) },
                    onFavorites={ filter="Favorites";albumId=null;personId=null;navigation.open(ShellDestination.PHOTOS) },
                    onArchive={ filter="Archive";albumId=null;personId=null;navigation.open(ShellDestination.PHOTOS) },
                    onActivity={ navigation.open(ShellDestination.AI_ACTIVITY) },
                    onPeople={ navigation.open(ShellDestination.PEOPLE) },onPlaces={ navigation.open(ShellDestination.PLACES) },
                    onOffline={ filter="Offline";albumId=null;personId=null;navigation.open(ShellDestination.PHOTOS) },
                    onEdits={ filter="Edits";albumId=null;personId=null;navigation.open(ShellDestination.PHOTOS) },bottomClearance=navigationClearance)
                6 -> {
                    LazyColumn(contentPadding=PaddingValues(16.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
                        if(handoffs.isEmpty()) item { Text("Open a photo and choose Edit with AI. Saved handoffs appear here.") }
                        items(handoffs,key={ it.id }) { handoff -> Card(Modifier.fillMaxWidth()) {
                            Column(Modifier.padding(12.dp)) {
                                Text(AiProvider.entries.find { it.name==handoff.provider }?.title ?: handoff.provider,style=MaterialTheme.typography.titleMedium)
                                Text(handoff.instructions,maxLines=3)
                                Row { Button(enabled=!busy,onClick={ pickResult(handoff) }) { Text("Import result") }
                                    TextButton(enabled=!busy,onClick={ model.dismiss(handoff) }) { Text("Dismiss") } }
                            }
                        } }
                    }
                }
                2 -> PeopleScreen(features,people,faces,settings.faceIndexing,onPerson={ personId=it; albumId=null; filter="All"; navigation.open(ShellDestination.PHOTOS) },onPhoto=::selectPhoto)
                3 -> PlacesScreen(photos,settings.onlineMaps,{ features.updateSettings(settings.copy(onlineMaps=it)) },::selectPhoto)
                4 -> DriveStorageScreen(photos)
            }
        }
        if(selecting) SelectionBottomBar(selectionBusy,(if(tab==5) searchApplied.filter else filter)=="Archive",tab==0 && albumId!=null,onAction={ kind ->
            if(kind==BulkKind.ALBUM) selectionAlbums=true
            else selection.execute(BulkAction(kind,if(kind==BulkKind.REMOVE_ALBUM) albumId else null))
        },modifier=Modifier.align(Alignment.BottomCenter).onSizeChanged { navigationHeight=it.height })
        else ShellBottomBar(navigation.activePrimary,onSelect={ destination ->
            if(destination.primary && destination!=ShellDestination.SEARCH) { albumId=null;personId=null;filter="All" }
            navigation.select(destination)
        },modifier=Modifier.align(Alignment.BottomCenter).testTag("shell:navigation").onSizeChanged { navigationHeight=it.height })
        }
    }
    SelectionDialogs(selection,albums,selectionAlbums,{ selectionAlbums=false },selectionResult,selectionBusy,selectionProgress,selectionTotal)
    if(showFilters && tab==5) SearchFilters(searchDraft,sources,onApply={ search.update(it);submitSearch();showFilters=false },onClose={ showFilters=false })
    if(showFilters && tab!=5) ModalBottomSheet(onDismissRequest={ showFilters=false }) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(16.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            Text("Filter photos",style=MaterialTheme.typography.titleLarge)
            if(tab==0) TextButton(onClick={ showFilters=false;jumpToDate=true },modifier=Modifier.heightIn(min=48.dp).testTag("timeline:jump")) { Text("Jump to date") }
            listOf("All","Favorites","Offline","Videos","Edits","Archive").forEach { option ->
                FilterChip(selected=filter==option,onClick={ filter=option },label={ Text(option) })
            }
            Text("Sources",style=MaterialTheme.typography.titleMedium)
            FilterChip(selected=sourceKey==null,onClick={ sourceKey=null },label={ Text("All sources") })
            sources.forEach { source -> FilterChip(selected=sourceKey==source.key,onClick={ sourceKey=source.key },
                label={ Text("${source.folderName} · ${source.email}") }) }
            Button(onClick={ showFilters=false },modifier=Modifier.align(Alignment.End)) { Text("Done") }
        }
    }
    if(jumpToDate) {
        val date=rememberDatePickerState()
        DatePickerDialog(onDismissRequest={ jumpToDate=false },confirmButton={
            TextButton(enabled=date.selectedDateMillis!=null,onClick={
                date.selectedDateMillis?.let { timeline.jump(Instant.ofEpochMilli(it).atZone(ZoneOffset.UTC).toLocalDate(),java.time.ZoneId.systemDefault()) }
                jumpToDate=false
            }) { Text("Jump") }
        },dismissButton={ TextButton(onClick={ jumpToDate=false }) { Text("Cancel") } }) { DatePicker(date) }
    }
    searchViewer?.let { session -> TimelineViewer(session,searchTimeline,features,
        onEdit={ searchTimeline.closeViewer();selectedId=it;editorId=it },
        onDetails={ selectedId=it;viewerDetailsOpen=true },obscured=viewerDetailsOpen || editorId!=null || showShare,
        onSource={ searchTimeline.closeViewer();navigation.open(ShellDestination.STORAGE) }) }
    timelineViewer?.let { session -> TimelineViewer(session,timeline,features,
        onEdit={ timeline.closeViewer();selectedId=it;editorId=it },
        onDetails={ selectedId=it;viewerDetailsOpen=true },obscured=viewerDetailsOpen || editorId!=null || showShare,
        onSource={ timeline.closeViewer();navigation.open(ShellDestination.STORAGE) }) }
    pendingViewKey?.let { key -> DriveOriginalDialog(cloud.find { it.key==key },features,onClose={ pendingViewKey=null;selectedId=null },onReady={
        pendingViewKey=null;selectedId=it;viewerId=it
    }) }
    viewerId?.let { id -> PhotoViewer(photos.filter { it.id==id || presence.showLocal(it,filter,albumId!=null || personId!=null) },id,features,
        onClose={ viewerId=null;selectedId=null;viewerDetailsOpen=false },obscured=viewerDetailsOpen || editorId!=null || showShare,onCurrent={ selectedId=it },
        onEdit={ viewerId=null; selectedId=it; editorId=it },onDetails={ selectedId=it;viewerDetailsOpen=true }) }
    if(selectedId!=null && selected==null && editorId==null && (viewerId==null || viewerDetailsOpen) && pendingViewKey==null) {
        AlertDialog(onDismissRequest={ selectedId=null;viewerDetailsOpen=false },title={ Text("Photo details") },
            text={ Text(selectedProblem ?: "Loading photo details…") },
            confirmButton={ if(selectedProblem!=null) TextButton(onClick={ selectedAttempt++ }) { Text("Retry") } },
            dismissButton={ TextButton(onClick={ selectedId=null;viewerDetailsOpen=false }) { Text("Close") } })
    }
    if(selected!=null && !showShare && editorId==null && (viewerId==null || viewerDetailsOpen) && pendingViewKey==null && (timelineViewer==null && searchViewer==null || viewerDetailsOpen)) PhotoDetails(selected,features,albums,memberships,people,faces,
        onClose={ viewerDetailsOpen=false;if(viewerId==null) selectedId=null },onEdit={ editorId=selected.id },onAI={ showShare=true },onExport={
            exportId=selected.id; export.launch(selected.displayName)
        },onResult=handoffs.firstOrNull { it.photoId==selected.id }?.let { h -> { pickResult(h) } },onView=if(viewerId==null && timelineViewer==null && searchViewer==null) ({ viewerId=selected.id }) else null,showPreview=viewerId==null && timelineViewer==null && searchViewer==null)
    if(selected!=null && showShare) ShareDialog(busy,onDismiss={ showShare=false }) { provider,instructions ->
        model.share(selected,provider,instructions) { intent -> context.startActivity(intent); showShare=false }
    }
    editorId?.let { id -> EditorScreen(id,onClose={ editorId=null },onSaved={ saved -> editorId=null; selectedId=saved }) }
}

@Composable
private fun ShareDialog(busy: Boolean,onDismiss: ()->Unit,onShare: (AiProvider,String)->Unit) {
    var provider by rememberSaveable { mutableStateOf(AiProvider.CHATGPT.name) }
    var instructions by rememberSaveable { mutableStateOf("") }
    val selected=AiProvider.valueOf(provider)
    AlertDialog(onDismissRequest={ if(!busy) onDismiss() },title={ Text("Edit with your AI app") },text={
        Column(Modifier.verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            AiProvider.entries.forEach { option -> Row(Modifier.clickable { provider=option.name },verticalAlignment=Alignment.CenterVertically) {
                RadioButton(selected=provider==option.name,onClick={ provider=option.name },enabled=!busy); Text(option.title)
            } }
            Text(selected.guidance,style=MaterialTheme.typography.bodySmall)
            OutlinedTextField(instructions,{ if(it.length<=4000) instructions=it },label={ Text("Describe your edit") },minLines=3,enabled=!busy)
            Text("Shares a preview up to 2048 pixels and copies instructions to your clipboard. GPS is removed. Save the AI output and import it here afterward.",style=MaterialTheme.typography.bodySmall)
        }
    },confirmButton={ Button(enabled=!busy && instructions.isNotBlank(),onClick={ onShare(selected,instructions) }) { Text("Continue to share sheet") } },
        dismissButton={ TextButton(enabled=!busy,onClick=onDismiss) { Text("Cancel") } })
}

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
import androidx.compose.ui.platform.LocalView
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
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalInputModeManager
import androidx.compose.ui.input.InputMode
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.type
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.myphotosanywhere.mpa.core.AiProvider
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.binding
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
    com.myphotosanywhere.mpa.device.DeviceLibraryLifecycle()
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
    val trash: com.myphotosanywhere.mpa.trash.TrashViewModel=viewModel()
    val trashBusy by trash.busy.collectAsStateWithLifecycle()
    val trashConfirmation by trash.confirmation.collectAsStateWithLifecycle()
    val selecting=selectedTargets.isNotEmpty()
    val selectionLocked=selectionBusy || selectionResult!=null || trashBusy || trashConfirmation!=null
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
    LaunchedEffect(importNavigation) { if(importNavigation>0) navigation.open(ShellDestination.ACTIVITY) }
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
    var importedPersonKey by rememberSaveable { mutableStateOf<String?>(null) }
    val importedLabel=remember(importedPersonKey) { importedPersonKey?.let { requireNotNull(com.myphotosanywhere.mpa.people.ImportedPersonLabel.fromKey(it)) } }
    LaunchedEffect(importedPersonKey) {
        val oldKey=importedPersonKey ?: return@LaunchedEffect
        try {
            val alias=kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
                features.app.personLabels.drain()
                features.app.database.personLabels().alias(oldKey)
            }
            if(importedPersonKey==oldKey) {
                importedPersonKey=null;personId=alias?.personId?.takeUnless { alias.ignored }
                if(personId==null) {
                    features.message.value="This person is no longer available. Choose a person from People."
                    navigation.open(ShellDestination.PEOPLE)
                }
            }
        } catch(cancelled: kotlinx.coroutines.CancellationException) { throw cancelled }
        catch(error: android.database.sqlite.SQLiteException) { features.message.value=error.message ?: "Cannot restore this person. Retry People." }
    }
    var searchOrigin by rememberSaveable { mutableStateOf<Bundle?>(null) }
    var showFilters by rememberSaveable { mutableStateOf(false) }
    var selectionBackup by rememberSaveable { mutableStateOf(false) }
    val timelineViewer by timeline.viewer.collectAsStateWithLifecycle()
    val timelineMessage by timeline.message.collectAsStateWithLifecycle()
    LaunchedEffect(tab,selectedId,viewerId,pendingViewKey,editorId) {
        val legacy=tab !in listOf(0,1,5) || selectedId!=null || viewerId!=null || pendingViewKey!=null || editorId!=null
        model.legacyLoading(tab in listOf(3,11) || viewerId!=null || pendingViewKey!=null);features.legacyLoading(legacy)
    }
    var jumpToDate by rememberSaveable { mutableStateOf(false) }
    val searchSuggestions=rememberLazyGridState()
    val collectionsHubGrid=rememberLazyGridState()
    val peopleList=androidx.compose.foundation.lazy.rememberLazyListState()
    var exportId by rememberSaveable { mutableStateOf<String?>(null) }
    val snackbar=remember { SnackbarHostState() }
    val context=LocalContext.current
    val keyboard=LocalSoftwareKeyboardController.current
    val focusManager=LocalFocusManager.current
    val rootView=LocalView.current
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
    // Animated Compose inset height can remain positive after IME submission has hidden the keyboard.
    // Resolve Back against current platform visibility, so a hiding animation cannot swallow navigation.
    fun dismissKeyboardIfOpen(): Boolean {
        val open=androidx.core.view.ViewCompat.getRootWindowInsets(rootView)
            ?.isVisible(androidx.core.view.WindowInsetsCompat.Type.ime())==true
        if(open) { focusManager.clearFocus(force=true);keyboard?.hide() }
        return open
    }
    fun submitSearch() { search.submit();focusManager.clearFocus(force=true);keyboard?.hide() }
    // A destination change ends its text-entry session. Search controls its own focus/IME flow.
    LaunchedEffect(tab) { if(tab!=ShellDestination.SEARCH.id) { focusManager.clearFocus(force=true);keyboard?.hide() } }

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
            albumId=null;personId=null;importedPersonKey=null;filter="All"
        }
        navigation.back()
        if(navigation.activePrimary!=ShellDestination.SEARCH) searchOrigin?.let { savedOrigin ->
            val origin=GalleryContext.restore(savedOrigin)
            filter=origin.filter;sourceKey=origin.source;albumId=origin.album;personId=origin.person;importedPersonKey=origin.importedPerson;searchOrigin=null
        }
    }
    BackHandler(enabled=!selecting && selectedId==null && viewerId==null && editorId==null && pendingViewKey==null &&
        !showFilters && navigation.canGoBack,onBack=::back)
    BackHandler(enabled=tab==5 && imeVisible && !selecting && !showFilters && searchViewer==null && selectedId==null && editorId==null && pendingViewKey==null) { if(!dismissKeyboardIfOpen()) back() }
    val title=when(navigation.destination) {
        ShellDestination.PHOTOS -> albums.find { it.id==albumId }?.name ?: people.find { it.id==personId }?.name
            ?: importedLabel?.name ?: if(filter=="All") "Photos" else filter
        else -> navigation.destination.title
    }
    val showNavigation=tab in listOf(0,1,2,3,5,6,16)
    var navigationHeight by remember { mutableIntStateOf(0) }
    val navigationClearance=if(showNavigation || selecting) with(LocalDensity.current) { navigationHeight.toDp() } else 0.dp
    val scrollClearance=if(selecting) 0.dp else navigationClearance
    val primaryBrand=navigation.destination==navigation.primary && tab in listOf(0,1)
    val galleryBarState=rememberTopAppBarState()
    val accessibleGallery=touchExplorationEnabled()
    // Android 10 can reset the root's input mode to Keyboard when recreating a
    // touch-driven activity. Only actual navigation keys request pinned keyboard chrome.
    var keyboardRequested by rememberSaveable { mutableStateOf(false) }
    val rootInputMode=LocalInputModeManager.current.inputMode
    val keyboardNavigation=keyboardRequested && rootInputMode==InputMode.Keyboard
    val galleryInput=Modifier.onPreviewKeyEvent { event ->
        if(event.type==KeyEventType.KeyDown && GalleryBarPolicy.navigationKey(event.nativeKeyEvent.keyCode)) keyboardRequested=true
        false
    }.pointerInput(Unit) {
        awaitEachGesture {
            awaitFirstDown(requireUnconsumed=false,pass=PointerEventPass.Initial)
            keyboardRequested=false
            do { val event=awaitPointerEvent(PointerEventPass.Initial) } while(event.changes.any { it.pressed })
        }
    }
    val galleryScrolling=GalleryBarPolicy.canCollapse(tab,selecting,accessibleGallery,keyboardNavigation)
    val galleryScroll=TopAppBarDefaults.enterAlwaysScrollBehavior(galleryBarState,canScroll={ galleryScrolling })
    val galleryContext=listOf(filter,sourceKey,albumId,personId,importedPersonKey).joinToString("|") { value -> value?.let { "${it.length}:$it" } ?: "-" }
    var previousGalleryContext by rememberSaveable { mutableStateOf(galleryContext) }
    LaunchedEffect(galleryContext,accessibleGallery,keyboardNavigation) {
        if(context.applicationInfo.flags and android.content.pm.ApplicationInfo.FLAG_DEBUGGABLE!=0) android.util.Log.i("GalleryBarRestoration","changed=${previousGalleryContext!=galleryContext} assisted=$accessibleGallery input=$rootInputMode requested=$keyboardRequested offset=${galleryBarState.heightOffset}")
        if(previousGalleryContext!=galleryContext || accessibleGallery || keyboardNavigation) {
            galleryBarState.heightOffset=0f;galleryBarState.contentOffset=0f
            previousGalleryContext=galleryContext
        }
    }
    var lastSelectionCount by remember { mutableIntStateOf(0) }
    SideEffect { if(selecting) lastSelectionCount=selectedTargets.size }
    Scaffold(modifier=if(galleryScrolling) galleryInput.nestedScroll(galleryScroll.nestedScrollConnection) else galleryInput,topBar={ MotionSwap(selecting) { selectionMode -> if(selectionMode) SelectionTopBar(if(selecting) selectedTargets.size else lastSelectionCount,selectionBusy,selection::clear) else if(tab==5) SearchTopBar(searchDraft.text,searchDraft.filtered,search::text,::submitSearch,
        onBack={ if(!dismissKeyboardIfOpen()) back() },onFilters={ showFilters=true }) else ShellTopBar(title,navigation.canGoBack && !primaryBrand,::back,canAddPhotos=!busy,
        onAddPhotos={ if(!busy) addPhotos.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo)) },
        onStorage={ navigation.open(ShellDestination.STORAGE) },onActivity={ navigation.open(ShellDestination.ACTIVITY) },onImport={ navigation.open(ShellDestination.IMPORTS) },
        onFilters={ showFilters=true },showFilters=tab==0 || tab==5,
        filtersActive=filter!="All" || sourceKey!=null,needsAttention=sync.any { it.error!=null },
        showTitle=(!primaryBrand || filter!="All" || albumId!=null || personId!=null || importedLabel!=null) && tab !in listOf(10,17),
        showActions=showNavigation,
        scrollBehavior=if(galleryScrolling) galleryScroll else null)
    } },snackbarHost={ SnackbarHost(snackbar,Modifier.padding(bottom=navigationClearance)) }) { padding ->
        Box(Modifier.fillMaxSize().padding(padding).consumeWindowInsets(padding).imePadding()) {
        // Browsing scrolls beneath floating navigation. Selection reserves the opaque toolbar viewport.
        Column(Modifier.fillMaxSize().then(if(selecting || tab !in listOf(0,1,5)) Modifier.padding(bottom=navigationClearance) else Modifier)) {
            when(tab) {
                0 -> {
                    ShellImportActivity(onOpen={ navigation.open(ShellDestination.ACTIVITY) },sync=sync)
                    TimelineGrid(TimelineQuery(filter,sourceKey,albumId,personId,importedPerson=importedLabel),timeline,features,
                        scrollClearance,
                        modifier=Modifier.weight(1f).fillMaxWidth(),selection=selectedTargets,onToggle=selection::toggle,onRangeChange=selection::replaceDrag,onBeginSelection=selection::ensureSelected,onSelectionLimit=selection::dragLimit,selectionLocked=selectionLocked,hasSources=sources.isNotEmpty(),
                        onConnect={ navigation.open(ShellDestination.STORAGE) },
                        onClearFilters={ filter="All";sourceKey=null;albumId=null;personId=null;importedPersonKey=null },onRefresh=features::refreshCloud)
                }
                5 -> {
                    ShellImportActivity(onOpen={ navigation.open(ShellDestination.ACTIVITY) },sync=sync)
                    SearchScreen(searchDraft,searchApplied,searchRecents,search,searchTimeline,features,searchSuggestions,
                        albums,sources,scrollClearance,selectedTargets,selection::toggle,selectionLocked,onFilters={ showFilters=true },
                        onPeople={ navigation.open(ShellDestination.PEOPLE) },onPlaces={ navigation.open(ShellDestination.PLACES) },
                        onAlbums={ navigation.open(ShellDestination.COLLECTIONS) },onSubmit=::submitSearch,
                        onConnect={ navigation.open(ShellDestination.STORAGE) },modifier=Modifier.weight(1f).fillMaxWidth(),onRangeChange=selection::replaceDrag,onBeginSelection=selection::ensureSelected,onSelectionLimit=selection::dragLimit)
                }
                1 -> AlbumsScreen(features,albums,collectionsHubGrid,onAlbum={ albumId=it;personId=null;importedPersonKey=null;filter="All";navigation.open(ShellDestination.PHOTOS) },
                    onFavorites={ filter="Favorites";albumId=null;personId=null;importedPersonKey=null;navigation.open(ShellDestination.PHOTOS) },
                    onArchive={ filter="Archive";albumId=null;personId=null;importedPersonKey=null;navigation.open(ShellDestination.PHOTOS) },
                    onActivity={ navigation.open(ShellDestination.AI_ACTIVITY) },
                    onPeople={ navigation.open(ShellDestination.PEOPLE) },onPlaces={ navigation.open(ShellDestination.PLACES) },
                    onOffline={ filter="Offline";albumId=null;personId=null;importedPersonKey=null;navigation.open(ShellDestination.PHOTOS) },
                    onEdits={ filter="Edits";albumId=null;personId=null;importedPersonKey=null;navigation.open(ShellDestination.PHOTOS) },bottomClearance=navigationClearance,onTrash={ navigation.open(ShellDestination.TRASH) })
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
                2 -> PeopleScreen(features,people,faces,settings.faceIndexing,onPerson={ personId=it; importedPersonKey=null; albumId=null; sourceKey=null; filter="All"; navigation.open(ShellDestination.PHOTOS) },onPhoto=::selectPhoto,onSettings={ navigation.open(ShellDestination.RECOGNITION) },listState=peopleList)
                3 -> PlacesScreen(photos,settings.onlineMaps,{ features.updateSettings(settings.copy(onlineMaps=it)) },::selectPhoto)
                16 -> com.myphotosanywhere.mpa.trash.TrashScreen(trash,navigationClearance,onSources={ navigation.open(ShellDestination.STORAGE) })
                4,7,8,9,10,11,12,13,14,15,17,18 -> com.myphotosanywhere.mpa.management.ManagementScreen(navigation.destination,photos,features,navigation::open)
            }
        }
        MotionVisibility(busy || featureBusy,MotionEdge.TOP,Modifier.align(Alignment.TopCenter)) {
            LinearProgressIndicator(Modifier.fillMaxWidth().testTag("shell:progress"))
        }
        MotionVisibility(selecting,MotionEdge.BOTTOM,Modifier.align(Alignment.BottomCenter)) {
        SelectionBottomBar(selectionBusy,(if(tab==5) searchApplied.filter else filter)=="Archive",tab==0 && albumId!=null,onAction={ kind ->
            if(kind==BulkKind.ALBUM) selectionAlbums=true
            else if(kind==BulkKind.BACKUP) selectionBackup=true
            else selection.execute(BulkAction(kind,if(kind==BulkKind.REMOVE_ALBUM) albumId else null))
        },onTrash={ trash.request(selectedTargets) },modifier=Modifier.onSizeChanged { if(selecting) navigationHeight=it.height })
        }
        MotionVisibility(!selecting && showNavigation,MotionEdge.BOTTOM,Modifier.align(Alignment.BottomCenter)) {
        ShellBottomBar(navigation.activePrimary,onSelect={ destination ->
            if(destination==ShellDestination.SEARCH && navigation.activePrimary!=ShellDestination.SEARCH)
                searchOrigin=GalleryContext(filter,sourceKey,albumId,personId,importedPersonKey).save()
            if(destination.primary && destination!=ShellDestination.SEARCH) { albumId=null;personId=null;importedPersonKey=null;filter="All";searchOrigin=null }
            navigation.select(destination)
        },modifier=Modifier.testTag("shell:navigation").onSizeChanged { if(!selecting) navigationHeight=it.height })
        }
        }
    }
    SelectionDialogs(selection,albums,selectionAlbums,{ selectionAlbums=false },selectionResult,selectionBusy,selectionProgress,selectionTotal)
    if(selectionBackup) com.myphotosanywhere.mpa.device.SelectionBackupDialog(selection,features,{ selectionBackup=false }) { navigation.open(ShellDestination.STORAGE) }
    if(showFilters && tab==5) SearchFilters(searchDraft,sources,onApply={ search.update(it);submitSearch();showFilters=false },onClose={ showFilters=false })
    if(showFilters && tab!=5) ModalBottomSheet(onDismissRequest={ showFilters=false }) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(16.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            Text("Filter photos",style=MaterialTheme.typography.titleLarge)
            val gridDensity by timeline.density.collectAsStateWithLifecycle()
            val gridLayout by timeline.layout.collectAsStateWithLifecycle()
            GalleryLayoutControls(gridLayout) { timeline.changeLayout(it) }
            GridDensityControls(gridDensity) { timeline.changeDensity(it) }
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
        onSource={ searchTimeline.closeViewer();navigation.open(ShellDestination.STORAGE) },onTrash={ trash.request(listOf(SelectionTarget.from(it))) }) }
    timelineViewer?.let { session -> TimelineViewer(session,timeline,features,
        onEdit={ timeline.closeViewer();selectedId=it;editorId=it },
        onDetails={ selectedId=it;viewerDetailsOpen=true },obscured=viewerDetailsOpen || editorId!=null || showShare,
        onSource={ timeline.closeViewer();navigation.open(ShellDestination.STORAGE) },onTrash={ trash.request(listOf(SelectionTarget.from(it))) }) }
    pendingViewKey?.let { key -> DriveOriginalDialog(cloud.find { it.key==key },features,onClose={ pendingViewKey=null;selectedId=null },onReady={
        pendingViewKey=null;selectedId=it;viewerId=it
    }) }
    viewerId?.let { id -> PhotoViewer(photos.filter { it.id==id || presence.showLocal(it,filter,albumId!=null || personId!=null || importedLabel!=null) },id,features,
        onClose={ viewerId=null;selectedId=null;viewerDetailsOpen=false },obscured=viewerDetailsOpen || editorId!=null || showShare,onCurrent={ selectedId=it },
        onEdit={ viewerId=null; selectedId=it; editorId=it },onDetails={ selectedId=it;viewerDetailsOpen=true },onTrash={ trash.request(listOf(SelectionTarget.from(it))) }) }
    if(selectedId!=null && selected==null && editorId==null && (viewerId==null || viewerDetailsOpen) && pendingViewKey==null) {
        AlertDialog(onDismissRequest={ selectedId=null;viewerDetailsOpen=false },title={ Text("Photo details") },
            text={ Text(selectedProblem ?: "Loading photo details…") },
            confirmButton={ if(selectedProblem!=null) TextButton(onClick={ selectedAttempt++ }) { Text("Retry") } },
            dismissButton={ TextButton(onClick={ selectedId=null;viewerDetailsOpen=false }) { Text("Close") } })
    }
    if(selected!=null && !showShare && editorId==null && (viewerId==null || viewerDetailsOpen) && pendingViewKey==null && (timelineViewer==null && searchViewer==null || viewerDetailsOpen)) PhotoDetails(selected,features,albums,memberships,people,faces,
        onClose={ viewerDetailsOpen=false;if(viewerId==null) selectedId=null },onEdit={ editorId=selected.id },onAI={ showShare=true },onExport={
            exportId=selected.id; export.launch(selected.displayName)
        },onResult=handoffs.firstOrNull { it.photoId==selected.id }?.let { h -> { pickResult(h) } },onView=if(viewerId==null && timelineViewer==null && searchViewer==null) ({ viewerId=selected.id }) else null,showPreview=viewerId==null && timelineViewer==null && searchViewer==null,
        onRemoveCopy={ features.requestRemoveDownload(selected) },onDeviceAccess={ timeline.closeViewer();searchTimeline.closeViewer();viewerId=null;viewerDetailsOpen=false;selectedId=null;navigation.open(ShellDestination.STORAGE) })
    if(selected!=null && showShare) ShareDialog(busy,onDismiss={ showShare=false }) { provider,instructions ->
        model.share(selected,provider,instructions) { intent -> context.startActivity(intent); showShare=false }
    }
    editorId?.let { id -> EditorScreen(id,onClose={ editorId=null },onSaved={ saved -> editorId=null; selectedId=saved }) }
    com.myphotosanywhere.mpa.trash.TrashDialogs(trash,onQueued={
        selection.clear();timeline.closeViewer();searchTimeline.closeViewer();viewerId=null;viewerDetailsOpen=false;selectedId=null
    })
    com.myphotosanywhere.mpa.device.DeviceBackupDialog(features) { timeline.closeViewer();searchTimeline.closeViewer();viewerId=null;viewerDetailsOpen=false;selectedId=null;navigation.open(ShellDestination.STORAGE) }
    DeviceCopyDialogs(features,onClosing={
        timeline.closeViewer();searchTimeline.closeViewer();viewerId=null;viewerDetailsOpen=false;selectedId=null
    })
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

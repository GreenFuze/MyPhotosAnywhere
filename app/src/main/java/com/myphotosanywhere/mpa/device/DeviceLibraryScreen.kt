package com.myphotosanywhere.mpa.device

import androidx.core.net.toUri
import android.app.Application
import android.database.ContentObserver
import android.net.Uri
import android.os.Handler
import android.os.Looper
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.repeatOnLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.R
import com.myphotosanywhere.mpa.management.GroupedMenu
import com.myphotosanywhere.mpa.management.MenuEntry
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.*

@OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class,kotlinx.coroutines.FlowPreview::class)
internal class DeviceLibraryModel(application: Application): AndroidViewModel(application) {
    private val app=application as MpaApplication
    val library=app.deviceMedia
    private val state=MutableStateFlow(library.permissions.current())
    val access=state.asStateFlow()
    private val failure=MutableStateFlow<String?>(null)
    val error=failure.asStateFlow()
    private val discovery=MutableStateFlow(false)
    val scanning=discovery.asStateFlow()
    private val refresh=MutableSharedFlow<Unit>(extraBufferCapacity=1)
    fun refresh() { state.value=library.permissions.current();refresh.tryEmit(Unit) }
    suspend fun observe() {
        merge(changes(),refresh).conflate().debounce(300).collect {
            state.value=library.permissions.current();discovery.value=true
            try {
                var completed=false
                repeat(3) { if(!completed) try {
                    val changed=library.reconcile();completed=true
                    if(changed>0 && (app.settings.state.value.faceIndexing || app.settings.state.value.objectIndexing)) app.people.schedule()
                } catch(changed: DeviceMediaLibrary.DeviceIndexChanged) { if(it==2) throw changed } }
                failure.value=null
            }
            catch(cancelled: CancellationException) { throw cancelled }
            catch(error: java.io.IOException) { failure.value=error.message ?: "Cannot refresh device photos. Retry." }
            catch(error: android.database.sqlite.SQLiteException) { failure.value=error.message ?: "Cannot index device photos. Retry." }
            finally { discovery.value=false }
        }
    }
    private fun changes()=callbackFlow {
        val observer=object: ContentObserver(Handler(Looper.getMainLooper())) {
            override fun onChange(selfChange: Boolean) { trySend(Unit) }
        }
        app.contentResolver.registerContentObserver("content://media".toUri(),true,observer);trySend(Unit)
        awaitClose { app.contentResolver.unregisterContentObserver(observer) }
    }
}

/** The observer belongs to the resumed application, across navigation and import browsing. */
@Composable
internal fun DeviceLibraryLifecycle(model: DeviceLibraryModel=viewModel()) {
    val owner=LocalLifecycleOwner.current
    LaunchedEffect(owner,model) { owner.lifecycle.repeatOnLifecycle(androidx.lifecycle.Lifecycle.State.RESUMED) { model.observe() } }
}

@Composable
internal fun DevicePhotoAccessMenu(model: DeviceLibraryModel=viewModel()) {
    val access by model.access.collectAsStateWithLifecycle()
    val error by model.error.collectAsStateWithLifecycle()
    val scanning by model.scanning.collectAsStateWithLifecycle()
    val request=rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { model.refresh() }
    val summary=when {
        access.full -> "Photos and videos are shown automatically. Backup starts only when you choose it."
        access.selected -> "Only your selected photos and videos are available. You can change your selection."
        access.images -> "Photos are available. Allow video access to include videos."
        access.videos -> "Videos are available. Allow photo access to include photos."
        else -> "Allow Android photo access to show camera photos and other device media automatically."
    }
    GroupedMenu(listOf(MenuEntry("Photos on this device",summary,R.drawable.ic_shell_photos,"device:access") { request.launch(model.library.permissions.request()) }))
    if(scanning) Text("Refreshing device photos…",style=MaterialTheme.typography.bodySmall,modifier=Modifier.testTag("device:scanning"))
    error?.let {
        Text(it,color=MaterialTheme.colorScheme.error,modifier=Modifier.testTag("device:error"))
        TextButton(onClick=model::refresh,modifier=Modifier.heightIn(min=48.dp)) { Text("Retry device photos") }
    }
}

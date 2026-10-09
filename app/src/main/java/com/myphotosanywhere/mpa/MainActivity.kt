package com.myphotosanywhere.mpa

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import coil.compose.AsyncImage
import com.myphotosanywhere.mpa.core.AiProvider
import com.myphotosanywhere.mpa.data.Handoff
import com.myphotosanywhere.mpa.data.Photo
import java.io.File
import java.text.DateFormat
import java.util.Date

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            MaterialTheme(colorScheme = lightColorScheme(
                primary = Color(0xFF166D5B), secondary = Color(0xFF49675F),
                background = Color(0xFFF8FAF7), surface = Color(0xFFF8FAF7),
            )) { LibraryScreen() }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun LibraryScreen(model: LibraryViewModel = viewModel()) {
    val photos by model.photos.collectAsStateWithLifecycle()
    val handoffs by model.handoffs.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val message by model.message.collectAsStateWithLifecycle()
    var tab by rememberSaveable { mutableStateOf(0) }
    var selectedId by rememberSaveable { mutableStateOf<String?>(null) }
    var resultHandoffId by rememberSaveable { mutableStateOf<String?>(null) }
    var showShare by rememberSaveable { mutableStateOf(false) }
    var query by rememberSaveable { mutableStateOf("") }
    val snackbar = remember { SnackbarHostState() }
    val context = LocalContext.current
    val addPhotos = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(100)) {
        model.importPhotos(it)
    }
    val importResult = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) {
            resultHandoffId?.let { model.importResult(uri, it) }
        }
        resultHandoffId = null
    }
    fun pickResult(handoff: Handoff) {
        resultHandoffId = handoff.id
        importResult.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
    }
    val selected = photos.find { it.id == selectedId }
    LaunchedEffect(message) {
        message?.let { snackbar.showSnackbar(it); model.clearMessage() }
    }
    Scaffold(
        topBar = { TopAppBar(title = {
            Column {
                Text("MyPhotosAnywhere", fontWeight = FontWeight.SemiBold)
                Text("Your photos. Your storage.", style = MaterialTheme.typography.labelMedium)
            }
        }) },
        snackbarHost = { SnackbarHost(snackbar) },
        bottomBar = {
            NavigationBar {
                listOf("Photos", "AI activity", "Storage").forEachIndexed { index, title ->
                    NavigationBarItem(selected = tab == index, onClick = { tab = index },
                        icon = { Text(listOf("▦", "✦", "▤")[index]) }, label = { Text(title) })
                }
            }
        },
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {
            if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
            when (tab) {
                0 -> {
                    Row(Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 12.dp),
                        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween) {
                        Column {
                            Text("Your library", style = MaterialTheme.typography.headlineSmall)
                            Text("${photos.size} photos · saved locally", style = MaterialTheme.typography.bodySmall)
                        }
                        Button(enabled = !busy, onClick = {
                            addPhotos.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
                        }) { Text("Add photos") }
                    }
                    if (photos.isEmpty()) {
                        Box(Modifier.fillMaxSize().padding(32.dp), contentAlignment = Alignment.Center) {
                            Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp)) {
                                Text("A home for your memories", style = MaterialTheme.typography.headlineSmall)
                                Text("Add a few photos to start. They stay available offline, and you can send selected photos to your AI app for editing.")
                                Text("Drive backup is coming in the next milestone. Keep your source copies.", style = MaterialTheme.typography.bodySmall)
                            }
                        }
                    } else {
                        OutlinedTextField(value = query, onValueChange = { query = it },
                            placeholder = { Text("Search filenames") }, singleLine = true,
                            modifier = Modifier.fillMaxWidth().padding(horizontal = 20.dp).padding(bottom = 12.dp))
                        LazyVerticalGrid(columns = GridCells.Adaptive(108.dp), contentPadding = PaddingValues(4.dp),
                            horizontalArrangement = Arrangement.spacedBy(4.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            items(photos.filter { it.displayName.contains(query, ignoreCase = true) }, key = { it.id }) { photo ->
                                Box(Modifier.aspectRatio(1f).clip(RoundedCornerShape(10.dp)).clickable { selectedId = photo.id }) {
                                    AsyncImage(model = File(photo.thumbnailPath), contentDescription = photo.displayName,
                                        contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
                                    if (photo.parentId != null) {
                                        Card(Modifier.align(Alignment.BottomEnd).padding(4.dp)) {
                                            Text("AI edit", style = MaterialTheme.typography.labelSmall, modifier = Modifier.padding(4.dp))
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                1 -> {
                    Text("AI activity", style = MaterialTheme.typography.headlineSmall, modifier = Modifier.padding(20.dp))
                    if (handoffs.isEmpty()) Text("Open a photo and choose Edit with AI. Pending handoffs appear here so you can import the result later.",
                        modifier = Modifier.padding(horizontal = 20.dp))
                    LazyColumn(contentPadding = PaddingValues(horizontal = 20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        items(handoffs, key = { it.id }) { handoff ->
                            val photo = photos.find { it.id == handoff.photoId }
                            Card(Modifier.fillMaxWidth()) {
                                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                                    Text(AiProvider.entries.find { it.name == handoff.provider }?.title ?: handoff.provider,
                                        style = MaterialTheme.typography.titleMedium)
                                    Text(photo?.displayName ?: "Original photo", style = MaterialTheme.typography.bodySmall)
                                    Text(handoff.instructions, maxLines = 3)
                                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                        Button(enabled = !busy && photo != null, onClick = { pickResult(handoff) }) { Text("Import AI result") }
                                        TextButton(enabled = !busy, onClick = { model.dismiss(handoff) }) { Text("Dismiss") }
                                    }
                                }
                            }
                        }
                    }
                }
                2 -> StorageScreen(photos)
            }
        }
    }
    if (selected != null && !showShare) {
        val latest = handoffs.firstOrNull { it.photoId == selected.id }
        AlertDialog(onDismissRequest = { selectedId = null }, title = { Text(selected.displayName) },
            text = {
                Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    AsyncImage(model = File(selected.originalPath), contentDescription = selected.displayName,
                        contentScale = ContentScale.Fit, modifier = Modifier.fillMaxWidth().height(260.dp))
                    Text("Imported ${DateFormat.getDateInstance().format(Date(selected.importedAt))} · ${formatBytes(selected.bytes)}")
                    Text(if (selected.parentId == null) "Original preserved locally" else "Edited copy · original preserved")
                    if (selected.latitude != null && selected.longitude != null) {
                        Text("GPS: ${selected.latitude}, ${selected.longitude}")
                        TextButton(onClick = {
                            try {
                                context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("geo:${selected.latitude},${selected.longitude}?q=${selected.latitude},${selected.longitude}")))
                            } catch (_: android.content.ActivityNotFoundException) { /* The system has no map app. */ }
                        }) { Text("Open location in Maps") }
                    }
                    if (latest != null) OutlinedButton(enabled = !busy, onClick = { pickResult(latest) }) { Text("Import AI result") }
                }
            },
            confirmButton = { Button(enabled = !busy, onClick = { showShare = true }) { Text("Edit with AI") } },
            dismissButton = { TextButton(onClick = { selectedId = null }) { Text("Close") } })
    }
    if (selected != null && showShare) {
        ShareDialog(busy, onDismiss = { showShare = false }) { provider, instructions ->
            model.share(selected, provider, instructions) { intent -> context.startActivity(intent); showShare = false }
        }
    }
}

@Composable
private fun ShareDialog(busy: Boolean, onDismiss: () -> Unit, onShare: (AiProvider, String) -> Unit) {
    var providerName by rememberSaveable { mutableStateOf(AiProvider.CHATGPT.name) }
    var instructions by rememberSaveable { mutableStateOf("") }
    val provider = AiProvider.valueOf(providerName)
    AlertDialog(onDismissRequest = { if (!busy) onDismiss() }, title = { Text("Edit with your AI app") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                AiProvider.entries.forEach { option ->
                    Row(Modifier.fillMaxWidth().clickable(enabled = !busy) { providerName = option.name },
                        verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(selected = provider == option, onClick = { providerName = option.name }, enabled = !busy)
                        Text(option.title)
                    }
                }
                Text(provider.guidance, style = MaterialTheme.typography.bodySmall)
                OutlinedTextField(value = instructions, onValueChange = { if (it.length <= 4000) instructions = it },
                    enabled = !busy, label = { Text("Describe your edit") }, minLines = 3, modifier = Modifier.fillMaxWidth())
                Text("This shares a preview up to 2048 pixels with the app you choose and copies instructions to your clipboard. GPS metadata is removed. Save the AI output and import it here afterward.",
                    style = MaterialTheme.typography.bodySmall)
            }
        }, confirmButton = {
            Button(enabled = !busy && instructions.isNotBlank(), onClick = { onShare(provider, instructions) }) {
                if (busy) CircularProgressIndicator(Modifier.size(20.dp)) else Text("Continue to share sheet")
            }
        }, dismissButton = { TextButton(enabled = !busy, onClick = onDismiss) { Text("Cancel") } })
}

@Composable
private fun StorageScreen(photos: List<Photo>) {
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)) {
        Text("Storage", style = MaterialTheme.typography.headlineSmall)
        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(formatBytes(photos.sumOf { it.bytes }), style = MaterialTheme.typography.headlineMedium)
                Text("${photos.size} original files and edited copies on this device")
                Text("Pending Drive backup", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.SemiBold)
            }
        }
        Text("Local-first browsing", style = MaterialTheme.typography.titleMedium)
        Text("Small thumbnails keep your gallery responsive. Imported photos and pending edits are protected files, separate from temporary AI exports.")
        Text("Google Drive · next milestone", style = MaterialTheme.typography.titleMedium)
        Text("Choose your Drive folder, back up in the background, and set a budget for downloaded originals. MPA will only evict files with a verified remote copy.")
        Text("Keep your source photos. Uninstalling the app or clearing its data removes this local library.", style = MaterialTheme.typography.bodySmall)
        Spacer(Modifier.height(8.dp))
        Text("MyPhotosAnywhere · MPA 0.1", style = MaterialTheme.typography.labelMedium)
    }
}

private fun formatBytes(bytes: Long): String = when {
    bytes >= 1024L * 1024 * 1024 -> "%.1f GiB".format(bytes / (1024.0 * 1024 * 1024))
    bytes >= 1024L * 1024 -> "%.1f MiB".format(bytes / (1024.0 * 1024))
    else -> "%.0f KiB".format(bytes / 1024.0)
}

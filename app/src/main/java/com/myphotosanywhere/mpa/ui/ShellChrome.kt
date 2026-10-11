package com.myphotosanywhere.mpa.ui

import androidx.annotation.DrawableRes
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import androidx.compose.ui.text.style.TextOverflow
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.myphotosanywhere.mpa.importing.ImportHistory
import com.myphotosanywhere.mpa.importing.ImportModel
import com.myphotosanywhere.mpa.R

@Composable
fun ShellIcon(@DrawableRes resource: Int, description: String?=null) {
    Icon(painterResource(resource),description,Modifier.size(24.dp))
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ShellTopBar(title: String,canGoBack: Boolean,onBack: ()->Unit,canAddPhotos: Boolean,onAddPhotos: ()->Unit,
    onStorage: ()->Unit,onActivity: ()->Unit,onFilters: ()->Unit,showFilters: Boolean,
    filtersActive: Boolean,needsAttention: Boolean) {
    var add by remember { mutableStateOf(false) }
    TopAppBar(title={ Text(title,maxLines=1,overflow=TextOverflow.Ellipsis) },navigationIcon={
        if(canGoBack) IconButton(onClick=onBack) { ShellIcon(R.drawable.ic_shell_back,"Back") }
        else Image(painterResource(R.drawable.ic_launcher_foreground),"MyPhotosAnywhere",Modifier.size(48.dp))
    },actions={
        if(showFilters) IconButton(onClick=onFilters,modifier=Modifier.testTag("shell:filters")) {
            BadgedBox(badge={ if(filtersActive) Badge() }) {
                ShellIcon(R.drawable.ic_shell_filter,if(filtersActive) "Filters applied" else "Filter photos")
            }
        }
        Box {
            IconButton(onClick={ add=true },modifier=Modifier.testTag("shell:add")) { ShellIcon(R.drawable.ic_shell_add,"Add photos or import") }
            DropdownMenu(expanded=add,onDismissRequest={ add=false }) {
                DropdownMenuItem(text={ Text("Add photos") },enabled=canAddPhotos,onClick={ add=false;onAddPhotos() })
                DropdownMenuItem(text={ Text("Import photos") },onClick={ add=false;onStorage() })
                DropdownMenuItem(text={ Text("Connect Google Drive") },onClick={ add=false;onStorage() })
            }
        }
        IconButton(onClick=onActivity,modifier=Modifier.testTag("shell:activity")) {
            BadgedBox(badge={ if(needsAttention) Badge { Text("!") } }) {
                ShellIcon(R.drawable.ic_shell_activity,"Imports and sync activity")
            }
        }
        IconButton(onClick=onStorage,modifier=Modifier.testTag("shell:storage")) {
            ShellIcon(R.drawable.ic_shell_account,"Accounts, sources and storage")
        }
    })
}

@Composable
fun ShellBottomBar(selected: ShellDestination,onSelect: (ShellDestination)->Unit) {
    Row(Modifier.fillMaxWidth().imePadding().navigationBarsPadding().padding(horizontal=16.dp,vertical=8.dp),
        horizontalArrangement=Arrangement.Center) {
        Row(Modifier.widthIn(max=480.dp).fillMaxWidth(),verticalAlignment=Alignment.CenterVertically,
            horizontalArrangement=Arrangement.spacedBy(12.dp)) {
            Surface(Modifier.weight(1f),shape=RoundedCornerShape(32.dp),tonalElevation=3.dp) {
                Row {
                    ShellTab(ShellDestination.PHOTOS,R.drawable.ic_shell_photos,selected,onSelect,Modifier.weight(1f))
                    ShellTab(ShellDestination.COLLECTIONS,R.drawable.ic_shell_collections,selected,onSelect,Modifier.weight(1f))
                }
            }
            Surface(shape=RoundedCornerShape(32.dp),tonalElevation=3.dp) {
                ShellTab(ShellDestination.SEARCH,R.drawable.ic_shell_search,selected,onSelect,Modifier.widthIn(min=72.dp))
            }
        }
    }
}

@Composable
private fun ShellTab(destination: ShellDestination,@DrawableRes icon: Int,selected: ShellDestination,
    onSelect: (ShellDestination)->Unit,modifier: Modifier) {
    val active=destination==selected
    Surface(color=if(active) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surface,
        shape=RoundedCornerShape(32.dp),modifier=modifier.padding(4.dp)) {
        Column(Modifier.selectable(active,role=Role.Tab,onClick={ onSelect(destination) })
            .testTag("nav:${destination.title}").heightIn(min=56.dp).padding(horizontal=8.dp,vertical=6.dp),
            horizontalAlignment=Alignment.CenterHorizontally,verticalArrangement=Arrangement.Center) {
            ShellIcon(icon)
            Text(destination.title,style=MaterialTheme.typography.labelMedium)
        }
    }
}

/** Observe job summaries, not per-item transfer ticks; keep the gallery independent of progress updates. */
@Composable
fun ShellImportActivity(onOpen: ()->Unit,model: ImportModel=viewModel()) {
    val jobs by model.jobs.collectAsStateWithLifecycle()
    val dismissed by model.dismissed.collectAsStateWithLifecycle()
    val visible=jobs.filter { it.id !in dismissed || !ImportHistory.canDismiss(it.state) }
    val running=visible.count { it.state in listOf("RUNNING","DOWNLOADING") }
    val label=when {
        running>0 -> if(running==1) "1 import running" else "$running imports running"
        visible.any { it.state=="WAITING_FOR_APP" } -> "Import waiting to resume"
        visible.any { it.state=="FAILED" || it.error!=null } -> "Import needs attention"
        visible.any { it.state=="PAUSED" } -> "Import paused"
        visible.any { !ImportHistory.canDismiss(it.state) } -> "Import ready to review"
        else -> null
    }
    if(label!=null) TextButton(onClick=onOpen,modifier=Modifier.fillMaxWidth().testTag("shell:import-activity")) {
        ShellIcon(R.drawable.ic_shell_activity)
        Spacer(Modifier.width(8.dp))
        Text(label,maxLines=1,overflow=TextOverflow.Ellipsis)
    }
}

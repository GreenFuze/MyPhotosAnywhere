package com.myphotosanywhere.mpa.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.annotation.DrawableRes
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
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
    onStorage: ()->Unit,onActivity: ()->Unit,onImport: ()->Unit,onFilters: ()->Unit,showFilters: Boolean,
    filtersActive: Boolean,needsAttention: Boolean,showTitle: Boolean=true,scrollBehavior: TopAppBarScrollBehavior?=null,showActions: Boolean=true) {
    var add by remember { mutableStateOf(false) }
    BoxWithConstraints {
    val compact=maxWidth<360.dp
    TopAppBar(modifier=Modifier.testTag("shell:top-bar"),scrollBehavior=scrollBehavior,title={ if(showTitle) Text(title,maxLines=1,overflow=TextOverflow.Ellipsis) },navigationIcon={
        if(canGoBack) IconButton(onClick=onBack) { ShellIcon(R.drawable.ic_shell_back,"Back") }
        else Image(painterResource(R.drawable.ic_launcher_foreground),"MyPhotosAnywhere",Modifier.size(48.dp))
    },actions={ if(showActions) {
        if(showFilters) IconButton(onClick=onFilters,modifier=Modifier.testTag("shell:filters")) {
            BadgedBox(badge={ if(filtersActive) Badge() }) {
                ShellIcon(R.drawable.ic_shell_filter,if(filtersActive) "Filters applied" else "Filter photos")
            }
        }
        Box {
            IconButton(onClick={ add=true },modifier=Modifier.testTag("shell:add")) { ShellIcon(R.drawable.ic_shell_add,"Add photos or import") }
            DropdownMenu(expanded=add,onDismissRequest={ add=false }) {
                DropdownMenuItem(text={ Text("Add photos") },leadingIcon={ ShellIcon(R.drawable.ic_shell_photos) },enabled=canAddPhotos,onClick={ add=false;onAddPhotos() })
                DropdownMenuItem(text={ Text("Import photos") },leadingIcon={ ShellIcon(R.drawable.ic_shell_add) },onClick={ add=false;onImport() })
                if(compact) DropdownMenuItem(text={ Text("Imports and activity") },leadingIcon={ ShellIcon(R.drawable.ic_shell_activity) },onClick={ add=false;onActivity() })
                DropdownMenuItem(text={ Text("Connect Google Drive") },leadingIcon={ ShellIcon(R.drawable.ic_shell_account) },onClick={ add=false;onStorage() })
            }
        }
        if(!compact) IconButton(onClick=onActivity,modifier=Modifier.testTag("shell:activity")) {
            BadgedBox(badge={ if(needsAttention) Badge { Text("!") } }) {
                ShellIcon(R.drawable.ic_shell_activity,"Imports and sync activity")
            }
        }
        IconButton(onClick=onStorage,modifier=Modifier.testTag("shell:storage")) {
            ShellIcon(R.drawable.ic_shell_account,"Accounts and sources")
        }
    } })
    }
}

/** The parent consumes system/IME insets; measured height also supplies scroll-end clearance. */
@Composable
fun ShellBottomBar(selected: ShellDestination,onSelect: (ShellDestination)->Unit,modifier: Modifier=Modifier) {
    BoxWithConstraints(modifier.fillMaxWidth().padding(horizontal=16.dp,vertical=8.dp),
        contentAlignment=Alignment.Center) {
        // Full-width rows keep labels readable when enlarged text cannot fit the compact pill.
        val expanded=maxWidth<280.dp || LocalDensity.current.fontScale>1.2f
        Row(Modifier.widthIn(max=480.dp).fillMaxWidth(),verticalAlignment=Alignment.CenterVertically,
            horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            Surface(Modifier.weight(1f),shape=RoundedCornerShape(32.dp),
                color=MaterialTheme.colorScheme.surfaceContainer,shadowElevation=2.dp) {
                if(expanded) Column(Modifier.padding(4.dp)) {
                    ShellTab(ShellDestination.PHOTOS,R.drawable.ic_shell_photos,selected,onSelect,Modifier.fillMaxWidth())
                    ShellTab(ShellDestination.COLLECTIONS,R.drawable.ic_shell_collections,selected,onSelect,Modifier.fillMaxWidth())
                } else Row(Modifier.padding(4.dp),horizontalArrangement=Arrangement.SpaceEvenly) {
                    ShellTab(ShellDestination.PHOTOS,R.drawable.ic_shell_photos,selected,onSelect,Modifier.widthIn(min=48.dp))
                    ShellTab(ShellDestination.COLLECTIONS,R.drawable.ic_shell_collections,selected,onSelect,Modifier.widthIn(min=48.dp))
                }
            }
            val searchActive=selected==ShellDestination.SEARCH
            Surface(shape=CircleShape,shadowElevation=2.dp,
                color=if(searchActive) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surfaceContainer,
                contentColor=if(searchActive) MaterialTheme.colorScheme.onSecondaryContainer else MaterialTheme.colorScheme.onSurface) {
                Box(Modifier.size(56.dp).selectable(searchActive,role=Role.Tab,onClick={ onSelect(ShellDestination.SEARCH) })
                    .testTag("nav:Search").semantics { contentDescription="Search" },contentAlignment=Alignment.Center) {
                    ShellIcon(R.drawable.ic_shell_search)
                }
            }
        }
    }
}

@Composable
private fun ShellTab(destination: ShellDestination,@DrawableRes icon: Int,selected: ShellDestination,
    onSelect: (ShellDestination)->Unit,modifier: Modifier) {
    val active=destination==selected
    val color by animateColorAsState(if(active) MaterialTheme.colorScheme.secondaryContainer else Color.Transparent,tween(150),label="Navigation selection")
    Surface(color=color,
        contentColor=if(active) MaterialTheme.colorScheme.onSecondaryContainer else MaterialTheme.colorScheme.onSurface,
        shape=RoundedCornerShape(28.dp),modifier=modifier) {
        Row(Modifier.selectable(active,role=Role.Tab,onClick={ onSelect(destination) })
            .testTag("nav:${destination.title}").heightIn(min=48.dp).padding(horizontal=8.dp,vertical=8.dp),
            verticalAlignment=Alignment.CenterVertically,horizontalArrangement=Arrangement.Center) {
            if(active) { ShellIcon(icon); Spacer(Modifier.width(6.dp)) }
            Text(destination.title,style=MaterialTheme.typography.labelLarge)
        }
    }
}

/** Observe job summaries, not per-item transfer ticks; keep the gallery independent of progress updates. */
@Composable
fun ShellImportActivity(onOpen: ()->Unit,sync: List<com.myphotosanywhere.mpa.drive.SyncState> = emptyList(),model: ImportModel=viewModel()) {
    val jobs by model.jobs.collectAsStateWithLifecycle()
    val dismissed by model.dismissed.collectAsStateWithLifecycle()
    val visible=jobs.filter { it.id !in dismissed || !ImportHistory.canDismiss(it.state) }
    val label=com.myphotosanywhere.mpa.management.ActivitySummary(visible,sync).label
    var lastLabel by remember { mutableStateOf("") }
    SideEffect { if(label!=null) lastLabel=label }
    MotionVisibility(label!=null,MotionEdge.TOP) {
    TextButton(onClick=onOpen,enabled=label!=null,modifier=Modifier.fillMaxWidth().testTag("shell:import-activity")) {
        ShellIcon(R.drawable.ic_shell_activity)
        Spacer(Modifier.width(8.dp))
        Text(label ?: lastLabel,maxLines=1,overflow=TextOverflow.Ellipsis)
    }
    }
}

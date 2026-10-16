package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.myphotosanywhere.mpa.R
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.ui.ShellIcon
import com.myphotosanywhere.mpa.ui.IconLabelAction
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.text.DateFormat
import java.util.Date

@Composable
internal fun ViewerTopBar(millis: Long?,name: String,onClose: ()->Unit,onDetails: ()->Unit,onHide: (()->Unit)?=null) {
    Row(Modifier.fillMaxWidth().heightIn(min=56.dp).padding(horizontal=4.dp),verticalAlignment=Alignment.CenterVertically) {
        IconButton(onClick=onClose,modifier=Modifier.size(48.dp).testTag("viewer-close")) { ShellIcon(R.drawable.ic_shell_back,"Close") }
        Column(Modifier.weight(1f).padding(horizontal=8.dp)) {
            Text(millis?.let { DateFormat.getDateInstance(DateFormat.MEDIUM).format(Date(it)) } ?: "Unknown date",
                color=Color.White,style=MaterialTheme.typography.titleMedium,modifier=Modifier.semantics { heading() })
            Text(name,color=Color.White.copy(alpha=.8f),style=MaterialTheme.typography.bodySmall,maxLines=1,overflow=TextOverflow.Ellipsis)
        }
        IconButton(onClick=onDetails,modifier=Modifier.size(48.dp).testTag("viewer-details")) { ShellIcon(R.drawable.ic_action_info,"Photo details") }
        if(onHide!=null) IconButton(onClick=onHide,modifier=Modifier.size(48.dp).testTag("viewer-hide-controls")) {
            ShellIcon(R.drawable.ic_viewer_hide,stringResource(R.string.viewer_hide_controls))
        }
    }
}

@Composable
internal fun ViewerActions(photo: Photo?,model: LibraryFeaturesModel,onEdit: (String)->Unit,showStatus: Boolean=true,onTrash: (()->Unit)?=null) {
    val context=LocalContext.current
    val busy by model.busy.collectAsStateWithLifecycle()
    val live by produceState(photo,photo?.id) {
        photo?.let { item -> model.app.timeline.photo(item.id).collect { value=it } }
    }
    val item=live?.takeIf { it.id==photo?.id }
    val device by produceState<com.myphotosanywhere.mpa.data.DeviceMediaRecord?>(null,item?.id) {
        item?.let { model.app.deviceMedia.observe(it.id).collect { value=it } }
    }
    val available by produceState(false,item?.id,item?.originalPath,item?.cachedVersion,device?.available,busy) {
        value=withContext(Dispatchers.IO) { item!=null && (item.originalPath.isNotBlank() && File(item.originalPath).isFile || model.app.deviceMedia.readable(item.id)) }
    }
    val inDrive by produceState(false,item?.id) {
        item?.let { model.app.database.presence().inDrive(it.id).collect { value=it } }
    }
    FlowRow(Modifier.fillMaxWidth().padding(horizontal=12.dp),horizontalArrangement=Arrangement.SpaceEvenly,
        verticalArrangement=Arrangement.spacedBy(4.dp)) {
        IconLabelAction(R.drawable.ic_action_share,"Share",{ item?.let { model.share(it) { intent -> context.startActivity(intent) } } },
            Modifier.testTag("viewer-share"),enabled=available && !busy,color=Color.White)
        if(photo?.mimeType?.startsWith("image/")!=false) IconLabelAction(R.drawable.ic_shell_edit,"Edit",{ item?.let { onEdit(it.id) } },
            Modifier.testTag("viewer-edit"),enabled=available && !busy,color=Color.White)
        if(item!=null) IconLabelAction(if(item.favorite) R.drawable.ic_shell_favorite else R.drawable.ic_action_favorite_outline,"Favorite",{ model.updatePhoto(item.id) { it.copy(favorite=!it.favorite) } },
            Modifier.testTag("viewer-favorite"),enabled=!busy,description=if(item.favorite) "Remove from favorites" else "Add to favorites",selected=item.favorite,
            color=if(item.favorite) Color(0xFF8BD5BE) else Color.White)
        if(item!=null && available && !inDrive) IconLabelAction(R.drawable.ic_action_backup,"Back up",{ model.requestDeviceBackup(item) },
            Modifier.testTag("viewer-backup"),enabled=!busy,color=Color.White)
        if(onTrash!=null) IconLabelAction(R.drawable.ic_action_trash,"Trash",onTrash,Modifier.testTag("viewer-trash"),enabled=!busy,color=Color.White,description="Move to Google Drive Trash")
        if(showStatus) ViewerStatus(model,Color.White)
    }
}

/** Operational feedback belongs to the foreground media surface, above its dialog window. */
@Composable
internal fun ViewerStatus(model: LibraryFeaturesModel,color: Color=LocalContentColor.current) {
    val message by model.message.collectAsStateWithLifecycle()
    message?.let {
        Text(it,color=color,style=MaterialTheme.typography.bodySmall,
            modifier=Modifier.fillMaxWidth().padding(horizontal=16.dp,vertical=8.dp).testTag("viewer:message")
                .semantics { liveRegion=LiveRegionMode.Polite })
    }
}

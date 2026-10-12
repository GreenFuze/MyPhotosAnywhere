package com.myphotosanywhere.mpa.library

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
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
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.text.DateFormat
import java.util.Date

@Composable
internal fun ViewerTopBar(millis: Long?,name: String,onClose: ()->Unit,onDetails: ()->Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min=56.dp).padding(horizontal=4.dp),verticalAlignment=Alignment.CenterVertically) {
        IconButton(onClick=onClose,modifier=Modifier.size(48.dp).testTag("viewer-close")) { ShellIcon(R.drawable.ic_shell_back,"Close") }
        Column(Modifier.weight(1f).padding(horizontal=8.dp)) {
            Text(millis?.let { DateFormat.getDateInstance(DateFormat.MEDIUM).format(Date(it)) } ?: "Unknown date",
                color=Color.White,style=MaterialTheme.typography.titleMedium,modifier=Modifier.semantics { heading() })
            Text(name,color=Color.White.copy(alpha=.8f),style=MaterialTheme.typography.bodySmall,maxLines=1,overflow=TextOverflow.Ellipsis)
        }
        TextButton(onClick=onDetails,modifier=Modifier.heightIn(min=48.dp).testTag("viewer-details")) { Text("Details",color=Color.White) }
    }
}

@Composable
internal fun ViewerActions(photo: Photo?,model: LibraryFeaturesModel,onEdit: (String)->Unit,showStatus: Boolean=true) {
    val context=LocalContext.current
    val busy by model.busy.collectAsStateWithLifecycle()
    val live by produceState(photo,photo?.id) {
        photo?.let { item -> model.app.timeline.photo(item.id).collect { value=it } }
    }
    val item=live?.takeIf { it.id==photo?.id }
    val available by produceState(false,item?.id,item?.originalPath,item?.cachedVersion,busy) {
        value=withContext(Dispatchers.IO) { item!=null && item.originalPath.isNotBlank() && File(item.originalPath).isFile }
    }
    FlowRow(Modifier.fillMaxWidth().padding(horizontal=12.dp),horizontalArrangement=Arrangement.SpaceEvenly,
        verticalArrangement=Arrangement.spacedBy(4.dp)) {
        TextButton(enabled=available && !busy,onClick={ item?.let { model.share(it) { intent -> context.startActivity(intent) } } },
            modifier=Modifier.heightIn(min=48.dp).testTag("viewer-share")) { Text("Share",color=if(available && !busy) Color.White else Color.Gray) }
        if(photo?.mimeType?.startsWith("image/")!=false) TextButton(enabled=available && !busy,onClick={ item?.let { onEdit(it.id) } },
            modifier=Modifier.heightIn(min=48.dp).testTag("viewer-edit")) { Text("Edit",color=if(available && !busy) Color.White else Color.Gray) }
        if(item!=null) FilterChip(selected=item.favorite,enabled=!busy,onClick={ model.updatePhoto(item.id) { it.copy(favorite=!it.favorite) } },
            label={ Text("Favorite") },modifier=Modifier.heightIn(min=48.dp).testTag("viewer-favorite"),
            colors=FilterChipDefaults.filterChipColors(labelColor=Color.White,selectedContainerColor=Color(0xFF8BD5BE),selectedLabelColor=Color.Black))
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

package com.myphotosanywhere.mpa.ui

import androidx.annotation.DrawableRes
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.*
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp

/** Short visual labels and one complete accessible action name; never truncates enlarged text. */
@Composable
internal fun IconLabelAction(@DrawableRes icon: Int,label: String,onClick: ()->Unit,modifier: Modifier=Modifier,
    enabled: Boolean=true,description: String=label,selected: Boolean?=null,color: Color=MaterialTheme.colorScheme.primary) {
    TextButton(onClick=onClick,enabled=enabled,modifier=modifier.widthIn(min=72.dp,max=112.dp).heightIn(min=64.dp)
        .semantics { contentDescription=description;selected?.let { this.selected=it } },
        colors=ButtonDefaults.textButtonColors(contentColor=color,disabledContentColor=color.copy(alpha=.38f))) {
        Column(horizontalAlignment=Alignment.CenterHorizontally,verticalArrangement=Arrangement.spacedBy(4.dp)) {
            ShellIcon(icon)
            Text(label,style=MaterialTheme.typography.labelMedium,textAlign=TextAlign.Center,modifier=Modifier.clearAndSetSemantics {})
        }
    }
}

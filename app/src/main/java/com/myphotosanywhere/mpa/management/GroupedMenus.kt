package com.myphotosanywhere.mpa.management

import androidx.annotation.DrawableRes
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.*
import androidx.compose.ui.unit.dp
import com.myphotosanywhere.mpa.R
import com.myphotosanywhere.mpa.ui.ShellIcon

/** The same menu vocabulary is shared by account, settings and preference destinations. */
internal data class MenuEntry(val title: String,val summary: String,@DrawableRes val icon: Int,
    val tag: String,val enabled: Boolean=true,val open: ()->Unit)

@Composable
internal fun GroupedMenu(entries: List<MenuEntry>,modifier: Modifier=Modifier) {
    Surface(modifier.fillMaxWidth(),shape=RoundedCornerShape(28.dp),color=MaterialTheme.colorScheme.surface) {
        Column {
            entries.forEachIndexed { index,entry ->
                Surface(onClick=entry.open,enabled=entry.enabled,color=MaterialTheme.colorScheme.surface,
                    modifier=Modifier.fillMaxWidth().heightIn(min=76.dp).testTag(entry.tag).semantics { role=Role.Button }) {
                    Row(Modifier.padding(horizontal=20.dp,vertical=20.dp),verticalAlignment=Alignment.CenterVertically,
                        horizontalArrangement=Arrangement.spacedBy(16.dp)) {
                        ShellIcon(entry.icon)
                        Column(Modifier.weight(1f),verticalArrangement=Arrangement.spacedBy(4.dp)) {
                            Text(entry.title,style=MaterialTheme.typography.titleMedium)
                            if(entry.summary.isNotBlank()) Text(entry.summary,style=MaterialTheme.typography.bodyMedium,color=MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        ShellIcon(R.drawable.ic_action_chevron)
                    }
                }
                if(index<entries.lastIndex) HorizontalDivider(Modifier.padding(start=60.dp,end=20.dp),color=MaterialTheme.colorScheme.surfaceContainerHigh)
            }
        }
    }
}

@Composable
internal fun PreferenceCard(title: String,summary: String,tag: String,onOpen: ()->Unit) {
    Surface(onClick=onOpen,modifier=Modifier.fillMaxWidth().heightIn(min=88.dp).testTag(tag).semantics { role=Role.Button },
        shape=RoundedCornerShape(28.dp),color=MaterialTheme.colorScheme.surface) {
        Column(Modifier.padding(24.dp),verticalArrangement=Arrangement.spacedBy(6.dp)) {
            Text(title,style=MaterialTheme.typography.titleLarge)
            if(summary.isNotBlank()) Text(summary,style=MaterialTheme.typography.bodyLarge,color=MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
internal fun PreferenceToggle(title: String,summary: String,checked: Boolean,onChange: (Boolean)->Unit,modifier: Modifier=Modifier) {
    Row(modifier.fillMaxWidth().heightIn(min=64.dp).semantics(mergeDescendants=true) { }
        .toggleable(checked,role=Role.Switch,onValueChange=onChange).padding(horizontal=4.dp,vertical=16.dp),
        verticalAlignment=Alignment.CenterVertically,horizontalArrangement=Arrangement.spacedBy(20.dp)) {
        Column(Modifier.weight(1f),verticalArrangement=Arrangement.spacedBy(6.dp)) {
            Text(title,style=MaterialTheme.typography.titleMedium)
            if(summary.isNotBlank()) Text(summary,style=MaterialTheme.typography.bodyLarge,color=MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Switch(checked=checked,onCheckedChange=null,modifier=Modifier.clearAndSetSemantics { })
    }
}

@Composable
internal fun PreferenceChoice(title: String,summary: String,selected: Boolean,tag: String,enabled: Boolean=true,onChoose: ()->Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min=64.dp).testTag(tag).semantics(mergeDescendants=true) { }
        .selectable(selected,enabled=enabled,role=Role.RadioButton,onClick=onChoose).padding(horizontal=4.dp,vertical=16.dp),
        verticalAlignment=Alignment.CenterVertically,horizontalArrangement=Arrangement.spacedBy(20.dp)) {
        Column(Modifier.weight(1f),verticalArrangement=Arrangement.spacedBy(4.dp)) {
            Text(title,style=MaterialTheme.typography.titleMedium)
            if(summary.isNotBlank()) Text(summary,style=MaterialTheme.typography.bodyMedium,color=MaterialTheme.colorScheme.onSurfaceVariant)
        }
        RadioButton(selected=selected,onClick=null,enabled=enabled,modifier=Modifier.clearAndSetSemantics { })
    }
}

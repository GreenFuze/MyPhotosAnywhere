package com.myphotosanywhere.mpa.library

import androidx.annotation.DrawableRes
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.myphotosanywhere.mpa.ui.ShellIcon

/** Shared presentation only; metadata ownership and actions stay with their existing models. */
@Composable
internal fun DetailSection(title: String,@DrawableRes icon: Int,content: @Composable ColumnScope.()->Unit) {
    Surface(shape=RoundedCornerShape(20.dp),color=MaterialTheme.colorScheme.surfaceContainerHigh,
        modifier=Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
            Row(verticalAlignment=Alignment.CenterVertically,horizontalArrangement=Arrangement.spacedBy(12.dp)) {
                ShellIcon(icon)
                Text(title,style=MaterialTheme.typography.titleSmall,modifier=Modifier.semantics { heading() })
            }
            content()
        }
    }
}

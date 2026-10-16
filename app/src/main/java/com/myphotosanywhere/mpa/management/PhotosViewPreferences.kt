package com.myphotosanywhere.mpa.management

import android.app.Application
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.semantics.*
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.R
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** These preferences only change presentation; leaving the gallery already saves its pixel anchor. */
internal class PhotosViewPreferencesModel(application: Application): AndroidViewModel(application) {
    private val presentation=(application as MpaApplication).galleryPresentation
    val density=presentation.density
    val layout=presentation.layout
    private val working=MutableStateFlow(false)
    val busy=working.asStateFlow()
    private val failure=MutableStateFlow<String?>(null)
    val message=failure.asStateFlow()
    fun density(value: GridDensity) { if(value!=density.value) change { presentation.update(value) } }
    fun layout(value: GalleryLayout) { if(value!=layout.value) change { presentation.updateLayout(value) } }
    private fun change(action: suspend ()->Unit) {
        if(working.value) return
        working.value=true;failure.value=null
        viewModelScope.launch {
            try { action() }
            catch(cancelled: CancellationException) { throw cancelled }
            catch(error: java.io.IOException) { failure.value=error.message ?: "Cannot save this photo-view preference. Retry." }
            finally { working.value=false }
        }
    }
}

@Composable
internal fun PhotosViewPreferences(model: PhotosViewPreferencesModel=viewModel()) {
    val layout by model.layout.collectAsStateWithLifecycle()
    val density by model.density.collectAsStateWithLifecycle()
    val busy by model.busy.collectAsStateWithLifecycle()
    val message by model.message.collectAsStateWithLifecycle()
    message?.let { Text(it,color=MaterialTheme.colorScheme.error) }
    Text(stringResource(R.string.menu_photo_layout),style=MaterialTheme.typography.titleLarge,modifier=Modifier.semantics { heading() })
    GalleryLayout.entries.forEach { value ->
        PreferenceChoice(stringResource(when(value) {
            GalleryLayout.MOSAIC -> R.string.menu_layout_mosaic
            GalleryLayout.ROWS -> R.string.menu_layout_rows
            GalleryLayout.SQUARE -> R.string.menu_layout_square
        }),stringResource(when(value) {
            GalleryLayout.MOSAIC -> R.string.menu_mosaic_summary
            GalleryLayout.ROWS -> R.string.menu_rows_summary
            GalleryLayout.SQUARE -> R.string.menu_square_summary
        }),layout==value,"preference:layout:${value.name}",enabled=!busy) { model.layout(value) }
    }
    HorizontalDivider(Modifier.padding(vertical=12.dp))
    Text(stringResource(R.string.menu_photo_density),style=MaterialTheme.typography.titleLarge,modifier=Modifier.semantics { heading() })
    GridDensity.entries.forEach { value ->
        PreferenceChoice(stringResource(when(value) {
            GridDensity.LARGE -> R.string.menu_size_large
            GridDensity.DEFAULT -> R.string.menu_size_default
            GridDensity.COMPACT -> R.string.menu_size_compact
        }),pluralStringResource(R.plurals.menu_column_count,value.columns,value.columns),density==value,
            "preference:density:${value.name}",enabled=!busy) { model.density(value) }
    }
    Text(stringResource(R.string.menu_dates_summary),style=MaterialTheme.typography.bodyMedium,color=MaterialTheme.colorScheme.onSurfaceVariant)
}

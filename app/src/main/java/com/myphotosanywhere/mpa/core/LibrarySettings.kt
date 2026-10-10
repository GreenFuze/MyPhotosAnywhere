package com.myphotosanywhere.mpa.core

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow

data class StorageSettings(val originalBudget: Long = 2L*1024*1024*1024, val thumbnailBudget: Long = 256L*1024*1024,
    val faceIndexing: Boolean = false, val objectIndexing: Boolean = false, val onlineMaps: Boolean = false)
class LibrarySettings(context: Context) {
    private val prefs=context.getSharedPreferences("library-settings",Context.MODE_PRIVATE)
    val state=MutableStateFlow(StorageSettings(prefs.getLong("originalBudget",2L*1024*1024*1024),
        prefs.getLong("thumbnailBudget",256L*1024*1024),prefs.getBoolean("faces",false),
        prefs.getBoolean("objects",false),prefs.getBoolean("maps",false)))
    fun update(value: StorageSettings) {
        require(value.originalBudget>=0 && value.thumbnailBudget>=0)
        prefs.edit().putLong("originalBudget",value.originalBudget).putLong("thumbnailBudget",value.thumbnailBudget)
            .putBoolean("faces",value.faceIndexing).putBoolean("objects",value.objectIndexing).putBoolean("maps",value.onlineMaps).apply()
        state.value=value
    }
}

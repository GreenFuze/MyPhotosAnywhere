package com.myphotosanywhere.mpa.core

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow

data class StorageSettings(val originalBudget: Long = 2L*1024*1024*1024, val thumbnailBudget: Long = 256L*1024*1024,
    val faceIndexing: Boolean = false, val objectIndexing: Boolean = false, val onlineMaps: Boolean = false,
    val sidecarBackup: Boolean = false, val overnightIndexing: Boolean = false,
    val nightStart: Int = 0, val nightEnd: Int = 360, val indexCloud: Boolean = false, val indexingPaused: Boolean = false)
class LibrarySettings(context: Context) {
    private val prefs=context.getSharedPreferences("library-settings",Context.MODE_PRIVATE)
    val state=MutableStateFlow(StorageSettings(prefs.getLong("originalBudget",2L*1024*1024*1024),
        prefs.getLong("thumbnailBudget",256L*1024*1024),prefs.getBoolean("faces",false),
        prefs.getBoolean("objects",false),prefs.getBoolean("maps",false),prefs.getBoolean("sidecars",false),
        prefs.getBoolean("overnight",false),prefs.getInt("nightStart",0),prefs.getInt("nightEnd",360),
        prefs.getBoolean("indexCloud",false),prefs.getBoolean("indexingPaused",false)))
    fun update(value: StorageSettings) {
        require(value.originalBudget>=0 && value.thumbnailBudget>=0)
        require(value.nightStart in 0..1439 && value.nightEnd in 0..1439 && value.nightStart!=value.nightEnd)
        prefs.edit().putLong("originalBudget",value.originalBudget).putLong("thumbnailBudget",value.thumbnailBudget)
            .putBoolean("faces",value.faceIndexing).putBoolean("objects",value.objectIndexing).putBoolean("maps",value.onlineMaps)
            .putBoolean("sidecars",value.sidecarBackup).putBoolean("overnight",value.overnightIndexing)
            .putInt("nightStart",value.nightStart).putInt("nightEnd",value.nightEnd)
            .putBoolean("indexCloud",value.indexCloud).putBoolean("indexingPaused",value.indexingPaused).apply()
        state.value=value
    }
}

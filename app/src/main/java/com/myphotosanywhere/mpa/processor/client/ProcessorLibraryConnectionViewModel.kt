package com.myphotosanywhere.mpa.processor.client

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.viewModelScope

internal class ProcessorLibraryConnectionViewModel(application: Application, saved: SavedStateHandle) : AndroidViewModel(application) {
    val connection = ProcessorLibraryConnectionFlow(saved,viewModelScope,ProcessorLibraryAndroidAccess(application))
}

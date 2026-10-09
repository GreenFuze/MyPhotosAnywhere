package com.myphotosanywhere.mpa

import android.app.Application
import android.content.Intent
import android.net.Uri
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.myphotosanywhere.mpa.ai.AiHandoff
import com.myphotosanywhere.mpa.core.AiProvider
import com.myphotosanywhere.mpa.core.EditPrompt
import com.myphotosanywhere.mpa.data.Handoff
import com.myphotosanywhere.mpa.data.Photo
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

class LibraryViewModel(application: Application) : AndroidViewModel(application) {
    private val repository = (application as MpaApplication).repository
    val photos = repository.photos.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())
    val handoffs = repository.handoffs.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())
    private val _busy = MutableStateFlow(false)
    val busy = _busy.asStateFlow()
    private val _message = MutableStateFlow<String?>(null)
    val message = _message.asStateFlow()
    fun clearMessage() { _message.value = null }

    fun importPhotos(uris: List<Uri>, handoff: Handoff? = null) {
        if (uris.isEmpty() || _busy.value) return
        _busy.value = true
        viewModelScope.launch {
            var imported = 0
            var failed = 0
            var lastError: String? = null
            try {
                for (uri in uris) {
                    try {
                        repository.import(uri, handoff)
                        imported++
                    } catch (cancelled: CancellationException) { throw cancelled }
                    catch (error: Exception) { failed++; lastError = error.message }
                }
                _message.value = if (failed == 0) "$imported photo(s) saved locally." else
                    "$imported saved, $failed failed: ${lastError ?: "Cannot import this image."}"
            } finally { _busy.value = false }
        }
    }

    fun share(photo: Photo, provider: AiProvider, instructions: String, launch: (Intent) -> Unit) {
        if (_busy.value) return
        _busy.value = true
        viewModelScope.launch {
            try {
                EditPrompt.build(instructions)
                val intent = AiHandoff.prepare(getApplication(), photo, provider, instructions)
                val handoff = repository.recordHandoff(photo, provider.name, instructions)
                try { launch(intent) }
                catch (error: Exception) { repository.dismissHandoff(handoff.id); throw error }
                _message.value = "Instructions copied. After saving the AI output, use Import AI result."
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (error: Exception) { _message.value = error.message ?: "Could not share this photo." }
            finally { _busy.value = false }
        }
    }

    fun importResult(uri: Uri, handoffId: String) {
        if (_busy.value) return
        _busy.value = true
        viewModelScope.launch {
            try {
                // Read persisted state directly: reactive lists may not have loaded after process recreation.
                val handoff = repository.findHandoff(handoffId) ?: error("This handoff is no longer pending.")
                repository.import(uri, handoff)
                _message.value = "Edited copy saved. The original is preserved."
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (error: Exception) { _message.value = error.message ?: "Could not import the result." }
            finally { _busy.value = false }
        }
    }

    fun dismiss(handoff: Handoff) {
        viewModelScope.launch {
            try { repository.dismissHandoff(handoff.id) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (error: Exception) { _message.value = error.message ?: "Could not dismiss the handoff." }
        }
    }
}

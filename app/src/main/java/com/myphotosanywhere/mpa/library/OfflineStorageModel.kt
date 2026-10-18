package com.myphotosanywhere.mpa.library

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

/** This operation survives rotation; cancelling never rolls back already verified removals. */
internal class OfflineStorageModel(application: Application): AndroidViewModel(application) {
    private val app=application as MpaApplication
    private val repository=OfflineStorageRepository(app,app.database,app.cache)
    private val current=MutableStateFlow<OfflineStorageSummary?>(null)
    val summary=current.asStateFlow()
    private val active=MutableStateFlow(false)
    val working=active.asStateFlow()
    private val reading=MutableStateFlow(false)
    val loading=reading.asStateFlow()
    private val status=MutableStateFlow<String?>(null)
    val message=status.asStateFlow()
    private val completed=MutableStateFlow(OfflineCleanupResult())
    val progress=completed.asStateFlow()
    private var operation: Job?=null
    fun refresh() {
        if(active.value || reading.value) return
        reading.value=true
        viewModelScope.launch {
            try { current.value=repository.summary() }
            catch(cancelled: CancellationException) { throw cancelled }
            catch(error: java.io.IOException) { status.value=error.message ?: "Cannot measure offline storage. Retry." }
            catch(error: android.database.sqlite.SQLiteException) { status.value=error.message ?: "Cannot read offline storage. Retry." }
            catch(_: SecurityException) { status.value="Android denied storage access. Files were kept." }
            finally { reading.value=false }
        }
    }
    fun clear(cutoff: Long) {
        if(active.value || reading.value) return
        require(current.value?.cutoff==cutoff) { "Review the current cleanup estimate first." }
        active.value=true;status.value=null;completed.value=OfflineCleanupResult()
        operation=viewModelScope.launch {
            try {
                val result=repository.clear(cutoff) { completed.value=it }
                completed.value=result;status.value=result.message();current.value=repository.summary()
            } catch(cancelled: CancellationException) {
                status.value=completed.value.message()+" Cleanup stopped. Refresh the estimate to continue."
                throw cancelled
            } catch(error: java.io.IOException) { status.value=error.message ?: "Cleanup stopped. Unverified files were kept; retry." }
            catch(error: android.database.sqlite.SQLiteException) { status.value=error.message ?: "Cleanup stopped because its checkpoint could not be read. Remaining files were kept." }
            catch(_: SecurityException) { status.value="Android denied access. Remaining files were kept." }
            finally { active.value=false }
        }
    }
    fun stop() { operation?.cancel() }
}

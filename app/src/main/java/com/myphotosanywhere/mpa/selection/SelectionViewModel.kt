package com.myphotosanywhere.mpa.selection

import android.app.Application
import android.content.Intent
import android.os.Bundle
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.viewModelScope
import com.myphotosanywhere.mpa.MpaApplication
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

internal class SelectionViewModel(application: Application,private val saved: SavedStateHandle): AndroidViewModel(application) {
    private val app=application as MpaApplication
    private val repository=SelectionRepository(app)
    private val _targets=MutableStateFlow(saved.get<ArrayList<Bundle>>("selection")?.map(SelectionTarget::restore) ?: emptyList())
    val targets=_targets.asStateFlow()
    private val _busy=MutableStateFlow(false)
    val busy=_busy.asStateFlow()
    private val _total=MutableStateFlow(0)
    val total=_total.asStateFlow()
    private val _progress=MutableStateFlow(0)
    val progress=_progress.asStateFlow()
    private val _result=MutableStateFlow<SelectionResult?>(null)
    val result=_result.asStateFlow()
    private val _share=MutableStateFlow<Intent?>(null)
    val share=_share.asStateFlow()
    private val _notice=MutableStateFlow<String?>(null)
    val notice=_notice.asStateFlow()
    private var job: Job?=null
    private fun store(items: List<SelectionTarget>) {
        _targets.value=items;saved["selection"]=ArrayList(items.map { it.save() })
    }
    fun toggle(target: SelectionTarget) {
        if(_busy.value || _result.value!=null) return
        val selection=MediaSelection(_targets.value)
        if(selection.toggle(target)) store(selection.snapshot()) else _notice.value="Select up to ${MediaSelection.MAX_ITEMS} photos per operation."
    }
    fun ensureSelected(target: SelectionTarget) {
        if(_targets.value.none { it.matches(target) }) toggle(target)
    }
    fun replaceDrag(items: List<SelectionTarget>) {
        if(_busy.value || _result.value!=null) return
        check(items.size<=MediaSelection.MAX_ITEMS && items.map { it.key }.distinct().size==items.size)
        if(items!=_targets.value) store(items)
    }
    fun dragLimit() { _notice.value="Select up to ${MediaSelection.MAX_ITEMS} photos per operation." }
    fun clear() { if(!_busy.value) { store(emptyList());_result.value=null } }
    fun clearNotice() { _notice.value=null }
    fun dismissResult() { _result.value=null }
    fun shareLaunched() { _share.value=null;store(emptyList()) }
    fun cancel() { job?.cancel() }
    private suspend fun notifyMetadata() {
        try { app.metadata.changed() }
        catch(cancelled: CancellationException) { throw cancelled }
        catch(error: java.io.IOException) { _notice.value="Changes are saved locally. Annotation backup needs retry: ${error.message}" }
        catch(error: android.database.sqlite.SQLiteException) { _notice.value="Changes are saved locally. Annotation backup needs retry: ${error.message}" }
    }
    fun execute(action: BulkAction) {
        if(_busy.value || _targets.value.isEmpty()) return
        val batch=_targets.value
        _busy.value=true;_total.value=batch.size;_progress.value=0;_result.value=null
        job=viewModelScope.launch {
            var completed=0
            val failures=ArrayList<SelectionFailure>()
            try {
                if(action.kind==BulkKind.SHARE) {
                    _share.value=repository.share(batch) { _progress.value=it }
                } else {
                    for(target in batch) {
                        currentCoroutineContext().ensureActive()
                        withContext(NonCancellable) {
                            try { repository.apply(target,action);completed++ }
                            catch(error: java.io.IOException) { failures.add(SelectionFailure(target,error.message ?: "Original unavailable. Retry.")) }
                            catch(error: android.database.sqlite.SQLiteException) { failures.add(SelectionFailure(target,error.message ?: "Cannot update this item. Retry.")) }
                            _progress.value++
                        }
                    }
                    store(failures.map { it.target })
                    _result.value=SelectionResult(completed,failures,queued=action.kind==BulkKind.OFFLINE)
                    if(completed>0) notifyMetadata()
                }
            } catch(cancelled: CancellationException) {
                // Already committed metadata is retained; remaining targets stay selected for retry.
                val unfinished=batch.drop(_progress.value).map { SelectionFailure(it,"Operation cancelled; this item was not completed.") }
                if(action.kind!=BulkKind.SHARE) { store((failures+unfinished).map { it.target });_result.value=SelectionResult(completed,failures+unfinished,cancelled=true) }
                else _result.value=SelectionResult(0,batch.map { SelectionFailure(it,"Share preparation cancelled. Nothing was shared.") },cancelled=true)
                if(completed>0) withContext(NonCancellable) { notifyMetadata() }
                throw cancelled
            } catch(error: java.io.IOException) {
                _result.value=SelectionResult(0,batch.map { SelectionFailure(it,error.message ?: "Share preparation failed. Nothing was shared.") })
            } catch(error: android.database.sqlite.SQLiteException) {
                _result.value=SelectionResult(completed,batch.drop(_progress.value).map { SelectionFailure(it,error.message ?: "Cannot update the library. Retry.") })
            } finally { _busy.value=false;job=null }
        }
    }
}

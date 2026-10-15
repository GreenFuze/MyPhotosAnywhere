package com.myphotosanywhere.mpa.trash

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.viewModelScope
import androidx.paging.Pager
import androidx.paging.cachedIn
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.selection.SelectionTarget
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.io.IOException

internal data class TrashConfirmation(val candidates: List<TrashCandidate>,val failures: List<String>,val restore: Boolean)

internal class TrashViewModel(application: Application,private val saved: SavedStateHandle): AndroidViewModel(application) {
    private val app=application as MpaApplication
    private val _selected=MutableStateFlow<List<DriveTrashRecord>>(emptyList())
    val selected=_selected.asStateFlow()
    val sources=app.drive.sources.stateIn(viewModelScope,SharingStarted.WhileSubscribed(5000),emptyList())
    val pages=Pager(TrashPagingSource.CONFIG) { TrashPagingSource(app.database) }.flow.cachedIn(viewModelScope)
    private val _confirmation=MutableStateFlow<TrashConfirmation?>(null)
    val confirmation=_confirmation.asStateFlow()
    private val _busy=MutableStateFlow(false)
    val busy=_busy.asStateFlow()
    private val _message=MutableStateFlow<String?>(null)
    val message=_message.asStateFlow()

    init { viewModelScope.launch {
        val keys=saved.get<ArrayList<String>>("trash:selected").orEmpty()
        require(keys.size<=500)
        _selected.value=keys.mapNotNull { app.database.trash().find(it) }
        DriveTrashWorker.schedule(app)
    } }
    private fun select(rows: List<DriveTrashRecord>) {
        require(rows.size<=500 && rows.map { it.key }.distinct().size==rows.size)
        _selected.value=rows;saved["trash:selected"]=ArrayList(rows.map { it.key })
    }
    fun clearSelection()=select(emptyList())
    fun beginSelection(row: DriveTrashRecord)=select(listOf(row))
    fun toggle(row: DriveTrashRecord) {
        val current=_selected.value
        if(current.any { it.key==row.key }) select(current.filterNot { it.key==row.key })
        else if(current.size<500) select(current+row) else selectionLimit()
    }
    fun dismiss() { if(!_busy.value) { _confirmation.value=null;_message.value=null } }
    fun clearMessage() { _message.value=null }
    fun request(targets: List<SelectionTarget>) {
        if(_busy.value || _confirmation.value!=null) return
        require(targets.isNotEmpty() && targets.size<=500)
        _busy.value=true;_message.value=null
        viewModelScope.launch {
            val candidates=ArrayList<TrashCandidate>();val failures=ArrayList<String>()
            try {
                for(target in targets) {
                    try { candidates.add(app.trash.prepare(target)) }
                    catch(error: IOException) { failures.add("${target.name}: ${error.message}") }
                }
                val unique=candidates.distinctBy { "${it.record.accountId}:${it.record.remoteId}" }
                _confirmation.value=TrashConfirmation(unique,failures,false)
            } catch(cancelled: CancellationException) { throw cancelled }
            catch(error: android.database.sqlite.SQLiteException) { _message.value="Cannot prepare Trash: ${error.message}. Originals are preserved." }
            finally { _busy.value=false }
        }
    }
    fun restore(rows: List<DriveTrashRecord>) {
        if(_busy.value || _confirmation.value!=null) return
        require(rows.isNotEmpty() && rows.size<=500)
        _busy.value=true
        viewModelScope.launch {
            try {
                val candidates=rows.filter { it.confirmed && it.desired==null }.mapNotNull { row -> app.database.drive().source(row.bindingKey)?.let { source ->
                    TrashCandidate(row,source.email,source.folderName)
                } }
                _confirmation.value=TrashConfirmation(candidates,if(candidates.size==rows.size) emptyList() else listOf("Some items are pending, unavailable or disconnected. Refresh Trash or reconnect their sources before restoring."),true)
            } catch(cancelled: CancellationException) { throw cancelled }
            catch(error: android.database.sqlite.SQLiteException) { _message.value="Cannot prepare restore: ${error.message}" }
            finally { _busy.value=false }
        }
    }
    fun confirm(onQueued: ()->Unit) {
        val confirmation=_confirmation.value ?: return
        if(_busy.value || confirmation.candidates.isEmpty()) return
        _busy.value=true
        viewModelScope.launch {
            try {
                app.trash.queue(confirmation.candidates,confirmation.restore)
                _confirmation.value=null
                _message.value="${confirmation.candidates.size} ${if(confirmation.restore) "restore" else "Trash"} operations queued. Open Collections > Trash for verified progress."
                clearSelection();onQueued()
                DriveTrashWorker.schedule(app)
            } catch(cancelled: CancellationException) { throw cancelled }
            catch(error: IOException) { _message.value=error.message }
            catch(error: android.database.sqlite.SQLiteException) { _message.value="Cannot save the operation: ${error.message}. Check Trash before retrying." }
            finally { _busy.value=false }
        }
    }
    fun selectionLimit() { _message.value="Select up to 500 photos per operation." }
    fun cancel(key: String) {
        if(_busy.value) return
        _busy.value=true
        viewModelScope.launch {
            try { app.trash.cancel(key) }
            catch(cancelled: CancellationException) { throw cancelled }
            catch(error: android.database.sqlite.SQLiteException) { _message.value="Cannot stop the operation: ${error.message}" }
            finally { _busy.value=false }
        }
    }
    fun refresh() {
        if(_busy.value) return
        _busy.value=true
        viewModelScope.launch {
            try { app.trash.refresh();DriveTrashWorker.schedule(app) }
            catch(cancelled: CancellationException) { throw cancelled }
            catch(error: IOException) { _message.value=error.message }
            catch(error: android.database.sqlite.SQLiteException) { _message.value="Cannot refresh Trash: ${error.message}" }
            finally { _busy.value=false }
        }
    }
}

package com.myphotosanywhere.mpa.timeline

import android.app.Application
import android.os.Bundle
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.viewModelScope
import androidx.paging.Pager
import androidx.paging.cachedIn
import androidx.paging.map
import com.myphotosanywhere.mpa.MpaApplication
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import java.time.LocalDate
import java.time.ZoneId

internal data class TimelineViewerState(val selected: TimelineEntry,val session: ViewerSession?=null,val problem: String?=null)
internal data class TimelineItem(val entry: TimelineEntry,val generation: Long)

@OptIn(ExperimentalCoroutinesApi::class)
internal class TimelineViewModel(application: Application,private val saved: SavedStateHandle): AndroidViewModel(application) {
    private val app=application as MpaApplication
    val repository=app.timeline
    private data class Request(val query: TimelineQuery,val cursor: TimelineCursor?,val generation: Long=0)
    private val request=MutableStateFlow(Request(TimelineQuery(),null))
    private var configured=false
    private var observingAnchor=false
    private var viewerJob: Job?=null
    private val _viewer=MutableStateFlow<TimelineViewerState?>(null)
    val viewer=_viewer.asStateFlow()
    val restoration=request.map { it.generation }.stateIn(viewModelScope,SharingStarted.Eagerly,0L)
    private val _refreshedGeneration=MutableStateFlow(-1L)
    val refreshedGeneration=_refreshedGeneration.asStateFlow()
    private val _message=MutableStateFlow<String?>(null)
    val message=_message.asStateFlow()
    val paging=request.flatMapLatest { current ->
        val sources=java.util.Collections.synchronizedSet(mutableSetOf<TimelinePagingSource>())
        Pager(TimelinePagingSource.CONFIG,initialKey=current.cursor) {
            TimelinePagingSource(repository,current.query) { _refreshedGeneration.value=current.generation }.also { source ->
                sources.add(source);source.registerInvalidatedCallback { sources.remove(source) }
            }
        }.flow.map { data -> data.map { TimelineItem(it,current.generation) } }
            .onCompletion { synchronized(sources) { sources.toList().forEach { it.invalidate() };sources.clear() } }
    }.cachedIn(viewModelScope)

    fun configure(query: TimelineQuery) {
        if(configured && request.value.query==query) return
        configured=true
        val anchor=anchor(query)
        updateRequest(Request(query,anchor?.cursor,request.value.generation+1))
        reconcileAnchor(request.value,anchor)
    }

    private fun reconcileAnchor(current: Request,anchor: TimelineAnchor?) {
        if(anchor==null) return
        viewModelScope.launch {
            try {
                val cursor=repository.reanchor(current.query,anchor.cursor)
                if(cursor!=anchor.cursor && request.value==current && anchor(current.query)==anchor) {
                    saved[anchorName(current.query)]=anchorBundle(cursor,anchor.offset)
                    updateRequest(current.copy(cursor=cursor,generation=current.generation+1))
                }
            } catch(cancelled: CancellationException) { throw cancelled }
            catch(error: android.database.sqlite.SQLiteException) { _message.value=error.message ?: "Cannot restore this position. Retry." }
        }
    }

    private fun anchorBundle(cursor: TimelineCursor,offset: Int)=Bundle().apply {
        putString("key",cursor.key);cursor.millis?.let { putLong("millis",it) };putInt("offset",offset)
    }

    private fun anchorName(query: TimelineQuery)="timeline:"+listOf(query.filter,query.source,query.album,query.person).joinToString("|") { it?.let { value -> "${value.length}:$value" } ?: "-" }

    fun anchor(query: TimelineQuery=request.value.query): TimelineAnchor? {
        val value=saved.get<Bundle>(anchorName(query)) ?: return null
        val key=value.getString("key") ?: return null
        return TimelineAnchor(TimelineCursor(if(value.containsKey("millis")) value.getLong("millis") else null,key),value.getInt("offset"))
    }

    fun rememberAnchor(entry: TimelineEntry,offset: Int) {
        saved[anchorName(request.value.query)]=Bundle().apply {
            putString("key",entry.key);entry.millis?.let { putLong("millis",it) };putInt("offset",offset)
        }
    }

    private fun updateRequest(value: Request) { observingAnchor=false;request.value=value }
    fun restored(generation: Long) { if(request.value.generation==generation) observingAnchor=true }
    fun rememberVisibleAnchor(entry: TimelineEntry,offset: Int,generation: Long) {
        if(observingAnchor && _viewer.value==null && request.value.generation==generation) rememberAnchor(entry,offset)
    }

    fun jump(date: LocalDate,zone: ZoneId) {
        viewModelScope.launch {
            try {
                val current=request.value
                val cursor=repository.seek(current.query,date.plusDays(1).atStartOfDay(zone).toInstant().toEpochMilli())
                if(cursor==null) _message.value="No photos on or before this date."
                else if(request.value.query==current.query) {
                    saved[anchorName(current.query)]=Bundle().apply { putString("key",cursor.key);cursor.millis?.let { putLong("millis",it) };putInt("offset",0) }
                    updateRequest(current.copy(cursor=cursor,generation=current.generation+1))
                }
            } catch(cancelled: CancellationException) { throw cancelled }
            catch(error: android.database.sqlite.SQLiteException) { _message.value=error.message ?: "Cannot jump to this date. Retry." }
        }
    }

    fun restoreAnchor() {
        val current=request.value
        updateRequest(current.copy(cursor=anchor()?.cursor,generation=current.generation+1))
        reconcileAnchor(request.value,anchor())
    }

    fun open(entry: TimelineEntry,offset: Int?=null,viewport: TimelineAnchor?=null) {
        closeViewer(restore=false)
        // Gallery position and viewer page are independent. Preserve the first
        // visible media and its measured offset, rather than positioning a later
        // tapped tile with a large negative scroll offset.
        val origin=viewport ?: anchor()
        if(origin!=null) saved[anchorName(request.value.query)]=anchorBundle(origin.cursor,origin.offset)
        else rememberAnchor(entry,offset ?: 0)
        _viewer.value=TimelineViewerState(entry)
        val query=request.value.query
        viewerJob=viewModelScope.launch {
            var session: ViewerSession?=null
            var handedOff=false
            try {
                session=app.viewerSessions.prepare(query,entry)
                if(_viewer.value?.selected?.key==entry.key) {
                    _viewer.value=TimelineViewerState(entry,session);handedOff=true
                }
            } catch(cancelled: CancellationException) { throw cancelled }
            catch(error: java.io.IOException) {
                currentCoroutineContext().ensureActive()
                _viewer.value=TimelineViewerState(entry,problem=error.message ?: "Cannot prepare browsing. Retry or return to the gallery.")
            }
            catch(error: android.database.sqlite.SQLiteException) {
                currentCoroutineContext().ensureActive()
                _viewer.value=TimelineViewerState(entry,problem=error.message ?: "Cannot prepare browsing. Retry or return to the gallery.")
            }
            finally { if(!handedOff) withContext(NonCancellable+Dispatchers.IO) { session?.close() } }
        }
    }

    fun clearMessage() { _message.value=null }
    fun closeViewer(restore: Boolean=true) {
        observingAnchor=false
        viewerJob?.cancel();viewerJob=null
        val session=_viewer.value?.session
        _viewer.value=null
        if(session!=null) app.viewerSessions.dispose(session)
        if(restore) restoreAnchor()
    }
    override fun onCleared() {
        viewerJob?.cancel()
        _viewer.value?.session?.let(app.viewerSessions::dispose)
    }
}

package com.myphotosanywhere.mpa.search

import android.os.Bundle
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.FlowPreview
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.launch

@OptIn(FlowPreview::class)
internal class SearchViewModel(private val saved: SavedStateHandle): ViewModel() {
    private val _draft=MutableStateFlow(saved.get<Bundle>("criteria")?.let(SearchCriteria::restore) ?: SearchCriteria())
    val draft=_draft.asStateFlow()
    private val _applied=MutableStateFlow(_draft.value)
    val applied=_applied.asStateFlow()
    private val history=RecentQueries(saved.get<ArrayList<String>>("recents").orEmpty())
    private val _recents=MutableStateFlow(history.snapshot())
    val recents=_recents.asStateFlow()
    private val _notice=MutableStateFlow<String?>(null)
    val notice=_notice.asStateFlow()
    init { viewModelScope.launch { _draft.debounce(300).collect { _applied.value=it } } }
    fun update(criteria: SearchCriteria) { _draft.value=criteria;saved["criteria"]=criteria.save() }
    fun text(value: String) {
        if(value.length<=256) update(_draft.value.copy(text=value)) else _notice.value="Search supports up to 256 characters."
    }
    fun submit() {
        _applied.value=_draft.value;history.add(_draft.value.text);storeHistory()
    }
    private fun storeHistory() { _recents.value=history.snapshot();saved["recents"]=ArrayList(_recents.value) }
    fun clearHistory() { history.clear();storeHistory() }
    fun clearFilters() { update(_draft.value.copy(type=com.myphotosanywhere.mpa.timeline.TimelineMediaType.ALL,filter="All",source=null,dates=null));submit() }
    fun clearNotice() { _notice.value=null }
}

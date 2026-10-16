package com.myphotosanywhere.mpa.timeline

import androidx.paging.PagingConfig
import androidx.paging.PagingSource
import androidx.paging.PagingState
import kotlinx.coroutines.CancellationException

internal class TimelinePagingSource(private val repository: TimelineRepository,private val query: TimelineQuery,
    private val refreshAnchor: ()->TimelineCursor?={ null },private val onRefresh: ()->Unit={}): PagingSource<TimelineCursor,TimelineEntry>() {
    private val observation=repository.observe(::invalidate)
    init { registerInvalidatedCallback { observation.close() } }
    override fun getRefreshKey(state: PagingState<TimelineCursor,TimelineEntry>): TimelineCursor?=
        refreshAnchor() ?: state.anchorPosition?.let { state.closestItemToPosition(it)?.cursor }

    override suspend fun load(params: LoadParams<TimelineCursor>): LoadResult<TimelineCursor,TimelineEntry> {
        try {
            val entries=when(params) {
                is LoadParams.Refresh -> repository.around(query,params.key,params.loadSize)
                is LoadParams.Prepend -> repository.page(query,params.key,TimelineDirection.BEFORE,params.loadSize)
                is LoadParams.Append -> repository.page(query,params.key,TimelineDirection.AFTER,params.loadSize)
            }
            if(invalid) return LoadResult.Invalid()
            val first=entries.firstOrNull()?.cursor
            val last=entries.lastOrNull()?.cursor
            val previous=first?.takeIf { repository.page(query,it,TimelineDirection.BEFORE,1).isNotEmpty() }
            val next=last?.takeIf { repository.page(query,it,TimelineDirection.AFTER,1).isNotEmpty() }
            if(invalid) return LoadResult.Invalid()
            if(params is LoadParams.Refresh) onRefresh()
            return LoadResult.Page(entries,previous,next)
        } catch(cancelled: CancellationException) { throw cancelled }
        catch(error: java.io.IOException) { return LoadResult.Error(error) }
        catch(error: android.database.sqlite.SQLiteException) { return LoadResult.Error(error) }
    }

    companion object {
        val CONFIG=PagingConfig(pageSize=60,initialLoadSize=90,prefetchDistance=18,maxSize=300,enablePlaceholders=false)
    }
}

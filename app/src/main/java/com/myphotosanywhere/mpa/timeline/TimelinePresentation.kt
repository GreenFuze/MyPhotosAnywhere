package com.myphotosanywhere.mpa.timeline

internal data class TimelineRefreshReceipt(val query: TimelineQuery,val generation: Long,val empty: Boolean)
internal enum class TimelineContent { LOADING, EMPTY, PHOTOS, ERROR }

/** An empty result belongs to an executed query, never to a transient Paging slot count. */
internal class TimelinePresentation {
    fun content(query: TimelineQuery,generation: Long,receipt: TimelineRefreshReceipt?,retained: Int,failed: Boolean): TimelineContent {
        require(retained>=0)
        if(retained>0) return TimelineContent.PHOTOS
        if(receipt?.query==query && receipt.generation==generation && receipt.empty) return TimelineContent.EMPTY
        return if(failed) TimelineContent.ERROR else TimelineContent.LOADING
    }
}

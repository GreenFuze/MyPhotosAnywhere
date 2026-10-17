package com.myphotosanywhere.mpa.timeline

import org.junit.Assert.*
import org.junit.Test

class TimelinePresentationTest {
    private val resolver=TimelinePresentation()
    private val query=TimelineQuery(search="missing")
    private fun content(receipt: TimelineRefreshReceipt?=null,rows: Int=0,failed: Boolean=false)=resolver.content(query,7,receipt,rows,failed)
    @Test fun newQueryWithoutExecutedResultIsLoading() { assertEquals(TimelineContent.LOADING,content()) }
    @Test fun completedEmptyRemainsEmptyWhilePagingTemporarilyHasNoRows() {
        val receipt=TimelineRefreshReceipt(query,7,true)
        repeat(20) { assertEquals(TimelineContent.EMPTY,content(receipt)) }
    }
    @Test fun anotherQueryCannotSupplyAnEmptyResultForCurrentSearch() {
        assertEquals(TimelineContent.LOADING,content(TimelineRefreshReceipt(TimelineQuery(),7,true)))
    }
    @Test fun previousAnchorGenerationCannotSupplyAnEmptyResult() {
        assertEquals(TimelineContent.LOADING,content(TimelineRefreshReceipt(query,6,true)))
    }
    @Test fun nonemptyReceiptWaitsForItsPageInsteadOfInventingAnEmptyLibrary() {
        assertEquals(TimelineContent.LOADING,content(TimelineRefreshReceipt(query,7,false)))
    }
    @Test fun usablePhotosRemainVisibleDuringRefreshFailure() { assertEquals(TimelineContent.PHOTOS,content(rows=3,failed=true)) }
    @Test fun failedFirstLoadIsAnErrorButPreviouslyCompletedEmptyRemainsUsable() {
        assertEquals(TimelineContent.ERROR,content(failed=true))
        assertEquals(TimelineContent.EMPTY,content(TimelineRefreshReceipt(query,7,true),failed=true))
    }
    @Test fun invalidRowCountsFailFast() { assertThrows(IllegalArgumentException::class.java) { content(rows=-1) } }
}

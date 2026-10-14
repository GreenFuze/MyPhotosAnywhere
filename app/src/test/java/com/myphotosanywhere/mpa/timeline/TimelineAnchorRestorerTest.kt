package com.myphotosanywhere.mpa.timeline

import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class TimelineAnchorRestorerTest {
    private val anchor=TimelineAnchor(TimelineCursor(100L,"photo:anchor"),-8)
    @Test fun prependCancelsObsoletePlacementAndRecomputesTheCanonicalIndex()=runTest {
        val snapshots=MutableStateFlow(listOf("first","photo:anchor"))
        val placements=mutableListOf<Pair<Int,Int>>();var cancelled=0;var corrections=0
        val restorer=TimelineAnchorRestorer(object: TimelinePlacement {
            override suspend fun place(index: Int,offset: Int) { placements.add(index to offset) }
            override suspend fun align(key: String,offset: Int) {
                assertEquals("photo:anchor",key);assertEquals(-8,offset)
                if(placements.size==1) try { awaitCancellation() } finally { cancelled++ }
                else corrections++
            }
        })
        val result=async { restorer.restore(anchor,snapshots) }
        runCurrent();assertFalse(result.isCompleted)
        snapshots.value=listOf("new","first","photo:anchor")
        assertTrue(result.await());assertEquals(listOf(1 to -8,2 to -8),placements)
        assertEquals(1,cancelled);assertEquals(1,corrections)
    }
    @Test fun anchorRemovalCancelsPlacementAndReportsTheNeedForExistingNearestItemRecovery()=runTest {
        val snapshots=MutableStateFlow(listOf("photo:anchor"));var cancelled=false
        val restorer=TimelineAnchorRestorer(object: TimelinePlacement {
            override suspend fun place(index: Int,offset: Int) { assertEquals(0,index) }
            override suspend fun align(key: String,offset: Int) { try { awaitCancellation() } finally { cancelled=true } }
        })
        val result=async { restorer.restore(anchor,snapshots) }
        runCurrent();snapshots.value=listOf("other")
        assertFalse(result.await());assertTrue(cancelled)
    }
    @Test fun queryOrLifecycleCancellationDoesNotReportSuccessfulRestoration()=runTest {
        var cancelled=false
        val restorer=TimelineAnchorRestorer(object: TimelinePlacement {
            override suspend fun place(index: Int,offset: Int) { }
            override suspend fun align(key: String,offset: Int) { try { awaitCancellation() } finally { cancelled=true } }
        })
        val result=async { restorer.restore(anchor,MutableStateFlow(listOf("photo:anchor"))) }
        runCurrent();result.cancelAndJoin();assertTrue(result.isCancelled);assertTrue(cancelled)
    }
}

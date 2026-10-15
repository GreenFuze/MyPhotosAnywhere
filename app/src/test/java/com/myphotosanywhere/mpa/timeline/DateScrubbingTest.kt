package com.myphotosanywhere.mpa.timeline

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test
import java.time.YearMonth

@OptIn(ExperimentalCoroutinesApi::class)
class DateScrubbingTest {
    private val old=YearMonth.of(1998,2)
    private val recent=YearMonth.of(2026,10)
    @Test fun verticalScaleHasNewestAtTopAndClampedOldestAtBottom() {
        val scale=DateScrubScale(MonthNavigation(old,recent))
        assertEquals(recent,scale.month(-2f));assertEquals(old,scale.month(2f))
        for(i in 0..scale.months.span) {
            val month=old.plusMonths(i);assertEquals(month,scale.month(scale.fraction(month)))
        }
        assertEquals(0f,scale.fraction(5f,10f,100f),0f)
        assertEquals(1f,scale.fraction(110f,10f,100f),0f)
        assertEquals(0f,scale.fraction(10f,10f,10f),0f)
    }
    @Test fun sameMonthAndInvalidGeometryAreExplicit() {
        val scale=DateScrubScale(MonthNavigation(old,old))
        assertEquals(old,scale.month(0.9f))
        for(value in listOf(Float.NaN,Float.POSITIVE_INFINITY,Float.NEGATIVE_INFINITY)) {
            assertThrows(IllegalArgumentException::class.java) { scale.month(value) }
            assertThrows(IllegalArgumentException::class.java) { scale.fraction(value,0f,10f) }
        }
        assertThrows(IllegalArgumentException::class.java) { scale.fraction(1f,2f,1f) }
    }
    @Test fun rapidMovesSeekOnlyLatestAfterQuietPeriod()=runTest {
        val calls=mutableListOf<YearMonth>()
        val coordinator=DateScrubCoordinator(this,{ calls.add(it) })
        repeat(50) { coordinator.offer(old.plusMonths(it.toLong()));advanceTimeBy(30) }
        assertTrue(calls.isEmpty());advanceTimeBy(100);runCurrent()
        assertEquals(listOf(old.plusMonths(49)),calls)
        assertTrue(coordinator.state.value.dragging);assertFalse(coordinator.state.value.pending)
        coordinator.finish();assertFalse(coordinator.state.value.dragging)
    }
    @Test fun releaseFlushesMostRecentMonthWithoutWaitingOrRepeatingCompletedSeek()=runTest {
        val calls=mutableListOf<YearMonth>()
        val coordinator=DateScrubCoordinator(this,{ calls.add(it) })
        coordinator.offer(old);coordinator.offer(recent);coordinator.finish();runCurrent()
        assertEquals(listOf(recent),calls);assertFalse(coordinator.state.value.pending)
        coordinator.finish();advanceUntilIdle();assertEquals(listOf(recent),calls)
    }
    @Test fun supersededInFlightSeekIsCancelledBeforeItCanCommit()=runTest {
        var cancelled=0;val committed=mutableListOf<YearMonth>()
        val coordinator=DateScrubCoordinator(this,{ month ->
            if(month==old) try { awaitCancellation() } finally { cancelled++ }
            else committed.add(month)
        })
        coordinator.offer(old);advanceTimeBy(100);runCurrent()
        coordinator.offer(recent);coordinator.finish();runCurrent()
        assertEquals(1,cancelled);assertEquals(listOf(recent),committed)
    }
    @Test fun leavingContextCancelsPendingAndRunningWorkWithoutStalePreview()=runTest {
        val calls=mutableListOf<YearMonth>();var cancelled=0
        val coordinator=DateScrubCoordinator(this,{ calls.add(it);try { awaitCancellation() } finally { cancelled++ } })
        coordinator.offer(old);coordinator.cancel();advanceUntilIdle();assertTrue(calls.isEmpty())
        coordinator.offer(recent);advanceTimeBy(100);runCurrent();coordinator.cancel();runCurrent()
        assertEquals(1,cancelled);assertEquals(DateScrubState(),coordinator.state.value)
    }
    @Test fun repeatedSameMonthDoesNotRestartDebounce()=runTest {
        val calls=mutableListOf<YearMonth>();val coordinator=DateScrubCoordinator(this,{ calls.add(it) })
        coordinator.offer(recent);advanceTimeBy(80);coordinator.offer(recent);advanceTimeBy(20);runCurrent()
        assertEquals(listOf(recent),calls);coordinator.cancel()
    }
}

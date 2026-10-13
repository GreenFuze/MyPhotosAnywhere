package com.myphotosanywhere.mpa.management

import com.myphotosanywhere.mpa.importing.ImportJob
import com.myphotosanywhere.mpa.drive.SyncState
import org.junit.Assert.*
import org.junit.Test

class ActivitySummaryTest {
    private fun job(state: String)=ImportJob("job","captured","TAKEOUT",state=state)
    @Test fun errorsTakePrecedenceOverOtherRunningWork() { assertEquals("Activity needs attention",ActivitySummary(listOf(job("RUNNING")),listOf(SyncState("a",error="Reconnect"))).label) }
    @Test fun importAndSyncRemainDistinct() { assertEquals("1 imports running · refreshing Drive",ActivitySummary(listOf(job("RUNNING")),listOf(SyncState("a",running=true))).label) }
    @Test fun waitingAndPausedAreNeverReportedAsComplete() { assertEquals("Import waiting to resume",ActivitySummary(listOf(job("WAITING_FOR_APP")),emptyList()).label);assertEquals("Import paused",ActivitySummary(listOf(job("PAUSED")),emptyList()).label) }
    @Test fun completedWorkHasNoPersistentStrip() { assertNull(ActivitySummary(listOf(job("COMPLETE")),emptyList()).label) }
    @Test fun readyReviewAndSyncOnlyRemainDiscoverable() { assertEquals("Import ready to review",ActivitySummary(listOf(job("PREVIEW")),emptyList()).label);assertEquals("Refreshing Drive library",ActivitySummary(emptyList(),listOf(SyncState("a",running=true))).label) }
}

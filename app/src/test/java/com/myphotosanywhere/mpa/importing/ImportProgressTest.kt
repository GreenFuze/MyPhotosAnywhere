package com.myphotosanywhere.mpa.importing

import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test

class ImportProgressTest {
    private val job=ImportJob("job","binding","TAKEOUT",state="RUNNING")
    private fun item(id: String, state: String, selected: Boolean=true)=
        ImportItem(id,job.id,"content://zip","$id.jpg","$id.jpg","image/jpeg",100,selected=selected,state=state)

    @Test fun verifiedBytesDoNotCountUntilMetadataCommitAndOtherJobsStayOut() {
        val progress=ImportProgress.from(job,listOf(item("done","COMPLETE"),item("upload","VERIFIED"),
            item("skipped","PENDING",false),item("other","COMPLETE").copy(job="other")))
        assertEquals(1,progress.complete); assertEquals(2,progress.total)
        assertEquals("Saving upload.jpg",progress.detail)
    }
    @Test fun uploadPercentageIsSeparateFromVerifiedFileCountAndBoundsOffsets() {
        val progress=ImportProgress.from(job,listOf(item("photo","UPLOADING").copy(offset=25)))
        assertEquals(0,progress.complete); assertEquals("Uploading photo.jpg · 25%",progress.detail)
        assertTrue(ImportProgress.from(job,listOf(item("photo","UPLOADING").copy(offset=101))).detail.endsWith("100%"))
        assertEquals("Preparing photo.jpg",ImportProgress.from(job,listOf(item("photo","STAGING"))).detail)
    }
    @Test fun terminalStateOverridesStaleTransferAndZeroTotalsAreValid() {
        assertEquals("Needs attention · Open MPA to resume",ImportProgress.from(job.copy(state="FAILED"),listOf(item("photo","UPLOADING"))).detail)
        assertEquals("Paused · Open MPA to resume",ImportProgress.from(job.copy(state="PAUSED"),emptyList()).detail)
        assertEquals(0,ImportProgress.from(job.copy(state="COMPLETE"),emptyList()).total)
    }
    @Test fun batchKeepsWorkingAcrossFilesUntilDurableCompletion() = runBlocking {
        var calls=0
        assertFalse(ImportBatch({ ++calls<4 },{ false }).run())
        assertEquals(4,calls)
    }
    @Test fun batchYieldsAfterFileWithoutConsumingNextCheckpoint() = runBlocking {
        var calls=0
        assertTrue(ImportBatch({ calls++; true },{ calls==2 }).run())
        assertEquals(2,calls)
    }
    @Test fun failedOrPausedRepositoryStopsBatchWithoutContinuation() = runBlocking {
        var calls=0
        assertFalse(ImportBatch({ calls++; false },{ error("Cannot yield failed work") }).run())
        assertEquals(1,calls)
    }
    @Test fun connectionErrorOverridesStaleStagingAndUploadStates() {
        val waiting=job.copy(error="Cannot resolve Google Drive. Retrying automatically (1/3).")
        val progress=ImportProgress.from(waiting,listOf(item("clip","STAGING")))
        assertEquals(waiting.error,progress.detail)
        assertEquals(0,progress.complete)
        assertEquals(waiting.error,ImportProgress.from(waiting.copy(state="FAILED"),listOf(item("clip","UPLOADING"))).detail)
    }
}

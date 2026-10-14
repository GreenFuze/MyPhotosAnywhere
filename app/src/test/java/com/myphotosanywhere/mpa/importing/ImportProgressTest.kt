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
    @Test fun selectionAndBlockedStatesDoNotClaimTransferIsRunning() {
        val stale=listOf(item("photo","UPLOADING").copy(offset=50))
        assertEquals("Review selection, then import selected files",ImportProgress.from(job.copy(state="PREVIEW"),stale).detail)
        assertEquals("Choose photos in the Google Photos picker",ImportProgress.from(job.copy(state="PICKING"),emptyList()).detail)
        assertEquals("Downloading Takeout ZIP",ImportProgress.from(job.copy(state="DOWNLOADING"),emptyList()).detail)
        assertEquals("Resume this import to continue",ImportProgress.from(job.copy(state="WAITING_FOR_APP"),stale).detail)
        assertEquals("Saved error",ImportProgress.from(job.copy(state="PREVIEW",error="Saved error"),stale).detail)
    }
    @Test fun terminalStateOverridesStaleTransferAndZeroTotalsAreValid() {
        assertEquals("Import needs attention. Review the error and resume.",ImportProgress.from(job.copy(state="FAILED"),listOf(item("photo","UPLOADING"))).detail)
        assertEquals("Paused · Resume when ready",ImportProgress.from(job.copy(state="PAUSED"),emptyList()).detail)
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
    @Test fun storedLegacyOpenAppInstructionsAreRenderedInContextWithoutChangingTheSavedFailure() {
        val error="Background import stopped. Open MPA and resume; transfer checkpoints are preserved."
        val saved=job.copy(state="FAILED",error=error)
        val detail=ImportProgress.from(saved,emptyList()).detail
        assertEquals("Background import stopped. Resume this import; transfer checkpoints are preserved.",detail)
        assertEquals(error,saved.error)
        assertEquals("Unknown failure: original missing",ImportStatusText.inApp("Unknown failure: original missing"))
        assertEquals("Android could not start background import. Resume this import to continue; transfer checkpoints are preserved.",
            ImportStatusText.inApp("Android could not start background import. Reopen MPA to continue; transfer checkpoints are preserved."))
    }
    @Test fun onlyExternalStoppedImportNotificationAsksToOpenMpa() {
        val paused=ImportProgress.from(job.copy(state="PAUSED"),emptyList())
        assertFalse(paused.detail.contains("Open MPA"))
        assertTrue(ImportStatusText.notification(paused).endsWith("Open MPA to review this import"))
        val running=ImportProgress.from(job,emptyList())
        assertEquals(running.detail,ImportStatusText.notification(running))
    }

}

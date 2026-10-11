package com.myphotosanywhere.mpa.importing

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.PhotoDatabase
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ImportForegroundRecoveryTest {
    private fun database()=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).allowMainThreadQueries().build()
    @Test fun blockedWorkRetainsCheckpointsAndWakesOnceWhenVisible() = runBlocking<Unit> {
        val db=database()
        try {
            val dao=db.imports(); val job=ImportJob("job","original","TAKEOUT",state="RUNNING")
            val item=ImportItem("item",job.id,"content://zip","clip.mp4","clip.mp4","video/mp4",200,
                state="UPLOADING",sha256="sha",md5="md5",staged="private",remoteId="reserved",sessionUri="session",offset=123)
            dao.saveJob(job); dao.insertItem(item)
            val recovery=ImportForegroundRecovery(dao)
            assertTrue(recovery.record(job.id))
            assertEquals("WAITING_FOR_APP",dao.job(job.id)?.state)
            val scheduled=mutableListOf<Pair<String,Boolean>>()
            repeat(2) { recovery.resume({ it==job.binding }) { id,replace -> scheduled.add(id to replace) } }
            assertEquals(listOf(job.id to false),scheduled)
            assertEquals(job,dao.job(job.id)); assertEquals(item,dao.item(item.id))
        } finally { db.close() }
    }
    @Test fun oldRetryIsReplacedButPausedCancelledAndDisconnectedJobsStayStopped() = runBlocking<Unit> {
        val db=database()
        try {
            val dao=db.imports(); val recovery=ImportForegroundRecovery(dao)
            val legacy=ImportJob("legacy","connected","TAKEOUT",state="RUNNING",error=ImportForegroundRecovery.LEGACY_MESSAGE)
            dao.saveJob(legacy)
            val unchanged=(listOf("PAUSED","CANCELLED","FAILED","COMPLETE").map { ImportJob(it,"connected","TAKEOUT",state=it,error=ImportForegroundRecovery.LEGACY_MESSAGE) }+
                ImportJob("disconnected","missing","TAKEOUT",state="WAITING_FOR_APP",error="wait"))
            unchanged.forEach { dao.saveJob(it) }
            val scheduled=mutableListOf<Pair<String,Boolean>>()
            recovery.resume({ it=="connected" }) { id,replace -> scheduled.add(id to replace) }
            assertEquals(listOf("legacy" to true),scheduled)
            unchanged.forEach { assertEquals(it,dao.job(it.id)) }
            assertEquals(legacy.copy(error=null),dao.job(legacy.id))
            unchanged.filter { it.state!="WAITING_FOR_APP" }.forEach { assertFalse(recovery.record(it.id)) }
        } finally { db.close() }
    }
    @Test fun pauseRacingVisibleRecoveryCannotBeResurrected() = runBlocking<Unit> {
        val db=database()
        try {
            val dao=db.imports(); val job=ImportJob("race","source","TAKEOUT",state="WAITING_FOR_APP")
            dao.saveJob(job)
            ImportForegroundRecovery(dao).resume({ dao.saveJob(job.copy(state="PAUSED")); true }) { _,_ -> fail("Must not enqueue paused import") }
            assertEquals("PAUSED",dao.job(job.id)?.state)
            assertEquals(0,dao.connectionState(job.id,"COMPLETE",null))
            assertEquals(0,dao.connectionState(job.id,"FAILED","late failure"))
            dao.clearRunningError(job.id)
            assertEquals(job.copy(state="PAUSED"),dao.job(job.id))
        } finally { db.close() }
    }
    private class ForegroundServiceStartNotAllowedException: IllegalStateException()
    @Test fun onlyAndroidStartRefusalsEnterWaitingAndProgrammingFailuresFailFast() {
        assertTrue(ImportForegroundRecovery.isStartRefused(ForegroundServiceStartNotAllowedException()))
        assertTrue(ImportForegroundRecovery.isStartRefused(IllegalStateException("wrapped",ForegroundServiceStartNotAllowedException())))
        assertFalse(ImportForegroundRecovery.isStartRefused(IllegalStateException("Worker already finished")))
        assertFalse(ImportForegroundRecovery.isStartRefused(java.io.IOException("unrelated")))
    }
}

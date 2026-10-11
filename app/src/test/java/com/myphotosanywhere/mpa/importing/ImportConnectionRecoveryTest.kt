package com.myphotosanywhere.mpa.importing

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.drive.DriveConnectionException
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.net.UnknownHostException

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ImportConnectionRecoveryTest {
    private fun database()=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).allowMainThreadQueries().build()
    private val failure get()=requireNotNull(DriveConnectionException.from(UnknownHostException("network")))
    @Test fun retriesPreserveVerifiedFilesAndEveryInFlightCheckpoint() = runBlocking<Unit> {
        val db=database()
        try {
            val dao=db.imports(); val recovery=ImportConnectionRecovery(dao)
            val job=ImportJob("job","original-destination","TAKEOUT",state="RUNNING")
            val item=ImportItem("video","job","content://zip","clip.mp4","clip.mp4","video/mp4",100,
                state="UPLOADING",staged="private-stage",remoteId="reserved",sessionUri="session",offset=42,sha256="sha",md5="md5")
            val complete=item.copy(id="done",state="COMPLETE",offset=100,staged=null,photoId="photo")
            dao.saveJob(job); dao.insertItem(item); dao.insertItem(complete)
            repeat(3) { attempt ->
                assertTrue(recovery.record(job.id,attempt,failure))
                assertEquals(item,dao.item(item.id)); assertEquals(complete,dao.item(complete.id))
                assertEquals(job.binding,dao.job(job.id)?.binding)
                assertEquals("RUNNING",dao.job(job.id)?.state)
                assertTrue(requireNotNull(dao.job(job.id)?.error).contains("(${attempt+1}/3)"))
            }
        } finally { db.close() }
    }
    @Test fun retryLimitStopsClearlyAndRetainsSourceAndUploadOffset() = runBlocking<Unit> {
        val db=database()
        try {
            val dao=db.imports(); val job=ImportJob("job","source","TAKEOUT",state="RUNNING")
            val item=ImportItem("item",job.id,"content://zip","clip.mp4","clip.mp4","video/mp4",100,state="STAGING")
            dao.saveJob(job); dao.insertItem(item)
            assertFalse(ImportConnectionRecovery(dao).record(job.id,3,failure))
            assertEquals("FAILED",dao.job(job.id)?.state)
            assertTrue(requireNotNull(dao.job(job.id)?.error).contains("Automatic retries stopped"))
            assertEquals(item,dao.item(item.id))
        } finally { db.close() }
    }
    @Test fun stoppedJobsCannotBeResurrectedByALateConnectionFailure() = runBlocking<Unit> {
        val db=database()
        try {
            val dao=db.imports()
            for(state in listOf("PAUSED","CANCELLED","COMPLETE","FAILED")) {
                val job=ImportJob(state,"source","TAKEOUT",state=state,error="keep")
                dao.saveJob(job)
                assertFalse(ImportConnectionRecovery(dao).record(job.id,0,failure))
                assertEquals(job,dao.job(job.id))
            }
            assertFalse(ImportConnectionRecovery(dao).record("missing",0,failure))
        } finally { db.close() }
    }
}

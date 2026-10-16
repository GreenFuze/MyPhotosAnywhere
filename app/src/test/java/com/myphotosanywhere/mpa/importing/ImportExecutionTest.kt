package com.myphotosanywhere.mpa.importing

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.drive.DriveConnectionException
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.net.UnknownHostException

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ImportExecutionTest {
    private fun database()=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).allowMainThreadQueries().build()
    private val failure get()=requireNotNull(DriveConnectionException.from(UnknownHostException("secret session")))
    @Test fun connectionRetriesKeepRunningProgressAndUploadCheckpoints() = runBlocking<Unit> {
        val db=database()
        try {
            val dao=db.imports(); dao.saveJob(ImportJob("job","destination","TAKEOUT",state="RUNNING"))
            val item=ImportItem("item","job","content://zip","clip.mp4","clip.mp4","video/mp4",100,
                state="UPLOADING",remoteId="reserved",sessionUri="session",offset=42,sha256="sha",md5="md5")
            dao.insertItem(item)
            var calls=0; val waits=mutableListOf<Long>(); val progress=mutableListOf<ImportProgress>()
            val execution=ImportExecution(dao,{
                if(++calls<=3) throw failure
                dao.connectionState("job","COMPLETE",null); false
            },{ progress.add(it) },{ waits.add(it); assertEquals(item,dao.item("item")) })
            assertTrue(execution.run("job"))
            assertEquals(listOf(30_000L,60_000L,120_000L),waits)
            assertEquals(3,progress.count { it.detail.contains("Retrying automatically") })
            assertTrue(progress.all { it.state=="RUNNING" })
            assertEquals(item,dao.item("item")); assertEquals("COMPLETE",dao.job("job")?.state)
        } finally { db.close() }
    }
    @Test fun pauseDuringBackoffStopsWithoutAnotherNetworkAttempt() = runBlocking<Unit> {
        val db=database()
        try {
            val dao=db.imports(); dao.saveJob(ImportJob("job","destination","TAKEOUT",state="RUNNING"))
            var calls=0
            assertTrue(ImportExecution(dao,{ calls++; throw failure },{}, {
                dao.connectionState("job","PAUSED",null)
            }).run("job"))
            assertEquals(1,calls); assertEquals("PAUSED",dao.job("job")?.state)
        } finally { db.close() }
    }
    @Test fun boundedFailuresEndVisiblyAndNonTransportErrorsFailFast() = runBlocking<Unit> {
        val db=database()
        try {
            val dao=db.imports()
            for(transport in listOf(true,false)) {
                dao.saveJob(ImportJob("job","destination","TAKEOUT",state="RUNNING"))
                var calls=0; var waits=0
                val execution=ImportExecution(dao,{ calls++; if(transport) throw failure else error("invalid bytes") },{}, { waits++ })
                if(transport) assertFalse(execution.run("job"))
                else {
                    try { execution.run("job");fail("Unexpected internal failures must propagate") }
                    catch(error: IllegalStateException) { assertEquals("invalid bytes",error.message) }
                }
                assertEquals(if(transport) 4 else 1,calls); assertEquals(if(transport) 3 else 0,waits)
                assertEquals("FAILED",dao.job("job")?.state)
                assertFalse(dao.job("job")?.error.orEmpty().contains("secret"))
            }
        } finally { db.close() }
    }
    @Test fun schedulerCancellationPropagatesWithoutChangingDurableState() = runBlocking<Unit> {
        val db=database()
        try {
            val dao=db.imports(); val job=ImportJob("job","destination","TAKEOUT",state="RUNNING")
            dao.saveJob(job)
            try { ImportExecution(dao,{ throw CancellationException("system stop") },{}).run("job"); fail() }
            catch(_: CancellationException) { }
            assertEquals(job,dao.job("job"))
        } finally { db.close() }
    }
}

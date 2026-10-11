package com.myphotosanywhere.mpa.importing

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.PhotoDatabase
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ImportHistoryTest {
    @Test fun terminalDismissalSurvivesRecreationAndKeepsAllDurableRecords() = runBlocking<Unit> {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        val history=ImportHistory(context)
        try {
            for(state in listOf("COMPLETE","CANCELLED","FAILED")) {
                val job=ImportJob("history-$state","source","TAKEOUT",state=state,error="kept")
                val item=ImportItem("item-$state",job.id,"content://original","clip.mp4","clip.mp4","video/mp4",100,
                    state=state,remoteId="remote",sessionUri="session",offset=42,staged="private-stage",metadata="kept")
                val download=TakeoutDownload(job.id,"encrypted",bytes=50,state="READY")
                db.imports().saveJob(job);db.imports().insertItem(item);db.imports().saveDownload(download)
                assertTrue(history.dismiss(db,job.id))
                assertTrue(job.id in ImportHistory(context).observeDismissed().first())
                assertEquals(job,db.imports().job(job.id));assertEquals(item,db.imports().item(item.id))
                assertEquals(download,db.imports().download(job.id))
                history.restore(job.id)
                assertFalse(job.id in ImportHistory(context).observeDismissed().first())
            }
        } finally { db.close() }
    }
    @Test fun activeOrPausedWorkCannotBeHidden() = runBlocking<Unit> {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        try {
            val history=ImportHistory(context)
            for(state in listOf("RUNNING","DOWNLOADING","WAITING_FOR_APP","PICKING","PREVIEW","PAUSED","UNKNOWN")) {
                val job=ImportJob("active-$state","source","TAKEOUT",state=state)
                db.imports().saveJob(job)
                assertFalse(history.dismiss(db,job.id));assertEquals(job,db.imports().job(job.id))
                assertFalse(job.id in history.observeDismissed().first())
            }
            assertFalse(history.dismiss(db,"missing"))
        } finally { db.close() }
    }
    @Test fun lateTerminalSummaryStaysDismissedAndRestoringAllowsFreshStatus() = runBlocking<Unit> {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        try {
            val history=ImportHistory(context);val id="late-summary"
            db.imports().saveJob(ImportJob(id,"source","TAKEOUT",state="CANCELLED"))
            assertTrue(history.dismiss(db,id))
            var posts=0
            history.withSummary(id,"CANCELLED") { posts++ }
            history.withSummary(id,"COMPLETE") { posts++ }
            assertEquals(0,posts)
            history.withSummary(id,"RUNNING") { posts++ };assertEquals(1,posts)
            history.restore(id)
            history.withSummary(id,"COMPLETE") { posts++ };assertEquals(2,posts)
        } finally { db.close() }
    }
}

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
class ImportErrorDismissalTest {
    @Test fun dismissalPersistsAcrossReopenAndPreservesEveryCheckpointAndOtherJob() = runBlocking<Unit> {
        val context=RuntimeEnvironment.getApplication()
        val name="dismiss-${java.util.UUID.randomUUID()}.db"
        fun open()=Room.databaseBuilder(context,PhotoDatabase::class.java,name).allowMainThreadQueries().build()
        var db=open()
        try {
            val job=ImportJob("old","destination","TAKEOUT",state="FAILED",error="old failure")
            val item=ImportItem("clip",job.id,"content://zip","clip.mp4","clip.mp4","video/mp4",100,
                state="FAILED",error="old file failure",staged="private-stage",remoteId="reserved",sessionUri="session",offset=42,
                sha256="sha",md5="md5",metadata="metadata")
            val complete=item.copy(id="done",state="COMPLETE",error=null,staged=null,offset=100,photoId="photo")
            val download=TakeoutDownload(job.id,"encrypted-link",bytes=100,total=500,etag="revision",state="READY",error="old download failure")
            val other=job.copy(id="other",error="keep")
            db.imports().saveJob(job); db.imports().saveJob(other)
            db.imports().insertItem(item); db.imports().insertItem(complete); db.imports().saveDownload(download)
            assertTrue(ImportErrorDismissal.hasErrors(job,listOf(item),download))
            assertTrue(ImportErrorDismissal(db).dismiss(job.id))
            db.close(); db=open()
            val dao=db.imports()
            assertEquals(job.copy(error=null),dao.job(job.id))
            assertEquals(item.copy(error=null),dao.item(item.id))
            assertEquals(complete,dao.item(complete.id))
            assertEquals(download.copy(error=null),dao.download(job.id))
            assertEquals(other,dao.job(other.id))
            assertFalse(ImportErrorDismissal.hasErrors(requireNotNull(dao.job(job.id)),dao.items(job.id),dao.download(job.id)))
            dao.saveJob(job.copy(error="new failure"))
            assertTrue(ImportErrorDismissal.hasErrors(requireNotNull(dao.job(job.id)),dao.items(job.id),dao.download(job.id)))
            assertEquals("new failure",dao.job(job.id)?.error)
        } finally { db.close(); context.deleteDatabase(name) }
    }

    @Test fun activeOrMissingJobsCannotHaveTheirCurrentErrorsDismissed() = runBlocking<Unit> {
        val db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).allowMainThreadQueries().build()
        try {
            val dao=db.imports(); val dismissal=ImportErrorDismissal(db)
            for(state in listOf("RUNNING","DOWNLOADING","PICKING","UNKNOWN")) {
                val job=ImportJob(state,"destination","TAKEOUT",state=state,error="current failure")
                val item=ImportItem(state,job.id,"uri","file","file","image/jpeg",10,error="current item failure")
                val download=TakeoutDownload(job.id,"encrypted",error="current download failure")
                dao.saveJob(job); dao.insertItem(item); dao.saveDownload(download)
                assertFalse(ImportErrorDismissal.canDismiss(job))
                assertFalse(dismissal.dismiss(job.id))
                assertEquals(job,dao.job(job.id)); assertEquals(item,dao.item(item.id)); assertEquals(download,dao.download(job.id))
            }
            assertFalse(dismissal.dismiss("missing"))
            for(state in listOf("PREVIEW","FAILED","PAUSED","CANCELLED","COMPLETE"))
                assertTrue(ImportErrorDismissal.canDismiss(ImportJob(state,"destination","TAKEOUT",state=state)))
        } finally { db.close() }
    }
}

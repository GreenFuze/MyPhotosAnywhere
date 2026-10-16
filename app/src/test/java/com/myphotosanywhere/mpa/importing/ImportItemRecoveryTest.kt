package com.myphotosanywhere.mpa.importing

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.drive.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.IOException

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ImportItemRecoveryTest {
    private fun database(name: String?=null): PhotoDatabase {
        if(name!=null) {
            val parent=requireNotNull(RuntimeEnvironment.getApplication().getDatabasePath(name).parentFile)
            check(parent.isDirectory || parent.mkdirs()) { "Cannot create owned fixture database directory: $parent" }
        }
        return if(name==null)
        Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).build()
        else Room.databaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java,name).build()
    }
    private val source=DriveSource("original-source","account","import@test","APP_FILES","folder","Original")
    private suspend fun seed(db: PhotoDatabase,state: String="RUNNING"): Pair<ImportJob,ImportItem> {
        val job=ImportJob("job",source.key,"TAKEOUT",state=state,error=if(state=="FAILED") "Saved failure" else null)
        val item=ImportItem("first",job.id,"content://original","a.png","a.png","image/png",100,metadata="retained",
            state="UPLOADING",sha256="a".repeat(64),md5="md5",staged="private-stage",remoteId="reserved",sessionUri="private-session",offset=42)
        db.drive().saveSource(source);db.imports().saveJob(job);db.imports().insertItem(item)
        return job to item
    }
    @Test fun selectedFailuresResumeAfterRestart()=runBlocking<Unit> {
        val name="item-recovery-${java.util.UUID.randomUUID()}.db";var db=database(name)
        try {
            val (job,first)=seed(db);val dao=db.imports()
            val next=first.copy(id="second",path="b.png",state="PENDING",remoteId=null,sessionUri=null,offset=0)
            val skipped=first.copy(id="unselected",path="c.png",state="FAILED",selected=false,error="Keep this receipt")
            dao.insertItem(next);dao.insertItem(skipped)
            val calls=mutableListOf<String>()
            assertFalse(ImportExecution(dao,{
                val item=dao.nextRunnable(job.id)
                if(item==null) { ImportItemRecovery(db).finish(job.id);false }
                else ImportItemRecovery(db).attempt(job.id,item.id) {
                    calls+=item.id
                    if(item.id==first.id) throw IOException("This source is temporarily unavailable")
                    dao.updateItem(item.copy(state="COMPLETE",remoteId="verified-second",offset=item.size,staged=null))
                }
            },{}).run(job.id))
            assertEquals(listOf("first","second"),calls)
            val done=requireNotNull(dao.item(next.id));val failed=requireNotNull(dao.item(first.id))
            assertEquals(first,failed.copy(state=first.state,error=first.error));assertEquals("FAILED",failed.state)
            assertEquals("COMPLETE",done.state);assertTrue(dao.job(job.id)?.error.orEmpty().contains("1 selected file could not"))
            db.close();db=database(name)
            db.drive().saveSource(source.copy(key="new-default",folderId="elsewhere",backupEnabled=true))
            ImportRetryPreparation(db).prepare(job.id)
            assertEquals(failed.copy(state="PENDING",error=null),db.imports().item(first.id))
            assertEquals(done,db.imports().item(next.id));assertEquals(skipped,db.imports().item(skipped.id))
            assertEquals(job.binding,db.imports().job(job.id)?.binding)
            assertEquals(first.id,db.imports().nextRunnable(job.id)?.id)
            assertTrue(ImportItemRecovery(db).attempt(job.id,first.id) {
                val current=requireNotNull(db.imports().item(first.id));db.imports().updateItem(current.copy(state="COMPLETE",offset=current.size))
            })
            assertTrue(ImportItemRecovery(db).finish(job.id));assertEquals(done,db.imports().item(next.id))
            assertEquals(skipped,db.imports().item(skipped.id))
        } finally { db.close();RuntimeEnvironment.getApplication().deleteDatabase(name) }
    }
    @Test fun authorizationAndDestinationFailuresStopTheJobWithoutBlamingAnyImage()=runBlocking<Unit> {
        for(failure in listOf(DriveAuthorizationNeeded(),ImportDestinationUnavailable("Destination denied"),
            PhotosPickerAuthorizationNeeded(),PhotosPickerRequestException(401,true,"Reconnect"),
            PhotosPickerRequestException(410,true,"Selection expired"),DriveHttpException(401))) {
            val db=database()
            try {
                val (job,item)=seed(db);val next=item.copy(id="next",path="b.png",state="PENDING");db.imports().insertItem(next)
                assertFalse(ImportItemRecovery(db).attempt(job.id,item.id) { throw failure })
                assertEquals(item,db.imports().item(item.id));assertEquals(next,db.imports().item(next.id))
                assertEquals("FAILED",db.imports().job(job.id)?.state);assertEquals(0,db.imports().selectedFailureCount(job.id))
                assertEquals(job.binding,db.imports().job(job.id)?.binding)
            } finally { db.close() }
        }
    }
    @Test fun rateAndServerBackoffKeepTheSameReservationAndOtherImagesUntouched()=runBlocking<Unit> {
        for(code in listOf(429,503)) {
            val db=database()
            try {
                val (job,item)=seed(db);var calls=0;val waits=mutableListOf<Long>()
                assertTrue(ImportExecution(db.imports(),{
                    val current=db.imports().nextRunnable(job.id)
                    if(current==null) { ImportItemRecovery(db).finish(job.id);false }
                    else ImportItemRecovery(db).attempt(job.id,current.id) {
                        if(++calls==1) throw DriveHttpException(code)
                        db.imports().updateItem(current.copy(state="COMPLETE",offset=current.size))
                    }
                },{ assertFalse(it.detail.contains("Backup will retry")) }, { waits+=it;assertEquals(item,db.imports().item(item.id));assertEquals("RUNNING",db.imports().job(job.id)?.state) }).run(job.id))
                assertEquals(2,calls);assertEquals(listOf(30_000L),waits)
                assertEquals(item.copy(state="COMPLETE",offset=item.size),db.imports().item(item.id))
            } finally { db.close() }
        }
    }
    @Test fun cancellationAndInternalInvariantFailuresPropagateWithoutChangingTheItem()=runBlocking<Unit> {
        val db=database()
        try {
            val (job,item)=seed(db)
            try { ImportItemRecovery(db).attempt(job.id,item.id) { throw CancellationException("stop") };fail() }
            catch(_: CancellationException) { }
            assertEquals(job,db.imports().job(job.id));assertEquals(item,db.imports().item(item.id))
            try { ImportItemRecovery(db).attempt(job.id,item.id) { error("broken invariant") };fail() }
            catch(error: IllegalStateException) { assertEquals("broken invariant",error.message) }
            assertEquals(job,db.imports().job(job.id));assertEquals(item,db.imports().item(item.id))
        } finally { db.close() }
    }
    @Test fun failureRacingWithPauseDoesNotResumeOrRewriteTheStoppedJob()=runBlocking<Unit> {
        val db=database()
        try {
            val (job,item)=seed(db);val paused=job.copy(state="PAUSED")
            assertFalse(ImportItemRecovery(db).attempt(job.id,item.id) {
                db.imports().saveJob(paused);throw IOException("Unavailable")
            })
            assertEquals(paused,db.imports().job(job.id));assertEquals(item,db.imports().item(item.id))
        } finally { db.close() }
    }
    @Test fun postCommitFollowUpFailureNeverRewindsVerifiedCompletion()=runBlocking<Unit> {
        val db=database()
        try {
            val (job,item)=seed(db);val completed=item.copy(state="COMPLETE",staged=null,offset=item.size,photoId="photo")
            assertFalse(ImportItemRecovery(db).attempt(job.id,item.id) {
                db.imports().updateItem(completed);throw IOException("Follow-up unavailable")
            })
            assertEquals(completed,db.imports().item(item.id));assertEquals("FAILED",db.imports().job(job.id)?.state)
            ImportRetryPreparation(db).prepare(job.id);assertNull(db.imports().nextRunnable(job.id))
            assertTrue(ImportItemRecovery(db).finish(job.id));assertEquals(completed,db.imports().item(item.id))
        } finally { db.close() }
    }
    @Test fun scopedInvalidInputsAndPerFilePermissionFailuresLeaveDurableReceipts()=runBlocking<Unit> {
        val db=database()
        try {
            val (job,item)=seed(db)
            assertTrue(ImportItemRecovery(db).attempt(job.id,item.id) {
                ImportItemInput().read("Takeout metadata") { com.google.gson.JsonParser.parseString("{broken") }
            })
            val failed=requireNotNull(db.imports().item(item.id));assertEquals("FAILED",failed.state)
            assertTrue(failed.error.orEmpty().contains("Takeout metadata"));assertEquals("RUNNING",db.imports().job(job.id)?.state)
            ImportRetryPreparation(db).prepare(job.id)
            assertTrue(ImportItemRecovery(db).attempt(job.id,item.id) { throw SecurityException("Document permission lost") })
            assertEquals("Document permission lost",db.imports().item(item.id)?.error)
            try { ImportItemInput().read("input") { throw android.database.sqlite.SQLiteConstraintException("database invariant") };fail() }
            catch(error: android.database.sqlite.SQLiteConstraintException) { assertEquals("database invariant",error.message) }
        } finally { db.close() }
    }
    @Test fun missingPickerMediaDoesNotBlockTheRestOfItsSelection()=runBlocking<Unit> {
        val db=database()
        try {
            val (job,item)=seed(db)
            assertTrue(ImportItemRecovery(db).attempt(job.id,item.id) {
                ImportItemInput().read("media") { throw PhotosPickerRequestException(404,false,"Media unavailable") }
            })
            assertEquals("RUNNING",db.imports().job(job.id)?.state);assertEquals("FAILED",db.imports().item(item.id)?.state)
        } finally { db.close() }
    }
    @Test fun disconnectedDestinationCannotRetargetOrResetFailedTransfers()=runBlocking<Unit> {
        val db=database()
        try {
            val (job,item)=seed(db,"FAILED");val failed=item.copy(state="FAILED",error="Saved receipt");db.imports().updateItem(failed)
            db.drive().disconnectSource(source.key)
            db.drive().saveSource(source.copy(key="other",folderId="other",backupEnabled=true))
            try { ImportRetryPreparation(db).prepare(job.id);fail() }
            catch(_: IllegalArgumentException) { }
            assertEquals(job,db.imports().job(job.id));assertEquals(failed,db.imports().item(item.id))
        } finally { db.close() }
    }
}

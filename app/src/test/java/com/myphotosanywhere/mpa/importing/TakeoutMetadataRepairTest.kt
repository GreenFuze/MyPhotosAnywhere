package com.myphotosanywhere.mpa.importing

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.library.*
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
class TakeoutMetadataRepairTest {
    private class Fixture: AutoCloseable {
        val db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).build()
        val job=ImportJob("saved","captured-destination","TAKEOUT",state="COMPLETE")
        val repair=TakeoutMetadataRepair(db)
        val raw="""{"title":"photo.jpg","photoTakenTime":{"timestamp":"12"},"geoData":{"latitude":32,"longitude":34},"people":[{"name":"Exported name"}]}"""
        suspend fun item(id: String,metadata: String=raw): Pair<Photo,ImportItem> {
            db.imports().saveJob(job)
            val photo=Photo(id,"photo.jpg","private-original-$id","private-preview-$id","image/jpeg",128,13,"a".repeat(64),
                favorite=true,parentId="parent",sourceBindingKey=job.binding,sourceRemoteId="remote-$id",cachedVersion="1",pinned=true)
            val item=ImportItem("item-$id",job.id,"saved-uri","photo.jpg","photo.jpg","image/jpeg",128,
                metadata=metadata,state="COMPLETE",sha256=photo.sha256,md5="b".repeat(32),remoteId=photo.sourceRemoteId,
                sessionUri="old-upload-session",offset=128,photoId=id)
            db.photos().insert(photo);db.imports().insertItem(item)
            return photo to item
        }
        override fun close()=db.close()
    }
    @Test fun repairIsOfflineIdempotentAndPreservesOriginalIdentityRelationshipsAndCheckpoints()=runBlocking<Unit> {
        Fixture().use { f ->
            val (photo,item)=f.item("photo");val album=Album("album","Reviewed album",1,true)
            val person=Person("reviewed","Reviewed name");val face=FaceRecord("face",photo.id,"crop",null,personId=person.id,corrected=true)
            f.db.library().saveAlbum(album);f.db.library().addToAlbum(AlbumPhoto(album.id,photo.id));f.db.library().savePerson(person);f.db.library().saveFace(face)
            f.db.library().saveDraft(EditDraft(photo.id,"draft",1))
            val result=f.repair.repair(f.job.id)
            assertEquals(MetadataRepairResult(1,0,0,emptyList()),result)
            val updated=requireNotNull(f.db.photos().find(photo.id))
            assertEquals(photo.copy(capturedAt=12000,latitude=32.0,longitude=34.0,importedMetadata=updated.importedMetadata),updated)
            assertTrue(updated.importedMetadata.contains("Exported name"))
            assertEquals(item,f.db.imports().item(item.id));assertEquals(f.job,f.db.imports().job(f.job.id))
            assertEquals(listOf(album),f.db.library().allAlbums());assertEquals(listOf(AlbumPhoto(album.id,photo.id)),f.db.library().members(album.id))
            assertEquals(listOf(person),f.db.library().allPeople());assertEquals(face,f.db.library().face(face.id))
            assertNotNull(f.db.library().draft(photo.id))
            assertEquals(MetadataRepairResult(0,1,0,emptyList()),f.repair.repair(f.job.id))
            assertEquals(updated,f.db.photos().find(photo.id))
        }
    }
    @Test fun reviewedCaptureLocationAndUnknownMetadataWinWithoutChangingImportedLabels()=runBlocking<Unit> {
        Fixture().use { f ->
            val (photo,_)=f.item("reviewed")
            val reviewed=photo.copy(capturedAt=9000,latitude=20.0,longitude=21.0,labels="reviewed",importedMetadata="{\"otherProvider\":{\"keep\":true}}")
            f.db.photos().update(reviewed)
            assertEquals(1,f.repair.repair(f.job.id).applied)
            val updated=requireNotNull(f.db.photos().find(photo.id))
            assertEquals(reviewed.copy(importedMetadata=updated.importedMetadata),updated)
            assertTrue(updated.importedMetadata.contains("otherProvider"));assertTrue(updated.importedMetadata.contains("Exported name"))
        }
    }
    @Test fun mismatchedHashSizeSourceRemoteAndPhotoIdCannotTargetAnotherPhoto()=runBlocking<Unit> {
        for(kind in listOf("hash","size","source","remote","photo")) Fixture().use { f ->
            val (photo,item)=f.item(kind)
            val changed=when(kind) {
                "hash" -> item.copy(sha256="c".repeat(64));"size" -> item.copy(size=129)
                "source" -> { f.db.imports().saveJob(f.job.copy(binding="new-default"));item }
                "remote" -> item.copy(remoteId="another-remote");"photo" -> item.copy(photoId="missing")
                else -> error("Unknown fixture")
            }
            f.db.imports().updateItem(changed)
            assertEquals(kind,1,f.repair.repair(f.job.id).failed)
            assertEquals(photo,f.db.photos().find(photo.id));assertEquals(changed,f.db.imports().item(item.id))
        }
    }
    @Test fun boundedPagesContinuePastInvalidAndConflictingSidecarsWithoutChangingVerifiedState()=runBlocking<Unit> {
        Fixture().use { f ->
            val saved=mutableListOf<Pair<Photo,ImportItem>>()
            repeat(65) { index -> saved+=f.item(index.toString().padStart(3,'0'),if(index==0) "{invalid" else f.raw) }
            val conflict=saved[1].first.copy(importedMetadata="{\"googleTakeout\":{\"title\":\"different.jpg\"}}")
            f.db.photos().update(conflict)
            val result=f.repair.repair(f.job.id)
            assertEquals(63,result.applied);assertEquals(2,result.failed);assertEquals(2,result.failures.size)
            assertEquals(saved[0].first,f.db.photos().find(saved[0].first.id));assertEquals(conflict,f.db.photos().find(conflict.id))
            saved.forEach { assertEquals(it.second,f.db.imports().item(it.second.id)) }
            assertEquals(f.job,f.db.imports().job(f.job.id));assertEquals(63,f.repair.repair(f.job.id).unchanged)
        }
    }
    @Test fun receiptsAreBoundedAndMissingSavedSidecarsNeverInventPeople()=runBlocking<Unit> {
        Fixture().use { f ->
            repeat(25) { f.item("bad-$it","{}") }
            val (photo,item)=f.item("missing")
            f.db.imports().updateItem(item.copy(metadata=null))
            val result=f.repair.repair(f.job.id)
            assertEquals(25,result.failed);assertEquals(20,result.failures.size);assertEquals(0,result.applied)
            assertEquals(photo,f.db.photos().find(photo.id))
        }
    }
    @Test fun activeJobsAndCancellationCannotBecomeSuccessfulEmptyRepair()=runBlocking<Unit> {
        Fixture().use { f ->
            val (photo,_)=f.item("active")
            f.db.imports().saveJob(f.job.copy(state="RUNNING"))
            try { f.repair.repair(f.job.id);fail("Active import must be paused") } catch(_: IOException) { }
            f.db.imports().saveJob(f.job)
            val cancelled=launch(start=CoroutineStart.LAZY) { f.repair.repair(f.job.id);fail("Cancelled repair ran") }
            cancelled.cancelAndJoin()
            assertEquals(photo,f.db.photos().find(photo.id));assertEquals(f.job,f.db.imports().job(f.job.id))
        }
    }
}

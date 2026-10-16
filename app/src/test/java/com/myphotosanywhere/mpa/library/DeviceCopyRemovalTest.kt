package com.myphotosanywhere.mpa.library

import android.app.Application
import android.graphics.Bitmap
import androidx.room.Room
import com.myphotosanywhere.mpa.core.LibrarySettings
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.importing.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File
import java.io.IOException

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class DeviceCopyRemovalTest {
    private class Fixture: AutoCloseable {
        val context=RuntimeEnvironment.getApplication()
        val database=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        val settings=LibrarySettings(context)
        private val previous=settings.state.value
        val id="removal-${java.util.UUID.randomUUID()}"
        val original=File(context.filesDir,"originals/$id")
        val preview=File(context.filesDir,"thumbnails/$id.png")
        val source=DriveSource("$id-source","account","removal@test","APP_FILES","folder","Removal fixture")
        lateinit var photo: Photo
        lateinit var row: CloudPhoto
        lateinit var identity: DeviceCopyIdentity
        suspend fun create() {
            settings.update(previous.copy(faceIndexing=false,objectIndexing=false))
            check(original.parentFile?.mkdirs()==true || requireNotNull(original.parentFile).isDirectory)
            check(preview.parentFile?.mkdirs()==true || requireNotNull(preview.parentFile).isDirectory)
            val bitmap=Bitmap.createBitmap(120,160,Bitmap.Config.ARGB_8888)
            try { bitmap.eraseColor(android.graphics.Color.BLUE);original.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { bitmap.recycle() }
            original.copyTo(preview)
            val sha=DriveBackupWorker.digest(original,"SHA-256")
            photo=Photo(id,"photo.png",original.path,preview.path,"image/png",original.length(),123,sha,capturedAt=122,
                sourceBindingKey=source.key,sourceRemoteId="remote",cachedVersion="1",parentId="edited-from-original",importedMetadata="{\"kept\":true}")
            row=CloudPhoto("${source.key}:remote",source.key,"remote","folder",photo.displayName,photo.mimeType,photo.bytes,
                DriveBackupWorker.digest(original,"MD5"),"1",null,photo.capturedAt,null,null,photoId=id,sha256=sha)
            identity=requireNotNull(DeviceCopyIdentity.from(photo))
            database.drive().saveSource(source);database.photos().insert(photo);database.cloud().savePhoto(row)
        }
        fun cache(verify: suspend (Photo,File)->Boolean)=MediaCache(context,database,settings,verify)
        override fun close() { database.close();settings.update(previous);original.delete();preview.delete() }
    }
    private suspend fun rejected(action: suspend ()->Unit): IOException {
        try { action();fail("The protected device original must not be removed") }
        catch(error: IOException) { return error }
        error("Unreachable assertion")
    }
    @Test fun verifiedRemovalKeepsPreviewIdentityMetadataMembershipAndPeople()=runBlocking<Unit> {
        val fixture=Fixture();fixture.create()
        try {
            val db=fixture.database;val album=Album("album","Album",1);val person=Person("person","Person")
            val membership=AlbumPhoto(album.id,fixture.id);val face=FaceRecord("face",fixture.id,"/crop",null,personId=person.id,corrected=true)
            db.library().saveAlbum(album);db.library().addToAlbum(membership);db.library().savePerson(person);db.library().saveFace(face)
            val previewBytes=fixture.preview.readBytes()
            fixture.cache { _,_ -> true }.removeDownloadedCopy(fixture.identity)
            assertFalse(fixture.original.exists());assertArrayEquals(previewBytes,fixture.preview.readBytes())
            assertEquals(fixture.photo.copy(originalPath=""),db.photos().find(fixture.id))
            assertEquals(fixture.row,db.cloud().photo(fixture.row.key));assertEquals(listOf(membership),db.library().members(album.id))
            assertEquals(face,db.library().face(face.id));assertEquals(listOf(person),db.library().allPeople())
        } finally { fixture.close() }
    }
    @Test fun staleConfirmationAndDisconnectedSourceAreRejectedBeforeRemoteWork()=runBlocking<Unit> {
        val fixture=Fixture();fixture.create();var calls=0
        try {
            val cache=fixture.cache { _,_ -> calls++;true }
            rejected { cache.removeDownloadedCopy(fixture.identity.copy(remote="another-file")) }
            fixture.database.drive().disconnectSource(fixture.source.key)
            rejected { cache.removeDownloadedCopy(fixture.identity) }
            assertEquals(0,calls);assertTrue(fixture.original.isFile);assertEquals(fixture.photo,fixture.database.photos().find(fixture.id))
            assertNull(DeviceCopyIdentity.from(fixture.photo.copy(sourceBindingKey=null,sourceRemoteId=null,cachedVersion=null)))
        } finally { fixture.close() }
    }
    @Test fun pinsDraftsAndUnfinishedTransfersPreventRemoval()=runBlocking<Unit> {
        for(kind in listOf("pin","album","draft","import","backup","download")) {
            val fixture=Fixture();fixture.create();var calls=0
            try {
                val db=fixture.database
                when(kind) {
                    "pin" -> db.photos().update(fixture.photo.copy(pinned=true))
                    "album" -> { db.library().saveAlbum(Album("album","Offline",1,true));db.library().addToAlbum(AlbumPhoto("album",fixture.id)) }
                    "draft" -> db.library().saveDraft(EditDraft(fixture.id,"draft",1))
                    "import" -> { db.imports().saveJob(ImportJob("job",fixture.source.key,"TAKEOUT",state="FAILED"));db.imports().insertItem(ImportItem("item","job","uri","path","photo.png","image/png",fixture.photo.bytes,state="FAILED",photoId=fixture.id)) }
                    "backup" -> db.drive().insertBackup(DriveBackup("backup",fixture.source.key,fixture.id,"account","removal@test","folder","APP_FILES"))
                    "download" -> db.cloud().saveDownload(DownloadTask(fixture.row.key,fixture.source.key,"remote"))
                    else -> error("Unknown fixture")
                }
                rejected { fixture.cache { _,_ -> calls++;true }.removeDownloadedCopy(fixture.identity) }
                assertEquals(kind,0,calls);assertTrue(kind,fixture.original.isFile)
                assertEquals(kind,fixture.photo.originalPath,db.photos().find(fixture.id)?.originalPath)
            } finally { fixture.close() }
        }
    }
    @Test fun failedVerificationAndCancellationKeepOriginalAndReleaseOwnedLease()=runBlocking<Unit> {
        val fixture=Fixture();fixture.create()
        try {
            rejected { fixture.cache { _,_ -> false }.removeDownloadedCopy(fixture.identity) }
            rejected { fixture.cache { _,_ -> throw DriveAuthorizationNeeded() }.removeDownloadedCopy(fixture.identity) }
            val entered=CompletableDeferred<Unit>();var allow=false
            val cache=fixture.cache { _,_ -> if(allow) true else { entered.complete(Unit);awaitCancellation() } }
            val job=launch { cache.removeDownloadedCopy(fixture.identity) }
            entered.await();job.cancelAndJoin()
            assertTrue(fixture.original.isFile);assertEquals(fixture.photo,fixture.database.photos().find(fixture.id))
            allow=true;cache.removeDownloadedCopy(fixture.identity)
            assertFalse("Cancelled verification must not leak a reader lease",fixture.original.exists())
        } finally { fixture.close() }
    }
    @Test fun sourceOrReaderChangesDuringVerificationCannotInvalidateTheSafetyDecision()=runBlocking<Unit> {
        for(kind in listOf("source","reader")) {
            val fixture=Fixture();fixture.create();var reader: AutoCloseable?=null
            try {
                lateinit var cache: MediaCache
                cache=fixture.cache { _,_ ->
                    if(kind=="source") fixture.database.drive().disconnectSource(fixture.source.key)
                    else reader=cache.acquireReader(fixture.id)
                    true
                }
                rejected { cache.removeDownloadedCopy(fixture.identity) }
                assertTrue(fixture.original.isFile);assertEquals(fixture.photo,fixture.database.photos().find(fixture.id))
            } finally { reader?.close();fixture.close() }
        }
    }
    @Test fun undecodableMediaWithoutPreviewKeepsVerifiedOriginalAndAllMetadata()=runBlocking<Unit> {
        val fixture=Fixture();fixture.create();var verified=0
        try {
            fixture.original.writeText("Unsupported original image bytes")
            val photo=fixture.photo.copy(bytes=fixture.original.length(),sha256=DriveBackupWorker.digest(fixture.original,"SHA-256"))
            val row=fixture.row.copy(bytes=photo.bytes,sha256=photo.sha256,md5=DriveBackupWorker.digest(fixture.original,"MD5"))
            fixture.database.photos().update(photo);fixture.database.cloud().savePhoto(row);check(fixture.preview.delete())
            rejected { fixture.cache { _,_ -> verified++;true }.removeDownloadedCopy(requireNotNull(DeviceCopyIdentity.from(photo))) }
            assertEquals(1,verified);assertTrue(fixture.original.isFile)
            assertEquals(photo.sha256,DriveBackupWorker.digest(fixture.original,"SHA-256"))
            assertEquals(photo,fixture.database.photos().find(photo.id));assertEquals(row,fixture.database.cloud().photo(row.key))
        } finally { fixture.close() }
    }
    @Test fun unrelatedMetadataUpdatedDuringVerificationIsPreservedAndAliasedOriginalsStayProtected()=runBlocking<Unit> {
        val fixture=Fixture();fixture.create()
        try {
            val alias=fixture.photo.copy(id="alias",sourceBindingKey=null,sourceRemoteId=null,cachedVersion=null)
            fixture.database.photos().insert(alias)
            rejected { fixture.cache { _,_ -> true }.removeDownloadedCopy(fixture.identity) }
            assertEquals(alias,fixture.database.photos().find(alias.id));assertTrue(fixture.original.isFile)
            fixture.database.photos().update(alias.copy(originalPath=""))
            fixture.cache { _,_ -> fixture.database.photos().update(fixture.photo.copy(favorite=true));true }.removeDownloadedCopy(fixture.identity)
            assertEquals(fixture.photo.copy(originalPath="",favorite=true),fixture.database.photos().find(fixture.id))
            assertFalse(fixture.original.exists());assertTrue(fixture.preview.isFile)
        } finally { fixture.close() }
    }
}

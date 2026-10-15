package com.myphotosanywhere.mpa.trash

import androidx.room.Room
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.selection.SelectionTarget
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File
import java.io.IOException

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=MpaApplication::class)
class DriveTrashRepositoryTest {
    private class Fixture: AutoCloseable {
        val app=RuntimeEnvironment.getApplication() as MpaApplication
        val db=Room.inMemoryDatabaseBuilder(app,PhotoDatabase::class.java).allowMainThreadQueries().build()
        val server=MockWebServer()
        val source=DriveSource.from(DriveConnection(accountId="account",email="fixture@example.test",mode="EXISTING_LIBRARY",folderId="root",folderName="Photos"))
        val file=File.createTempFile("trash-original-",".jpg",app.cacheDir).apply { writeText("original") }
        val sha="a".repeat(64)
        val photo=Photo("photo","p.jpg",file.absolutePath,"/thumb","image/jpeg",8,1,sha,capturedAt=1,
            favorite=true,sourceBindingKey=source.key,sourceRemoteId="media",cachedVersion="1",pinned=true,importedMetadata="synthetic metadata")
        val cloud=CloudPhoto("${source.key}:media",source.key,"media","root","p.jpg","image/jpeg",8,"md5","1",null,1,null,null,photoId=photo.id,sha256=sha)
        var trashed=false;var version="1";var remoteSha=sha;var canTrash=true;var canUntrash=true
        var user="account";var patchStatus=200;var commitThenFail=false;var patches=0;var rootTrashed=false;var missingRevision=false
        val requests=mutableListOf<String>()
        val repository: DriveTrashRepository
        init {
            server.dispatcher=object: Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    synchronized(requests) { requests.add("${request.method} ${request.path}") }
                    if(request.path?.startsWith("/drive/v3/about")==true)
                        return MockResponse().setBody("""{"user":{"permissionId":"$user","emailAddress":"fixture@example.test"}}""")
                    if(request.path?.startsWith("/drive/v3/files/root")==true)
                        return MockResponse().setBody("""{"id":"root","name":"Photos","mimeType":"application/vnd.google-apps.folder","trashed":$rootTrashed,"version":"1"}""")
                    if(request.path?.startsWith("/drive/v3/files/media")==true) {
                        if(request.method=="PATCH") {
                            patches++
                            if(request.getHeader("If-Match")!="etag-$version") return MockResponse().setResponseCode(412)
                            if(patchStatus!=200) return MockResponse().setResponseCode(patchStatus)
                            trashed=request.body.readUtf8()=="{\"trashed\":true}"
                            version=(version.toInt()+1).toString()
                            if(commitThenFail) return MockResponse().setResponseCode(500)
                            return MockResponse().setBody("{}")
                        }
                        return MockResponse().apply { if(!missingRevision) setHeader("ETag","etag-$version") }.setBody("""{"id":"media","name":"p.jpg","mimeType":"image/jpeg","size":"8","md5Checksum":"md5","sha256Checksum":"$remoteSha","version":"$version","parents":["root"],"trashed":$trashed,"capabilities":{"canTrash":$canTrash,"canUntrash":$canUntrash}}""")
                    }
                    return MockResponse().setResponseCode(404)
                }
            }
            server.start()
            repository=DriveTrashRepository(app,db) { DriveGateway("fixture",OkHttpClient(),server.url("/drive/v3/"),server.url("/upload")) }
        }
        suspend fun seed() {
            db.drive().saveSource(source);db.photos().insert(photo);db.cloud().savePhoto(cloud)
            db.library().saveAlbum(Album("album","Album",1));db.library().addToAlbum(AlbumPhoto("album",photo.id))
            db.library().savePerson(Person("person","Synthetic person"))
            db.library().saveFace(FaceRecord("face",photo.id,"crop",null,"person",corrected=true))
            db.library().saveDraft(EditDraft(photo.id,"draft",1))
        }
        suspend fun prepare()=repository.prepare(SelectionTarget.from(photo,cloud))
        suspend fun queue()=repository.queue(listOf(prepare()))
        override fun close() { server.shutdown();db.close();file.delete() }
    }

    @Test fun trashAndRestorePreserveOriginalMetadataAndRelationshipsAndHideEveryNormalQuery()=runBlocking<Unit> {
        Fixture().use { f ->
            f.seed();f.queue()
            val timeline=TimelineRepository(f.db)
            assertEquals(1,timeline.page(TimelineQuery(),null,TimelineDirection.AFTER,10).size)
            assertFalse(requireNotNull(f.db.trash().find(f.cloud.key)).confirmed)
            f.repository.process(f.cloud.key)
            assertTrue(f.trashed);assertTrue(f.db.trash().hidesPhoto(f.photo.id))
            for(filter in listOf("All","Favorites","Offline","Archive","Videos","Edits"))
                assertTrue(timeline.page(TimelineQuery(filter),null,TimelineDirection.AFTER,10).isEmpty())
            assertTrue(timeline.page(TimelineQuery(album="album"),null,TimelineDirection.AFTER,10).isEmpty())
            assertTrue(timeline.page(TimelineQuery(person="person"),null,TimelineDirection.AFTER,10).isEmpty())
            assertTrue(f.db.photos().observeAll().first().isEmpty())
            val record=requireNotNull(f.db.trash().find(f.cloud.key))
            f.repository.queue(listOf(TrashCandidate(record,f.source.email,f.source.folderName)),restore=true)
            f.repository.process(record.key)
            assertFalse(f.trashed);assertFalse(f.db.trash().hidesPhoto(f.photo.id));assertEquals(2,f.patches)
            val restored=requireNotNull(f.db.photos().find(f.photo.id))
            assertEquals(f.photo.copy(cachedVersion="3"),restored)
            assertEquals("original",f.file.readText())
            assertEquals(listOf(AlbumPhoto("album",f.photo.id)),f.db.library().members("album"))
            assertEquals("person",f.db.library().face("face")?.personId);assertTrue(requireNotNull(f.db.library().face("face")).corrected)
            assertEquals("draft",f.db.library().draft(f.photo.id)?.recipe)
            assertEquals(f.photo.id,timeline.page(TimelineQuery(),null,TimelineDirection.AFTER,10).single().photoId)
        }
    }

    @Test fun lostMutationReplyIsReconciledWithoutPatchingTwice()=runBlocking<Unit> {
        Fixture().use { f ->
            f.seed();f.queue();f.commitThenFail=true
            try { f.repository.process(f.cloud.key);fail() } catch(error: DriveHttpException) { assertEquals(500,error.status);f.repository.failure(f.cloud.key,error,true) }
            assertTrue(f.trashed);assertNotNull(f.db.trash().find(f.cloud.key)?.desired)
            f.repository.process(f.cloud.key)
            assertEquals(1,f.patches);assertTrue(requireNotNull(f.db.trash().find(f.cloud.key)).confirmed)
            assertNull(f.db.trash().find(f.cloud.key)?.desired)
        }
    }

    @Test fun changedOriginalOrRevisionAndDeniedCapabilityNeverMutate()=runBlocking<Unit> {
        for(kind in listOf("content","revision","permission","folder")) Fixture().use { f ->
            f.seed();f.queue()
            when(kind) { "content" -> f.remoteSha="b".repeat(64);"revision" -> f.version="2";"permission" -> f.canTrash=false;"folder" -> f.rootTrashed=true }
            try { f.repository.process(f.cloud.key);fail(kind) } catch(_: TrashUnavailable) { }
            assertEquals(0,f.patches);assertFalse(f.trashed);assertEquals("original",f.file.readText())
        }
    }

    @Test fun accountMismatchNeverCrossesMutationBoundary()=runBlocking<Unit> {
        Fixture().use { f -> f.seed();f.queue();f.user="wrong-account"
            try { f.repository.process(f.cloud.key);fail() } catch(_: DriveAuthorizationNeeded) { }
            assertEquals(0,f.patches);assertEquals(1,f.requests.size)
        }
    }

    @Test fun conditionalConflictAndCancellationPreserveOriginalAndIntentTruth()=runBlocking<Unit> {
        Fixture().use { f -> f.seed();f.queue();f.patchStatus=412
            try { f.repository.process(f.cloud.key);fail() } catch(error: DriveHttpException) { assertEquals(412,error.status);f.repository.failure(f.cloud.key,error,false) }
            assertFalse(f.trashed);assertFalse(requireNotNull(f.db.trash().find(f.cloud.key)).confirmed)
            assertNull(f.db.trash().find(f.cloud.key)?.desired);assertNotNull(f.db.trash().find(f.cloud.key)?.error)
            f.repository.queue(listOf(f.prepare()));f.repository.cancel(f.cloud.key)
            f.repository.process(f.cloud.key);assertEquals(1,f.patches);assertFalse(f.trashed)
        }
    }

    @Test fun localOnlyOriginalCannotEnterTrashAndUnlinkedRemoteRequiresNoDownload()=runBlocking<Unit> {
        Fixture().use { f -> f.seed()
            val local=f.photo.copy(id="local",sourceBindingKey=null,sourceRemoteId=null)
            f.db.photos().insert(local)
            try { f.repository.prepare(SelectionTarget.from(local));fail() } catch(_: TrashUnavailable) { }
            f.db.cloud().savePhoto(f.cloud.copy(photoId=null))
            val candidate=f.repository.prepare(SelectionTarget.from(f.cloud.copy(photoId=null)))
            f.repository.queue(listOf(candidate));f.repository.process(candidate.record.key)
            assertTrue(f.trashed);assertFalse(f.db.trash().hidesPhoto(local.id))
            assertTrue(f.requests.none { "alt=media" in it });assertEquals("original",f.file.readText())
        }
    }

    @Test fun trashingOneDistinctDriveCopyRetainsOtherLiveRepresentation()=runBlocking<Unit> {
        Fixture().use { f -> f.seed()
            val other=DriveSource.from(DriveConnection(accountId="other",email="other@example.test",mode="EXISTING_LIBRARY",folderId="other-root",folderName="Other"))
            f.db.drive().saveSource(other)
            val copy=f.cloud.copy(key="${other.key}:other-media",bindingKey=other.key,remoteId="other-media")
            f.db.cloud().savePhoto(copy)
            f.queue();f.repository.process(f.cloud.key)
            assertFalse(f.db.trash().hidesPhoto(f.photo.id));assertFalse(requireNotNull(f.db.cloud().photo(copy.key)).removed)
            assertEquals(1,TimelineRepository(f.db).page(TimelineQuery(),null,TimelineDirection.AFTER,10).size)
        }
    }

    @Test fun externalTrashAndRestoreAreIndexedWithoutGuessingRemovedMeansTrashed()=runBlocking<Unit> {
        Fixture().use { f -> f.seed()
            val index=DriveTrashIndex(f.db)
            index.verified(f.source.key,DriveFile("media","p.jpg","image/jpeg",bytes=8,md5="md5",sha256=f.sha,trashed=true,version="2"))
            assertTrue(f.db.trash().hidesPhoto(f.photo.id))
            index.verified(f.source.key,DriveFile("media","p.jpg","image/jpeg",bytes=8,md5="md5",sha256=f.sha,trashed=false,version="3"))
            assertFalse(f.db.trash().hidesPhoto(f.photo.id));assertEquals(0,f.patches)
        }
    }

    @Test fun staleSyncPageCannotUndoVerifiedTrashOrRestore()=runBlocking<Unit> {
        Fixture().use { f -> f.seed();f.queue();f.repository.process(f.cloud.key)
            val index=DriveTrashIndex(f.db)
            assertFalse(index.accepts(f.source.key,DriveFile("media","p.jpg","image/jpeg",version="1")))
            assertTrue(index.accepts(f.source.key,DriveFile("media","p.jpg","image/jpeg",version="3")))
            val record=requireNotNull(f.db.trash().find(f.cloud.key))
            f.repository.queue(listOf(TrashCandidate(record,f.source.email,f.source.folderName)),true);f.repository.process(record.key)
            assertFalse(index.accepts(f.source.key,DriveFile("media","p.jpg","image/jpeg",version="2",trashed=true)))
            assertFalse(f.db.trash().hidesPhoto(f.photo.id))
        }
    }

    @Test fun unfinishedImportKeepsCheckpointAndStopsTrashUntilItemCompletes()=runBlocking<Unit> {
        Fixture().use { f -> f.seed();f.queue()
            val job=com.myphotosanywhere.mpa.importing.ImportJob("job",f.source.key,"TAKEOUT",state="RUNNING")
            val item=com.myphotosanywhere.mpa.importing.ImportItem("item",job.id,"content://fixture","p.jpg","p.jpg","image/jpeg",8,
                state="UPLOADING",sha256=f.sha,md5="md5",remoteId="media",sessionUri="session",offset=4)
            f.db.imports().saveJob(job);f.db.imports().insertItem(item)
            try { f.repository.process(f.cloud.key);fail() } catch(_: TrashUnavailable) { }
            assertEquals(0,f.patches);assertEquals(item,f.db.imports().item(item.id));assertEquals(job,f.db.imports().job(job.id))
            f.db.imports().updateItem(item.copy(state="COMPLETE",offset=8))
            f.repository.process(f.cloud.key);assertEquals(1,f.patches)
        }
    }

    @Test fun externalTrashOfReplacedBytesDoesNotHideUnrelatedOldLocalIdentity()=runBlocking<Unit> {
        Fixture().use { f -> f.seed()
            DriveTrashIndex(f.db).verified(f.source.key,DriveFile("media","replacement.jpg","image/jpeg",bytes=9,md5="different",sha256="b".repeat(64),trashed=true,version="2"))
            val record=requireNotNull(f.db.trash().find(f.cloud.key))
            assertNull(record.photoId);assertEquals("",record.thumbnailPath);assertFalse(f.db.trash().hidesPhoto(f.photo.id))
            assertEquals("original",f.file.readText())
        }
    }
    @Test fun restoreConfirmationCannotRetargetReplacementContent()=runBlocking<Unit> {
        Fixture().use { f -> f.seed();f.queue();f.repository.process(f.cloud.key)
            val captured=requireNotNull(f.db.trash().find(f.cloud.key))
            val updated=captured.copy(bytes=9,sha256="b".repeat(64),expectedVersion="3",observedVersion="3",photoId=null)
            f.db.trash().save(updated)
            try { f.repository.queue(listOf(TrashCandidate(captured,f.source.email,f.source.folderName)),true);fail() } catch(_: TrashUnavailable) { }
            assertEquals(updated,f.db.trash().find(captured.key));assertEquals(1,f.patches)
        }
    }

    @Test fun samePhysicalFileAliasesAreTrashedTogetherWithoutDuplicatingMutations()=runBlocking<Unit> {
        Fixture().use { f -> f.seed()
            val nested=DriveSource.from(DriveConnection(accountId="account",email=f.source.email,mode="EXISTING_LIBRARY",folderId="nested",folderName="Nested"))
            f.db.drive().saveSource(nested)
            val alias=f.cloud.copy(key="${nested.key}:media",bindingKey=nested.key,parentId="nested")
            f.db.cloud().savePhoto(alias)
            f.queue();f.repository.process(f.cloud.key)
            assertEquals(1,f.patches);assertTrue(requireNotNull(f.db.cloud().photo(alias.key)).removed)
            assertTrue(f.db.trash().hidesPhoto(f.photo.id));assertEquals("original",f.file.readText())
        }
    }

    @Test fun missingConditionalRevisionIsAnActionableFailureWithoutMutationOrEndlessRetry()=runBlocking<Unit> {
        Fixture().use { f -> f.seed();f.queue();f.missingRevision=true
            try { f.repository.process(f.cloud.key);fail() } catch(error: DriveRevisionUnavailable) {
                assertFalse(DriveTrashFailures.retryable(error));f.repository.failure(f.cloud.key,error,false)
            }
            assertEquals(0,f.patches);assertFalse(f.trashed);assertEquals("original",f.file.readText())
            assertNull(f.db.trash().find(f.cloud.key)?.desired);assertNotNull(f.db.trash().find(f.cloud.key)?.error)
        }
    }

}

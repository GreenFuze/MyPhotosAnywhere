package com.myphotosanywhere.mpa.timeline

import android.app.Application
import androidx.room.Room
import androidx.paging.PagingSource
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.Assert.*
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class TimelineRepositoryTest {
    private lateinit var db: PhotoDatabase
    private lateinit var repository: TimelineRepository
    private lateinit var directory: File
    private lateinit var temporaryRoot: File
    @Before fun setup()=runBlocking {
        db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).build()
        repository=TimelineRepository(db) { it.startsWith("/present/") }
        // Robolectric embeds long test names in its app-data path, exceeding
        // Windows native SQLite's path limit. Keep the disposable fixture short.
        temporaryRoot=java.nio.file.Files.createTempDirectory("mpa-timeline-").toFile()
        directory=File(temporaryRoot,"sessions")
        source("a");source("b");source("disabled",false)
    }
    @After fun cleanup() { db.close();temporaryRoot.deleteRecursively() }
    private suspend fun source(id: String,enabled: Boolean=true) {
        db.drive().saveSource(DriveSource(id,id,"$id@test","APP_FILES",id,id,enabled=enabled))
    }
    private suspend fun photo(id: String,time: Long?=1000,imported: Long=1,archived: Boolean=false,
        favorite: Boolean=false,source: String?=null,remote: String?=null,path: String="/present/$id",parent: String?=null): Photo {
        val value=Photo(id,"$id.jpg",path,"thumb-$id","image/jpeg",10,imported,"sha-$id",capturedAt=time,
            archived=archived,favorite=favorite,sourceBindingKey=source,sourceRemoteId=remote,parentId=parent)
        db.photos().insert(value);return value
    }
    private suspend fun cloud(id: String,time: Long?=2000,source: String="a",photo: String?=null,
        removed: Boolean=false,modified: String?=null): CloudPhoto {
        val value=CloudPhoto("$source:$id",source,id,source,"$id.jpg","image/jpeg",10,"md5-$id","v1",modified,time,
            null,null,photoId=photo,removed=removed)
        db.cloud().savePhoto(value);return value
    }
    private suspend fun rows(query: TimelineQuery=TimelineQuery())=repository.page(query,null,TimelineDirection.AFTER,300)

    @Test fun dateBoundsRespectSourcesVisibilityAndUnknownDates()=runBlocking<Unit> {
        photo("old",1000);photo("new",3000);photo("hidden",9999,archived=true)
        cloud("remote",4000,source="a");cloud("unknown",null,source="a");cloud("disconnected",10000,source="disabled")
        assertEquals(TimelineDateRange(1000,4000),repository.dateRange(TimelineQuery()))
        assertEquals(TimelineDateRange(4000,4000),repository.dateRange(TimelineQuery(source="a")))
        assertEquals(TimelineDateRange(9999,9999),repository.dateRange(TimelineQuery("Archive")))
        assertNull(repository.dateRange(TimelineQuery(source="b")))
        assertEquals(4000L,rows().first().millis)
    }

    @Test fun dateBoundsUseRealOriginalsAndMetadataPredicates()=runBlocking<Unit> {
        val kept=photo("labelled",2000,path="/present/labelled")
        photo("missing",5000,path="/missing/file");photo("older",1000,path="/present/older")
        assertEquals(TimelineDateRange(1000,2000),repository.dateRange(TimelineQuery("Offline")))
        assertEquals(TimelineDateRange(2000,2000),repository.dateRange(TimelineQuery(search="labelled")))
        assertNull(repository.dateRange(TimelineQuery(search="no match")))
        assertEquals(kept,db.photos().find(kept.id))
    }

    @Test fun mixedSourcesShareChronologyAndOnlyExplicitLinksCollapse()=runBlocking {
        photo("local",1000);photo("linked",null,source="a",remote="linked")
        cloud("linked",2000,photo="linked");cloud("new",3000);cloud("distinct",500,source="a")
        cloud("distinct",500,source="b");cloud("hidden",9000,source="disabled");cloud("unknown",null)
        assertEquals(listOf("cloud:a:new","photo:linked","photo:local","cloud:a:distinct","cloud:b:distinct","cloud:a:unknown"),rows().map { it.key })
        assertEquals(TimestampProvenance.LINKED_CAPTURE,rows()[1].provenance)
        assertEquals(listOf("photo:linked","cloud:a:new","cloud:a:distinct","cloud:a:unknown").toSet(),rows(TimelineQuery(source="a")).map { it.key }.toSet())
    }

    @Test fun localCaptureThenExactLinkedCaptureThenKeyThenImportHaveStableProvenance()=runBlocking {
        photo("capture",9000,source="a",remote="exact");cloud("exact",1,photo="capture")
        photo("linked",null,source="b",remote="exact")
        cloud("a-first",3000,photo="linked");cloud("exact",4000,source="b",photo="linked")
        photo("key-linked",null);cloud("z",6000,photo="key-linked");cloud("a",5000,photo="key-linked")
        photo("import",null,imported=2000)
        val entries=rows().associateBy { it.photoId }
        assertEquals(9000L,entries.getValue("capture").millis)
        assertEquals(4000L,entries.getValue("linked").millis)
        assertEquals(5000L,entries.getValue("key-linked").millis)
        assertEquals(TimestampProvenance.IMPORT,entries.getValue("import").provenance)
        db.drive().disconnectSource("b")
        assertEquals(4000L,rows().first { it.photoId=="linked" }.millis)
    }

    @Test fun archiveTombstonesAndOrganizationalFiltersNeverResurrectLinkedCloudCopies()=runBlocking {
        photo("archived",4000,archived=true);cloud("archive-copy",4000,photo="archived")
        photo("removed",3000,favorite=true,source="a",remote="removed");cloud("removed",3000,photo="removed",removed=true)
        photo("edited",2000,parent="original");photo("plain",1000)
        db.library().saveAlbum(Album("album","Album",1));db.library().addToAlbum(AlbumPhoto("album","removed"))
        db.library().savePerson(Person("person","Person"));db.library().saveFace(FaceRecord("face","removed","",null,personId="person"))
        assertEquals(listOf("photo:edited","photo:plain"),rows().map { it.key })
        assertEquals(listOf("photo:archived"),rows(TimelineQuery("Archive")).map { it.key })
        assertEquals(listOf("photo:removed"),rows(TimelineQuery("Favorites")).map { it.key })
        assertEquals(listOf("photo:edited"),rows(TimelineQuery("Edits")).map { it.key })
        assertEquals(listOf("photo:removed"),rows(TimelineQuery(album="album")).map { it.key })
        assertEquals(listOf("photo:removed"),rows(TimelineQuery(person="person")).map { it.key })
    }

    @Test fun driveModificationRequiresValidTimestampAndUnknownDatesSortLast()=runBlocking {
        cloud("valid",null,modified="2026-01-02T03:04:05.123Z")
        cloud("invalid-calendar",null,modified="2026-02-30T03:04:05Z")
        cloud("invalid",null,modified="1")
        cloud("offset",null,modified="2026-01-02T04:04:05.123+01:00")
        val entries=rows()
        assertEquals(listOf("cloud:a:offset","cloud:a:valid","cloud:a:invalid","cloud:a:invalid-calendar"),entries.map { it.key })
        assertEquals(java.time.Instant.parse("2026-01-02T03:04:05.123Z").toEpochMilli(),entries[0].millis)
        assertEquals(TimestampProvenance.DRIVE_MODIFIED,entries[0].provenance)
        assertTrue(entries.takeLast(2).all { it.millis==null && it.provenance==TimestampProvenance.UNKNOWN })
    }

    @Test fun bidirectionalKeysetsCrossEqualAndUnknownTimestampsWithoutDuplicates()=runBlocking {
        repeat(103) { cloud("item-${it.toString().padStart(3,'0')}",if(it<98) (it/3).toLong() else null) }
        val expected=rows()
        val forward=mutableListOf<TimelineEntry>();var cursor: TimelineCursor?=null
        while(true) {
            val page=repository.page(TimelineQuery(),cursor,TimelineDirection.AFTER,17)
            if(page.isEmpty()) break
            forward.addAll(page);cursor=page.last().cursor
        }
        assertEquals(expected,forward);assertEquals(103,forward.map { it.key }.toSet().size)
        val backward=mutableListOf<TimelineEntry>();cursor=TimelineCursor(null,"cloud:zzzz")
        while(true) {
            val page=repository.page(TimelineQuery(),cursor,TimelineDirection.BEFORE,19)
            if(page.isEmpty()) break
            backward.addAll(0,page);cursor=page.first().cursor
        }
        assertEquals(expected,backward)
        assertEquals(expected.takeLast(90),repository.around(TimelineQuery(),expected.last().cursor,90))
    }

    @Test fun missingOriginalsDoNotTerminateOfflinePagingAndThumbnailsDoNotQualify()=runBlocking {
        repeat(125) { photo("missing-$it",10000L+it,path="/missing/$it") }
        repeat(65) { photo("present-$it",it.toLong()) }
        cloud("remote",999999)
        val entries=rows(TimelineQuery("Offline"))
        assertEquals(65,entries.size);assertTrue(entries.all { it.photoId!!.startsWith("present-") })
        val seek=repository.seek(TimelineQuery("Offline"),Long.MAX_VALUE)
        assertEquals(entries.first().cursor,seek)
    }

    @Test fun dateSeekUsesExclusiveBoundaryAndSourceVisibility()=runBlocking {
        photo("before",999);photo("at",1000);cloud("later",1001);cloud("unknown",null)
        assertEquals("photo:before",repository.seek(TimelineQuery(),1000)?.key)
        assertEquals("photo:at",repository.seek(TimelineQuery(),1001)?.key)
        assertNull(repository.seek(TimelineQuery(),999))
    }

    @Test fun pagingRefreshRetainsAnchorAndInvalidationReleasesObserver()=runBlocking {
        repeat(140) { photo("p-$it",it.toLong()) }
        val source=TimelinePagingSource(repository,TimelineQuery())
        val result=source.load(PagingSource.LoadParams.Refresh(TimelineCursor(70,"photo:p-70"),90,false)) as PagingSource.LoadResult.Page<TimelineCursor,TimelineEntry>
        assertEquals(90,result.data.size);assertTrue(result.data.any { it.photoId=="p-70" })
        assertNotNull(result.prevKey);assertNotNull(result.nextKey)
        source.invalidate();assertTrue(source.invalid)
        assertTrue(source.load(PagingSource.LoadParams.Refresh(null,90,false)) is PagingSource.LoadResult.Invalid)
    }

    @Test fun sessionFreezesOrderDuringImportsAndResolvesVerifiedCloudToLocalTransition()=runBlocking {
        photo("middle",2000);cloud("older",1000);cloud("newer",3000)
        val selected=rows().first { it.photoId=="middle" }
        val store=ViewerSessionStore(directory,repository)
        val session=store.prepare(TimelineQuery(),selected)
        try {
            assertEquals(3,session.count);assertEquals(1,session.initialPage)
            val frozen=(0 until session.count).map { session.entry(it).key }
            photo("import-new",4000);photo("import-old",500)
            val original=photo("downloaded",1000)
            val old=requireNotNull(db.cloud().photo("a:older"))
            db.cloud().savePhoto(old.copy(photoId=original.id))
            assertEquals(frozen,(0 until session.count).map { session.entry(it).key })
            assertEquals("downloaded",repository.resolve(session.entry(2)).photo?.id)
            db.cloud().savePhoto(old.copy(removed=true))
            assertNull(repository.resolve(session.entry(2)).photo)
            assertEquals("cloud:a:older",session.entry(2).key)
        } finally { session.close() }
        assertTrue(directory.listFiles().orEmpty().isEmpty())
    }

    @Test fun sessionPreparationFailureKeepsIdentityAndCleansPartialIndex()=runBlocking {
        val selected=photo("selected",1000)
        val entry=rows().single()
        db.photos().update(selected.copy(archived=true))
        try { ViewerSessionStore(directory,repository).prepare(TimelineQuery(),entry);fail("Removed anchor must not select another photo") }
        catch(expected: ViewerPreparationException) { assertTrue(expected.message!!.contains("no longer")) }
        assertTrue(directory.listFiles().orEmpty().isEmpty())
        assertEquals(selected.copy(archived=true),db.photos().find(selected.id))
    }

    @Test fun changedRemoteContentIsUnavailableRatherThanAnotherPhoto()=runBlocking {
        val old=cloud("remote",1000);val entry=rows().single()
        db.cloud().savePhoto(old.copy(md5="different",version="v2"))
        val resolved=repository.resolve(entry)
        assertNull(resolved.photo);assertNull(resolved.cloud);assertEquals(entry,resolved.entry)
    }

    @Test fun cancelledSessionPreparationLeavesNoIndex()=runBlocking {
        repeat(200) { photo("p-$it",it.toLong()) }
        val entry=rows().first()
        val task=launch(start=CoroutineStart.LAZY) { ViewerSessionStore(directory,repository).prepare(TimelineQuery(),entry).close() }
        task.cancel();task.join()
        assertFalse(directory.exists())
    }

    @Test fun cancellationInsideSnapshotReleasesTransactionAndDeletesPartialIndex()=runBlocking {
        repeat(150) { photo("cancel-$it",it.toLong()) }
        val entry=rows().first()
        val reached=CompletableDeferred<Unit>()
        val release=java.util.concurrent.CountDownLatch(1)
        val checked=java.util.concurrent.atomic.AtomicInteger()
        val offline=TimelineRepository(db) {
            if(checked.incrementAndGet()==65) {
                reached.complete(Unit)
                check(release.await(10,java.util.concurrent.TimeUnit.SECONDS))
            }
            true
        }
        val task=launch { ViewerSessionStore(directory,offline).prepare(TimelineQuery("Offline"),entry).close() }
        withTimeout(10000) { reached.await() }
        task.cancel();release.countDown();task.join()
        assertTrue(directory.listFiles().orEmpty().isEmpty())
        photo("after-cancel",9999)
        assertEquals("photo:after-cancel",rows().first().key)
    }

    @Test fun removedAnchorRefreshChoosesChronologicalNeighborsWithoutResurrectingIt()=runBlocking {
        photo("newer",3000);val removed=photo("removed",2000);photo("older",1000)
        val anchor=rows()[1].cursor
        db.photos().update(removed.copy(archived=true))
        val refreshed=repository.around(TimelineQuery(),anchor,90)
        assertEquals(listOf("photo:newer","photo:older"),refreshed.map { it.key })
        assertEquals("photo:older",refreshed.first { TimelineOrder.compare(it.cursor,anchor)>=0 }.key)
    }

    @Test fun binaryKeyOrderingMatchesSqliteForSupplementaryUnicodeAndUnknownDates()=runBlocking {
        cloud("\uE000",1000);cloud("\uD800\uDC00",1000);cloud("unknown",null)
        val entries=rows()
        assertEquals(entries.map { it.cursor }.sortedWith(TimelineOrder),entries.map { it.cursor })
        assertEquals("cloud:a:\uE000",entries[0].key)
        assertEquals("cloud:a:\uD800\uDC00",entries[1].key)
    }

    @Test fun gridAnchorFollowsVerifiedRepresentationAndRetainsCursorWhenRemoved()=runBlocking {
        val remote=cloud("anchor",3000)
        val original=rows().single().cursor
        val local=photo("downloaded",null,imported=4000)
        db.cloud().savePhoto(remote.copy(photoId=local.id))
        val resolved=repository.reanchor(TimelineQuery(),original)
        assertEquals(TimelineCursor(3000,"photo:downloaded"),resolved)
        db.photos().update(local.copy(archived=true))
        assertEquals(original,repository.reanchor(TimelineQuery(),original))
        assertEquals(resolved,repository.reanchor(TimelineQuery(),resolved))
    }

    @Test fun queriesAndSessionsLeaveTakeoutMetadataEditsPinsAndAnnotationsUnchanged()=runBlocking {
        val original=photo("takeout",123000).copy(importedMetadata="{\"photoTakenTime\":{\"timestamp\":\"123\"}}",
            pinned=true,labels="cat",placeLabel="Home",latitude=32.0,longitude=34.0)
        db.photos().update(original)
        val edited=photo("edit",124000,parent=original.id).copy(editProvider="MPA",editInstructions="crop")
        db.photos().update(edited)
        val remote=cloud("takeout",123000,photo=original.id)
        val album=Album("preserved","Family",1)
        val membership=AlbumPhoto(album.id,original.id)
        val person=Person("preserved-person","Name")
        val face=FaceRecord("preserved-face",original.id,"",null,personId=person.id)
        db.library().saveAlbum(album);db.library().addToAlbum(membership)
        db.library().savePerson(person);db.library().saveFace(face)
        val before=db.photos().all()
        for(query in listOf(TimelineQuery(),TimelineQuery("Offline"),TimelineQuery("Edits"),TimelineQuery(album=album.id),TimelineQuery(person=person.id))) {
            val entries=rows(query)
            assertTrue(entries.isNotEmpty())
            val session=ViewerSessionStore(directory,repository).prepare(query,entries.first())
            try { repository.resolve(session.entry(session.initialPage)) } finally { session.close() }
        }
        assertEquals(before,db.photos().all())
        assertEquals(remote,db.cloud().photo(remote.key))
        assertEquals(face,db.library().face(face.id))
        assertEquals(9,db.openHelper.readableDatabase.version)
    }

    @Test fun lowStoragePreparationPreservesOriginalRecordsAndCleansIndex()=runBlocking {
        val photo=photo("space",123)
        val budget=StorageBudget(object: StorageBudget.Capacity {
            override fun available(directory: File)=0L
            override fun allocatable(directory: File)=0L
            override fun allocate(directory: File,bytes: Long)=error("Insufficient capacity must not allocate")
        })
        try { ViewerSessionStore(directory,repository,budget).prepare(TimelineQuery(),rows().single());fail("Low storage must be recoverable") }
        catch(expected: ViewerPreparationException) { assertTrue(expected.message!!.contains("space")) }
        assertEquals(photo,db.photos().find(photo.id))
        assertTrue(directory.listFiles().orEmpty().isEmpty())
    }
}

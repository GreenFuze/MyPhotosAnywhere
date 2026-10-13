package com.myphotosanywhere.mpa.search

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.nio.file.Files
import java.time.Instant
import java.time.ZoneId

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class SearchRepositoryTest {
    private lateinit var db: PhotoDatabase
    private lateinit var repository: TimelineRepository
    private val directory=Files.createTempDirectory("mpa-search-").toFile()
    @Before fun setup()=runBlocking {
        db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).build()
        repository=TimelineRepository(db) { it.startsWith("/present/") }
        for(key in listOf("a","b")) db.drive().saveSource(DriveSource(key,key,"$key@test","APP_FILES",key,key))
    }
    @After fun close() { db.close();directory.deleteRecursively() }
    private fun query(text: String,filter: String="All",source: String?=null)=TimelineQuery(filter,source,search=text,searchZone="UTC")
    private suspend fun photo(id: String,time: Long=1000,label: String="beach",source: String="a",favorite: Boolean=false,archived: Boolean=false,path: String="/present/$id",mime: String="image/jpeg"): Photo {
        val photo=Photo(id,"$id.jpg",path,"thumb-$id",mime,10,time,"sha-$id",capturedAt=time,labels=label,
            sourceBindingKey=source,favorite=favorite,archived=archived)
        db.photos().insert(photo);return photo
    }
    private suspend fun cloud(id: String,local: String?=null,removed: Boolean=false,source: String="a") {
        db.cloud().savePhoto(CloudPhoto("$source:$id",source,id,source,"beach dog.jpg","image/jpeg",10,"md5","1",null,2000,null,null,photoId=local,removed=removed))
    }
    private suspend fun rows(query: TimelineQuery)=repository.page(query,null,TimelineDirection.AFTER,300)
    @Test fun indexedPeopleAlbumsPlacesAndImportedMetadataPreserveTokenSemantics()=runBlocking {
        val value=photo("unicode").copy(displayName="ÉTÉ_%'.jpg",placeLabel="Tel Aviv",importedMetadata="Takeout wedding")
        db.photos().update(value);db.library().savePerson(Person("person","כינרת Alice"));db.library().saveFace(FaceRecord("face",value.id,"",null,personId="person"))
        db.library().saveAlbum(Album("album","Family trip",1));db.library().addToAlbum(AlbumPhoto("album",value.id))
        assertEquals(listOf(value.id),rows(query("été alice כינרת aviv trip wedding")).map { it.photoId })
        assertEquals(listOf(value.id),rows(query("_%'")).map { it.photoId });assertTrue(rows(query("' OR 1=1 --")).isEmpty())
    }
    @Test fun sparseBidirectionalPagesHaveNoMissingOrDuplicateMatches()=runBlocking {
        repeat(205) { photo("item-${it.toString().padStart(3,'0')}",(it/3).toLong(),if(it%13==0) "needle" else "other") }
        val q=query("needle");val expected=rows(q);val forward=mutableListOf<TimelineEntry>();var cursor: TimelineCursor?=null
        while(true) { val page=repository.page(q,cursor,TimelineDirection.AFTER,3);if(page.isEmpty()) break;forward+=page;cursor=page.last().cursor }
        assertEquals(16,forward.size);assertEquals(expected.map { it.key },forward.map { it.key })
        val backwards=repository.page(q,expected.last().cursor,TimelineDirection.BEFORE,300)
        assertEquals(expected.dropLast(1).map { it.key },backwards.map { it.key })
    }
    @Test fun sourcesArchiveTombstonesAndLinkedRepresentationsKeepVisibility()=runBlocking {
        photo("local");photo("b",source="b");photo("archive",archived=true);cloud("linked","archive")
        val removed=photo("removed").copy(sourceRemoteId="removed");db.photos().update(removed);cloud("removed",removed.id,true)
        cloud("remote");cloud("disabled",source="b");db.drive().disconnectSource("b")
        assertEquals(setOf("photo:local","cloud:a:remote"),rows(query("beach",source="a")).map { it.key }.toSet())
        assertEquals(listOf("photo:archive"),rows(query("beach","Archive")).map { it.key })
        assertTrue(rows(query("dog beach")).none { it.photoId==null })
    }
    @Test fun dateTypeAndOrganizationFiltersComposeAtLocalDayBoundaries()=runBlocking {
        val start=Instant.parse("2026-03-28T23:00:00Z").toEpochMilli();val end=Instant.parse("2026-03-29T22:00:00Z").toEpochMilli()
        photo("before",start-1,favorite=true,mime="video/mp4");photo("inside",start,favorite=true,mime="video/mp4")
        photo("last",end-1,favorite=true,mime="video/mp4");photo("after",end,favorite=true,mime="video/mp4");photo("image",start,favorite=true)
        val q=SearchCriteria("beach",TimelineMediaType.VIDEOS,"Favorites",dates=SearchDates(java.time.LocalDate.of(2026,3,29),java.time.LocalDate.of(2026,3,29))).timeline(ZoneId.of("Europe/Berlin"))
        assertEquals(listOf("last","inside"),rows(q).map { it.photoId })
    }
    @Test fun seekAndOfflineFilteringSkipNonmatchesAndMissingOriginals()=runBlocking {
        photo("new",3000,"other");photo("missing",2000,path="/missing");photo("old",1000)
        val q=query("beach","Offline")
        assertEquals(listOf("old"),rows(q).map { it.photoId });assertEquals("photo:old",repository.seek(q,4000)?.key)
        assertNull(repository.find(q,"photo:missing"))
    }
    @Test fun metadataRenamesInvalidateSearchWithoutChangingMediaIdentity()=runBlocking {
        val value=photo("identity");db.library().savePerson(Person("p","Before"));db.library().saveFace(FaceRecord("f",value.id,"",null,personId="p"))
        val paging=TimelinePagingSource(repository,query("Before"));assertEquals(value.id,rows(query("Before")).single().photoId)
        db.library().savePerson(Person("p","After"));db.invalidationTracker.refreshAsync()
        withTimeout(10000) { while(!paging.invalid) { delay(10) } }
        assertTrue(rows(query("Before")).isEmpty());assertEquals(value,db.photos().find(value.id));assertEquals(value.id,rows(query("After")).single().photoId)
    }
    @Test fun viewerSnapshotUsesTheSameQueryAndKeepsItsSequenceDuringImports()=runBlocking {
        photo("older",1000);photo("newer",3000);photo("other",2000,"other")
        val q=query("beach");val expected=rows(q);val store=ViewerSessionStore(directory,repository)
        val session=store.prepare(q,expected.last())
        try {
            photo("inserted",2000);assertEquals(2,session.count)
            assertEquals(expected.map { it.key },(0 until session.count).map { session.entry(it).key })
            assertEquals(1,session.initialPage)
        } finally { session.close() }
    }
    @Test fun repeatedFaceAssociationsDoNotDuplicateResultsAndLabelsNeedNoOriginal()=runBlocking {
        val value=photo("remote-metadata",path="");db.library().savePerson(Person("p","Alice"))
        repeat(3) { db.library().saveFace(FaceRecord("f$it",value.id,"",null,personId="p")) }
        assertEquals(listOf(value.id),rows(query("Alice beach")).map { it.photoId });assertEquals("",db.photos().find(value.id)?.originalPath)
    }
}

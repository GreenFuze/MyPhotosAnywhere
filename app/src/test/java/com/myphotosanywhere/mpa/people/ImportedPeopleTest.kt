package com.myphotosanywhere.mpa.people

import android.app.Application
import androidx.room.Room
import com.google.gson.JsonArray
import com.google.gson.JsonObject
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.importing.TakeoutCodec
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.*
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.nio.file.Files

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ImportedPeopleTest {
    private lateinit var db: PhotoDatabase
    private lateinit var timeline: TimelineRepository
    private lateinit var imported: ImportedPeopleRepository
    @Before fun setup()=runBlocking { db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).build();timeline=TimelineRepository(db) { it.startsWith("present") };imported=ImportedPeopleRepository(timeline) }
    @After fun close() { db.close() }
    private suspend fun photo(id: String,names: List<String>,binding: String?="a",time: Long=100,archived: Boolean=false): Photo {
        val raw=JsonObject().apply { addProperty("title","$id.jpg");add("photoTakenTime",JsonObject().apply { addProperty("timestamp","1") });add("people",JsonArray().apply { names.forEach { name -> add(JsonObject().apply { addProperty("name",name) }) } }) }.toString()
        val codec=TakeoutCodec();val photo=codec.apply(Photo(id,"$id.jpg","present-$id","thumb-$id","image/jpeg",1,time,"hash-$id",capturedAt=time,sourceBindingKey=binding,archived=archived),codec.parse(raw));db.photos().insert(photo);return photo
    }
    private suspend fun page(label: ImportedPersonLabel)=timeline.page(TimelineQuery(importedPerson=label),null,TimelineDirection.AFTER,300)
    @Test fun existingMetadataBecomesGroupsWithoutMutationOrRecognition()=runBlocking {
        val photo=photo("old",listOf(" כינרת ","כינרת","Alice",""));val before=requireNotNull(db.photos().find(photo.id))
        assertEquals(listOf("Alice","כינרת"),imported.groups().map { it.label.name });assertEquals(listOf(1,1),imported.groups().map { it.photos })
        assertEquals(before,db.photos().find(photo.id));assertTrue(db.library().allFaces().isEmpty());assertTrue(db.library().allPeople().isEmpty())
    }
    @Test fun equalNamesAcrossSourcesAndReviewedIdentitiesRemainSeparate()=runBlocking {
        photo("a",listOf("Sam"));photo("b",listOf("Sam"),"b");db.library().savePerson(Person("reviewed","Sam"));db.library().saveFace(FaceRecord("f","a","",null,personId="reviewed",corrected=true))
        val groups=imported.groups();assertEquals(2,groups.size);assertNotEquals(groups[0].label.key,groups[1].label.key)
        assertEquals(listOf("a"),page(ImportedPersonLabel("Sam","a")).map { it.photoId });assertEquals(listOf("b"),page(ImportedPersonLabel("Sam","b")).map { it.photoId })
        assertEquals(listOf(Person("reviewed","Sam")),db.library().allPeople());assertEquals("reviewed",db.library().face("f")?.personId)
    }
    @Test fun punctuationUnicodeAndCaseUseExactLabelsRatherThanSubstringQueries()=runBlocking {
        photo("one",listOf("O'Neil_%","Été","כינרת"));photo("two",listOf("o'neil_%","Été Jr"))
        for(name in listOf("O'Neil_%","Été","כינרת")) assertEquals(listOf("one"),page(ImportedPersonLabel(name,"a")).map { it.photoId })
        assertTrue(page(ImportedPersonLabel("Neil","a")).isEmpty());assertTrue(page(ImportedPersonLabel("' OR 1=1 --","a")).isEmpty())
    }
    @Test fun missingPeopleAndUnknownProvenanceDoNotInventIdentities()=runBlocking {
        photo("empty",emptyList());db.photos().insert(Photo("plain","plain.jpg","present","","image/jpeg",1,1,"hash",importedMetadata="legacy text"))
        assertTrue(imported.groups().isEmpty());assertTrue(ImportedPeopleCodec().names("{\"googlePhotosPicker\":{}}").isEmpty());assertNull(ImportedPersonLabel.fromKey("reviewed-person"))
    }
    @Test fun archiveSourceAndRemovedRepresentationVisibilityRemainCanonical()=runBlocking {
        val visible=photo("visible",listOf("Sam"));photo("archived",listOf("Sam"),archived=true)
        val removed=photo("removed",listOf("Sam")).copy(originalPath="",sourceRemoteId="r");db.photos().update(removed)
        db.drive().saveSource(DriveSource("a","a","a@test","APP_FILES","a","A"))
        db.cloud().savePhoto(CloudPhoto("a:r","a","r","a","removed.jpg","image/jpeg",1,"md5","1",null,100,null,null,photoId="removed",removed=true))
        assertEquals(1,imported.groups().single().photos);assertEquals(listOf(visible.id),page(ImportedPersonLabel("Sam","a")).map { it.photoId })
        assertEquals(listOf("archived"),timeline.page(TimelineQuery("Archive",importedPerson=ImportedPersonLabel("Sam","a")),null,TimelineDirection.AFTER,10).map { it.photoId })
    }
    @Test fun sparseBidirectionalPagingSeekAndOfflineFilterHaveNoExtraMatches()=runBlocking {
        repeat(35) { photo("p${it.toString().padStart(2,'0')}",if(it%5==0) listOf("Sam") else listOf("Other"),time=it.toLong()) }
        val q=TimelineQuery(importedPerson=ImportedPersonLabel("Sam","a"));val expected=page(ImportedPersonLabel("Sam","a"));val actual=mutableListOf<TimelineEntry>();var cursor: TimelineCursor?=null
        while(true) { val rows=timeline.page(q,cursor,TimelineDirection.AFTER,2);if(rows.isEmpty()) break;actual+=rows;cursor=rows.last().cursor }
        assertEquals(expected.map { it.key },actual.map { it.key });assertEquals(7,actual.size);assertTrue(actual.all { it.searchDocument==null })
        assertEquals(expected.dropLast(1).map { it.key },timeline.page(q,actual.last().cursor,TimelineDirection.BEFORE,10).map { it.key })
        assertEquals(15L,timeline.seek(q,20)?.millis);assertEquals(7,timeline.page(q.copy(filter="Offline"),null,TimelineDirection.AFTER,10).size)
        db.photos().find("p30")?.let { db.photos().update(it.copy(originalPath="missing")) };assertEquals(6,timeline.page(q.copy(filter="Offline"),null,TimelineDirection.AFTER,10).size)
    }
    @Test fun viewerSnapshotRemainsFrozenAcrossImportsAndLabelChanges()=runBlocking {
        photo("one",listOf("Sam"),time=100);photo("two",listOf("Sam"),time=50)
        val label=ImportedPersonLabel("Sam","a");val q=TimelineQuery(importedPerson=label);val directory=Files.createTempDirectory("mpa-imported-people").toFile()
        try { val session=ViewerSessionStore(directory,timeline).prepare(q,page(label).first())
            session.use { assertEquals(2,it.count);photo("new",listOf("Sam"),time=200);assertEquals("one",it.entry(0).photoId);assertEquals("two",it.entry(1).photoId) }
            assertEquals(3,page(label).size)
        } finally { directory.deleteRecursively() }
    }
    @Test fun keysRestoreExactlyAndInvalidReservedKeysFailFast() {
        for(binding in listOf(null,"a/b:source")) { val label=ImportedPersonLabel("כינרת O'Neil_%",binding);assertEquals(label,ImportedPersonLabel.fromKey(label.key)) }
        assertThrows(IllegalArgumentException::class.java) { ImportedPersonLabel.fromKey("takeout-person:!") }
        assertThrows(IllegalArgumentException::class.java) { ImportedPersonLabel("  ",null) }
    }
    @Test fun providerLabelKeysCannotHijackAnExistingReviewedPersonId()=runBlocking<Unit> {
        photo("imported",listOf("Sam"));photo("reviewed",emptyList())
        val label=ImportedPersonLabel("Sam","a")
        db.library().savePerson(Person(label.key,"Reviewed identity"));db.library().saveFace(FaceRecord("reviewed-face","reviewed","",null,personId=label.key,corrected=true))
        assertEquals(listOf("reviewed"),timeline.page(TimelineQuery(person=label.key),null,TimelineDirection.AFTER,10).map { it.photoId })
        assertEquals(listOf("imported"),page(label).map { it.photoId });assertEquals(label.key,db.library().face("reviewed-face")?.personId)
        assertThrows(IllegalArgumentException::class.java) { TimelineQuery(person=label.key,importedPerson=label) }
    }
    @Test fun importedOriginRoundTripsSeparatelyFromReviewedIdentity() {
        val label=ImportedPersonLabel("כינרת","source")
        val context=com.myphotosanywhere.mpa.search.GalleryContext("All",null,null,null,label.key)
        assertEquals(context,com.myphotosanywhere.mpa.search.GalleryContext.restore(context.save()))
        assertNull(context.person);assertEquals(label,ImportedPersonLabel.fromKey(context.importedPerson))
    }
}

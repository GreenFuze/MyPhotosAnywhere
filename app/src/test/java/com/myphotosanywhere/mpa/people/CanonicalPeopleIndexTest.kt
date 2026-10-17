package com.myphotosanywhere.mpa.people

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.core.LibrarySettings
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.importing.TakeoutCodec
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.flow.first
import org.junit.Assert.*
import org.junit.*
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class CanonicalPeopleIndexTest {
    private lateinit var db: PhotoDatabase
    private lateinit var index: CanonicalPeopleIndex
    private lateinit var timeline: TimelineRepository
    private lateinit var people: PeopleRepository
    @Before fun setup() {
        val context=RuntimeEnvironment.getApplication()
        db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        installPersonLabelTriggers(db.openHelper.writableDatabase)
        index=CanonicalPeopleIndex(db);timeline=TimelineRepository(db,originalExists={ true })
        val settings=LibrarySettings(context)
        people=PeopleRepository(context,db,MediaCache(context,db,settings),settings)
    }
    @After fun close() { db.close() }
    private suspend fun photo(id: String,binding: String="a"): Photo {
        val codec=TakeoutCodec()
        val raw="""{"title":"$id.jpg","photoTakenTime":{"timestamp":"1"},"people":[{"name":"Sam"}],"unknown":{"preserve":true}}"""
        val photo=codec.apply(Photo(id,"$id.jpg","present-$id","thumb-$id","image/jpeg",1,100,"hash-$id",capturedAt=100,sourceBindingKey=binding),codec.parse(raw))
        db.photos().insert(photo);return photo
    }
    private suspend fun identity(binding: String="a")=requireNotNull(db.personLabels().alias(ImportedPersonLabel("Sam",binding).key)?.personId)
    private suspend fun members(person: String)=timeline.page(TimelineQuery(person=person),null,TimelineDirection.AFTER,100).map { it.photoId }
    private suspend fun groups()=CanonicalPeopleRepository(timeline).groups()
    @Test fun labelsAndReviewedFaceMergeIntoOneCardAndPhotoWithoutMetadataMutation()=runBlocking {
        val original=photo("one");index.drain();val imported=identity()
        db.library().savePerson(Person("reviewed","Samuel"))
        db.library().saveFace(FaceRecord("face","one","",null,personId="reviewed",corrected=true))
        people.merge(imported,"reviewed")
        assertEquals("reviewed",identity());assertEquals(listOf("one"),members("reviewed"))
        assertEquals(listOf(1),groups().map { it.photos });assertEquals(original,db.photos().find("one"))
        assertEquals("reviewed",db.library().face("face")?.personId)
    }
    @Test fun canonicalRenameSurvivesNewImportsAndLaterSourceBindingChanges()=runBlocking {
        val original=photo("one");index.drain();val person=identity()
        db.library().savePerson(Person(person,"Samuel"));photo("two");index.drain()
        db.photos().update(original.copy(sourceBindingKey="new-folder"));index.drain()
        assertEquals(person,identity());assertNull(db.personLabels().alias(ImportedPersonLabel("Sam","new-folder").key))
        assertEquals("a",db.personLabels().origin("one")?.binding)
        assertEquals("Samuel",groups().single().person.name);assertEquals(2,groups().single().photos)
        assertEquals(setOf("one","two"),timeline.page(TimelineQuery(search="Samuel"),null,TimelineDirection.AFTER,100).map { it.photoId }.toSet())
    }
    @Test fun equalNamesAcrossSourcesRequireMergeAndFutureImportsReuseSurvivor()=runBlocking {
        photo("a","a");photo("b","b");index.drain();val a=identity("a");val b=identity("b")
        assertNotEquals(a,b);people.merge(b,a);photo("new","b");index.drain()
        assertEquals(a,identity("b"));assertEquals(3,groups().single().photos)
        assertEquals(setOf("a","b","new"),members(a).toSet())
    }
    @Test fun exclusionSurvivesMetadataRemovalAndReadditionWithoutHidingOriginal()=runBlocking {
        val original=photo("one");index.drain();val person=identity()
        db.personLabels().exclude("one",person);index.index("one")
        db.photos().update(original.copy(importedMetadata=""));index.drain()
        db.photos().update(original);index.drain()
        assertTrue(db.personLabels().members("one").single().excluded);assertTrue(members(person).isEmpty())
        assertEquals(0,groups().single().photos);assertEquals(original,db.photos().find("one"))
        assertEquals(listOf("one"),timeline.page(TimelineQuery(),null,TimelineDirection.AFTER,100).map { it.photoId })
    }
    @Test fun removingPersonIgnoresRetainedAliasInsteadOfResurrectingIdentity()=runBlocking {
        val original=photo("one");index.drain();val person=identity()
        db.library().saveFace(FaceRecord("face","one","",null,personId=person))
        people.deletePerson(person);index.index("one")
        assertTrue(groups().isEmpty());assertTrue(db.personLabels().alias(ImportedPersonLabel("Sam","a").key)?.ignored==true)
        assertNull(db.library().face("face")?.personId);assertTrue(db.library().face("face")?.corrected==true)
        assertEquals(original,db.photos().find("one"))
    }
    @Test fun malformedRowDoesNotStopOtherLabelsAndRetryOnlyProcessesFailedWork()=runBlocking {
        val good=photo("good");val bad=photo("bad").copy(importedMetadata="{invalid");db.photos().update(bad)
        assertEquals(1,index.drain());val person=identity()
        assertEquals(1,db.personLabels().errors());assertTrue(db.personLabels().pending(50).isEmpty())
        assertEquals(good,db.photos().find("good"));assertEquals(bad,db.photos().find("bad"))
        db.photos().update(bad.copy(importedMetadata=good.importedMetadata));assertEquals(1,index.drain())
        assertEquals(person,identity());assertEquals(0,db.personLabels().errors());assertEquals(2,db.personLabels().allMembers().size)
        assertEquals(0,index.drain());assertEquals(1,groups().size)
    }
    @Test fun assigningAnExistingPersonPreservesTheirLatestCanonicalName()=runBlocking {
        val original=photo("one");index.drain();val person=identity()
        db.library().savePerson(Person(person,"Samuel"));db.library().saveFace(FaceRecord("face","one","",null))
        people.assign(listOf("face"),Person(person,"Stale label"),allowCreate=false)
        assertEquals("Samuel",db.personLabels().person(person)?.name);assertEquals(person,db.library().face("face")?.personId)
        assertEquals(original,db.photos().find("one"));assertEquals(1,groups().single().photos)
    }
    @Test fun staleFaceAndPersonChoicesDoNotCreateEmptyOrResurrectedIdentities()=runBlocking {
        photo("one");db.library().saveFace(FaceRecord("face","one","",null))
        try { people.assign(listOf("missing"),Person("new","New"));fail("Missing face must not create a person") } catch(_: java.io.IOException) { }
        try { people.assign(listOf("face"),Person("deleted","Old"),allowCreate=false);fail("Deleted identity must not return") } catch(_: java.io.IOException) { }
        assertTrue(db.library().allPeople().isEmpty());assertNull(db.library().face("face")?.personId)
    }
    @Test fun duplicateFaceAssignmentsFailBeforeWritingAnything()=runBlocking {
        db.library().saveFace(FaceRecord("face","photo","",null))
        try { people.assign(listOf("face","face"),Person("new","New"));fail("Duplicate identities must fail") } catch(_: IllegalArgumentException) { }
        assertTrue(db.library().allPeople().isEmpty());assertNull(db.library().face("face")?.personId)
    }
    @Test fun collectionAndSearchPeopleCoversUseTheSameNamesOnlyMemberships()=runBlocking {
        val original=photo("one");index.drain();val person=identity()
        val collections=com.myphotosanywhere.mpa.library.CollectionsRepository(db,timeline)
        assertEquals(listOf(original.thumbnailPath),collections.special("People").first().paths)
        db.personLabels().exclude(original.id,person)
        assertTrue(collections.special("People").first().paths.isEmpty())
        assertEquals(original,db.photos().find(original.id));assertTrue(db.library().allFaces().isEmpty())
    }
}

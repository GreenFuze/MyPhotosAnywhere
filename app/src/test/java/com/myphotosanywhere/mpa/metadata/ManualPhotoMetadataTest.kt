package com.myphotosanywhere.mpa.metadata

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import kotlinx.coroutines.runBlocking
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.security.MessageDigest

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ManualPhotoMetadataTest {
    private class Fixture: AutoCloseable {
        val db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).build()
        val server=MockWebServer().apply { start() }
        val client=OkHttpClient.Builder().followRedirects(false).followSslRedirects(false).build()
        val api=DriveGateway("test-token",client,server.url("/drive/v3/"),server.url("/upload/drive/v3/files"))
        val task=DriveBackup("manual","source","photo","account","test@example.com","folder","APP_FILES",state="METADATA_PENDING",remoteId="original",uploadedBytes=10,remoteMd5="verified",remoteVersion="v1")
        val metadata=ManualPhotoMetadata(db)
        val alias=com.myphotosanywhere.mpa.people.ImportedPersonLabel("Sam",null).key
        val descriptor="""{"id":"sidecar","name":"original.mpa.xmp","mimeType":"application/rdf+xml","parents":["folder"],"trashed":false,"version":"1"}"""
        suspend fun create() {
            db.photos().insert(Photo("photo","Camera.jpg","/private/original","/private/thumbnail","image/jpeg",10,123,"a".repeat(64),capturedAt=122,
                latitude=32.0,longitude=34.0,favorite=true,placeLabel="Garden",labels="plants",parentId="parent",editProvider="mpa",editInstructions="keep",
                importedMetadata="{\"googleTakeout\":{\"title\":\"Camera.jpg\",\"photoTakenTime\":{\"timestamp\":\"1\"},\"people\":[{\"name\":\"Sam\"}]}}"))
            db.photos().insert(Photo("unrelated","Other.jpg","/private/other","","image/jpeg",20,999,"b".repeat(64)))
            db.library().saveAlbum(Album("album","Family",1));db.library().addToAlbum(AlbumPhoto("album","photo"))
            db.library().saveAlbum(Album("other-album","Unrelated",1))
            db.library().saveDraft(EditDraft("photo",com.myphotosanywhere.mpa.edit.EditRecipe(exposure=0.5f).json(),1))
            db.library().savePerson(Person("person","Samuel"));db.library().savePerson(Person("other-person","Unrelated"))
            db.library().saveFace(FaceRecord("face","photo","/private/crop",byteArrayOf(1,2,3),personId="person",corrected=true,left=0.1f,top=0.1f,right=0.4f,bottom=0.4f,imageWidth=100,imageHeight=100,modelVersion="test"))
            db.personLabels().saveAlias(PersonAlias(alias,"Sam",null,"person"))
            db.personLabels().saveMember(PhotoPersonLabel("photo",alias,"person",true));db.personLabels().restoreOrigin(PhotoPeopleOrigin("photo",null))
            db.deviceMedia().authorize(ManualDeviceBackup(task.id,task.photoId,task.bindingKey,"a".repeat(64),10,true,metadataId="sidecar"))
            db.drive().insertBackup(task)
        }
        fun reply(code: Int=200,body: String="{}",etag: String?=null) {
            server.enqueue(MockResponse().setResponseCode(code).setBody(body).apply { if(etag!=null) setHeader("ETag",etag) })
        }
        fun hash(xml: String)=MessageDigest.getInstance("SHA-256").digest(xml.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
        override fun close() { db.close();server.shutdown();client.connectionPool.evictAll();client.dispatcher.executorService.shutdown() }
    }
    @Test fun portableSnapshotContainsOnlyThisPhotoAnnotationsAndNoPrivateBiometrics()=runBlocking<Unit> {
        Fixture().use { f ->
            f.create();val xml=XmpCodec.encode(f.metadata.snapshot(f.task));val catalog=XmpCodec.decode(xml)
            val photo=catalog.assets.single()
            assertEquals("original",photo.remoteId);assertEquals(122L,photo.capturedAt);assertEquals(32.0,photo.latitude)
            assertEquals(com.myphotosanywhere.mpa.edit.EditRecipe(exposure=0.5f).json(),photo.recipe);assertEquals(listOf("album"),photo.albums);assertTrue(photo.favorite)
            assertEquals("Samuel",catalog.people.single().name);assertEquals("Family",catalog.albums.single().name)
            assertTrue(requireNotNull(photo.photoPeople).members.single().excluded);assertTrue(photo.faces.single().corrected)
            assertFalse(xml.contains("/private/"));assertFalse(xml.contains("Unrelated"));assertFalse(xml.contains("embedding"));assertFalse(xml.contains("cropPath"))
            assertEquals(f.task,f.db.drive().backup(f.task.id))
        }
    }
    @Test fun sidecarCreateIsReadBackBeforeAcknowledgingAndNeverUploadsTheOriginalAgain()=runBlocking<Unit> {
        Fixture().use { f ->
            f.create();val xml=XmpCodec.encode(f.metadata.snapshot(f.task))
            f.reply(404);f.reply();f.reply(body=f.descriptor);f.reply(body=xml,etag="accepted")
            f.metadata.backup(f.api,f.task)
            val saved=requireNotNull(f.db.deviceMedia().authorization(f.task.id))
            assertEquals("sidecar",saved.metadataId);assertEquals(f.hash(xml),saved.metadataSha);assertEquals("accepted",saved.metadataEtag)
            assertEquals(4,f.server.requestCount)
            val paths=(1..4).map { f.server.takeRequest() }
            assertEquals("POST",paths[1].method);val body=paths[1].body.readUtf8()
            assertTrue(body.contains("original.mpa.xmp"));assertFalse(body.contains("/private/original"))
            assertTrue(paths.filterIndexed { index,_ -> index!=1 }.all { it.path?.startsWith("/drive/v3/files/sidecar")==true })
            assertEquals(f.task,f.db.drive().backup(f.task.id))
        }
    }
    @Test fun aLostCreateResponseReusesTheReservedSidecarWithoutWritingAgain()=runBlocking<Unit> {
        Fixture().use { f ->
            f.create();val xml=XmpCodec.encode(f.metadata.snapshot(f.task))
            val saved=requireNotNull(f.db.deviceMedia().authorization(f.task.id))
            f.db.deviceMedia().updateAuthorization(saved.copy(metadataSha=f.hash(xml)))
            f.reply(body=f.descriptor);f.reply(body=xml,etag="recovered")
            f.metadata.backup(f.api,f.task)
            assertEquals(2,f.server.requestCount);assertEquals("recovered",f.db.deviceMedia().authorization(f.task.id)?.metadataEtag)
            repeat(2) { assertEquals("GET",f.server.takeRequest().method) }
        }
    }
    @Test fun changedRemoteMetadataIsKeptAndItsOriginalCheckpointSurvives()=runBlocking<Unit> {
        Fixture().use { f ->
            f.create();val saved=requireNotNull(f.db.deviceMedia().authorization(f.task.id))
            f.db.deviceMedia().updateAuthorization(saved.copy(metadataSha="old",metadataEtag="old-tag"))
            f.reply(body=f.descriptor);f.reply(body="someone else's annotations",etag="new-tag")
            try { f.metadata.backup(f.api,f.task);fail("Conflicting annotations must not be overwritten") } catch(_: DriveBackupBlocked) { }
            assertEquals(2,f.server.requestCount);assertEquals(f.task,f.db.drive().backup(f.task.id))
            assertEquals("old-tag",f.db.deviceMedia().authorization(f.task.id)?.metadataEtag)
        }
    }
}

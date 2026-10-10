package com.myphotosanywhere.mpa.metadata

import android.app.Application
import androidx.room.Room
import androidx.room.withTransaction
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class MetadataPersistenceTest {
    @Test fun editsDuringAnUploadRemainInThePersistentOutbox() = runBlocking {
        val db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).build()
        try {
            db.metadata().insert(MetadataState("source")); db.metadata().dirty("source")
            val upload=requireNotNull(db.metadata().state("source"))
            db.metadata().dirty("source")
            db.metadata().uploaded("source","remote","etag",upload.generation)
            val current=requireNotNull(db.metadata().state("source"))
            assertEquals(2L,current.generation); assertEquals(1L,current.uploadedGeneration)
            assertEquals("etag",current.etag)
        } finally { db.close() }
    }
    @Test fun sourceCatalogIncludesOnlyItsAssetsAndReferencedGlobalPeopleAndAlbums() = runBlocking {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        try {
            val a=DriveSource.from(DriveConnection(accountId="a",email="a@example.test",mode="APP_FILES",folderId="a-folder",folderName="A"))
            val b=DriveSource.from(DriveConnection(accountId="b",email="b@example.test",mode="APP_FILES",folderId="b-folder",folderName="B"))
            db.drive().saveSource(a); db.drive().saveSource(b)
            db.library().savePerson(Person("shared-person","Sam")); db.library().savePerson(Person("private-person","Private"))
            db.library().saveAlbum(Album("a-album","A trip",1)); db.library().saveAlbum(Album("b-album","Private trip",1))
            for((source,id,person,album) in listOf(listOf(a.key,"a-photo","shared-person","a-album"),listOf(b.key,"b-photo","private-person","b-album"))) {
                db.photos().insert(Photo(id,"$id.jpg","","","image/jpeg",10,1,"a".repeat(64),sourceBindingKey=source,sourceRemoteId=id,cachedVersion="1"))
                db.cloud().savePhoto(CloudPhoto("$source:$id",source,id,"parent","$id.jpg","image/jpeg",10,"md5","1",null,null,null,null,photoId=id))
                db.library().saveFace(FaceRecord("face-$id",id,"/private/crop",byteArrayOf(1,2),person,corrected=true))
                db.library().addToAlbum(AlbumPhoto(album,id))
            }
            val catalog=MetadataRepository(context,db).snapshot(a)
            assertEquals(listOf("a-photo"),catalog.assets.map { it.id })
            assertEquals(listOf("shared-person"),catalog.people.map { it.id })
            assertEquals(listOf("a-album"),catalog.albums.map { it.id })
            val xml=XmpCodec.encode(catalog)
            assertFalse(xml.contains("Private")); assertFalse(xml.contains("b-photo")); assertFalse(xml.contains("/private/crop"))
        } finally { db.close() }
    }
    @Test fun recoveryRestoresPortableIdsReviewedRegionsAlbumsAndRecipeWithoutChangingMedia() = runBlocking {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        try {
            val source=DriveSource.from(DriveConnection(accountId="a",email="a@test",mode="APP_FILES",folderId="folder",folderName="A"))
            db.drive().saveSource(source)
            val photo=Photo("fresh-local-id","photo.jpg","/original","/thumb","image/jpeg",10,1,"a".repeat(64),
                sourceBindingKey=source.key,sourceRemoteId="remote",cachedVersion="1")
            db.photos().insert(photo)
            db.cloud().savePhoto(CloudPhoto("${source.key}:remote",source.key,"remote","folder","photo.jpg","image/jpeg",10,"md5","1",null,null,null,null,photoId=photo.id))
            val repository=MetadataRepository(context,db)
            val asset=repository.snapshot(source).assets.single().copy(id="portable-id",favorite=true,place="Home",
                recipe=com.myphotosanywhere.mpa.edit.EditRecipe().json(),albums=listOf("album"),
                faces=listOf(PortableFace("stable-face","person",true,com.myphotosanywhere.mpa.people.FaceRegion(.1f,.1f,.4f,.4f),1600,1200,"model")))
            val catalog=XmpCatalog("a","folder",listOf(asset),listOf(PortablePerson("person","Sam")),listOf(PortableAlbum("album","Family")))
            repository.applyCatalog(source,XmpCodec.decode(XmpCodec.encode(catalog)),1)
            val restored=requireNotNull(db.photos().find(photo.id))
            assertEquals("portable-id",restored.metadataAssetId); assertTrue(restored.favorite)
            assertEquals(photo.originalPath,restored.originalPath); assertEquals(photo.sha256,restored.sha256)
            val face=requireNotNull(db.library().face("stable-face"))
            assertEquals(photo.id,face.photoId); assertEquals("person",face.personId); assertTrue(face.corrected)
            assertNull(face.embedding); assertEquals(.1f,face.left!!,.0001f)
            assertEquals("Sam",db.library().allPeople().single().name)
            assertEquals(listOf(AlbumPhoto("album",photo.id)),db.library().allMemberships())
            assertNotNull(db.library().draft(photo.id)); assertEquals("restored",db.library().scan(photo.id)?.modelVersion)
        } finally { db.close() }
    }
    @Test fun checksumMismatchIsSkippedAndIdentityCollisionRollsBackTheWholeRecovery() = runBlocking {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        try {
            val source=DriveSource.from(DriveConnection(accountId="a",email="a@test",mode="APP_FILES",folderId="folder",folderName="A"))
            db.drive().saveSource(source)
            val photo=Photo("local","photo.jpg","/original","/thumb","image/jpeg",10,1,"a".repeat(64))
            db.photos().insert(photo); db.photos().insert(photo.copy(id="other"))
            db.cloud().savePhoto(CloudPhoto("${source.key}:remote",source.key,"remote","folder","photo.jpg","image/jpeg",10,"md5","1",null,null,null,null,photoId=photo.id))
            val repository=MetadataRepository(context,db); val original=repository.snapshot(source)
            val changed=original.copy(assets=original.assets.map { it.copy(sha256="b".repeat(64),favorite=true) })
            repository.applyCatalog(source,changed,0)
            assertFalse(requireNotNull(db.photos().find(photo.id)).favorite)
            db.library().saveFace(FaceRecord("collision","other","crop",null))
            val collision=original.copy(assets=original.assets.map { it.copy(favorite=true,
                faces=listOf(PortableFace("collision","new-person",true,null,null,null,null))) },people=listOf(PortablePerson("new-person","New")))
            try { repository.applyCatalog(source,collision,1); fail("Expected collision") } catch(_: IllegalArgumentException) { }
            assertFalse(requireNotNull(db.photos().find(photo.id)).favorite)
            assertEquals("other",db.library().face("collision")?.photoId); assertTrue(db.library().allPeople().isEmpty())
        } finally { db.close() }
    }

    @Test fun annotationMutationAndOutboxGenerationCommitOrRollBackTogether() = runBlocking {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).addCallback(METADATA_CALLBACK).build()
        try {
            val source=DriveSource.from(DriveConnection(accountId="a",email="a@test",mode="APP_FILES",folderId="folder",folderName="A"))
            db.drive().saveSource(source)
            val photo=Photo("photo","photo.jpg","/original","/thumb","image/jpeg",10,1,"a".repeat(64))
            db.photos().insert(photo)
            val before=requireNotNull(db.metadata().state(source.key)).generation
            db.photos().update(photo.copy(favorite=true))
            val edited=requireNotNull(db.metadata().state(source.key)).generation
            assertTrue(edited>before)
            try {
                db.withTransaction { db.photos().update(photo.copy(placeLabel="rollback")); error("Abort") }
            } catch(_: IllegalStateException) { }
            assertEquals(edited,db.metadata().state(source.key)?.generation)
            assertEquals("",db.photos().find(photo.id)?.placeLabel)
            db.photos().update(requireNotNull(db.photos().find(photo.id)).copy(lastAccess=123))
            assertEquals(edited,db.metadata().state(source.key)?.generation)
        } finally { db.close() }
    }

}

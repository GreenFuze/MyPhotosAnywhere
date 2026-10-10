package com.myphotosanywhere.mpa.metadata

import android.app.Application
import androidx.room.Room
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
}

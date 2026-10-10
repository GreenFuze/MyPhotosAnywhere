package com.myphotosanywhere.mpa.library

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
class LibraryPersistenceTest {
    @Test fun changingRemoteVersionKeepsLocalPhotoAndPersonCorrections() = runBlocking {
        val db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).allowMainThreadQueries().build()
        try {
            val p=Photo("local","photo.jpg","/original","/thumb","image/jpeg",1,1,"hash",sourceBindingKey="binding",sourceRemoteId="remote",cachedVersion="1")
            db.photos().insert(p)
            val row=CloudPhoto("binding:remote","binding","remote","folder","photo.jpg","image/jpeg",1,"md5","1",null,null,null,null,photoId=p.id)
            db.cloud().savePhoto(row)
            db.library().savePerson(Person("person","Alice"))
            db.library().saveFace(FaceRecord("face",p.id,"/face",null,personId="person",corrected=true))
            db.cloud().savePhoto(row.copy(version="2",removed=true))
            assertEquals(p,db.photos().find(p.id))
            assertTrue(db.library().face("face")?.corrected==true)
            assertEquals("person",db.library().face("face")?.personId)
        } finally { db.close() }
    }
    @Test fun failedReconciliationRollsBackItsTokenAndRows() = runBlocking {
        val db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).allowMainThreadQueries().build()
        try {
            db.cloud().saveSync(SyncState("binding","old"))
            try { db.withTransaction {
                db.cloud().saveSync(SyncState("binding","new"))
                db.cloud().savePhoto(CloudPhoto("binding:remote","binding","remote","folder","photo.jpg","image/jpeg",1,null,null,null,null,null,null))
                error("Interrupted transaction")
            } } catch(_: IllegalStateException) { }
            assertEquals("old",db.cloud().sync("binding")?.changeToken)
            assertNull(db.cloud().photo("binding:remote"))
        } finally { db.close() }
    }
    @Test fun mergedAndRemovedNamesLeaveCorrectionsAndPhotosIntact() = runBlocking {
        val db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).allowMainThreadQueries().build()
        try {
            db.library().savePerson(Person("one","Alice"));db.library().savePerson(Person("two","Alicia"))
            db.library().saveFace(FaceRecord("face","photo","/face",null,personId="one"))
            db.library().mergePeople("one","two"); db.library().deletePerson("one")
            assertEquals("two",db.library().face("face")?.personId)
            db.library().unassignPerson("two");db.library().deletePerson("two")
            assertNull(db.library().face("face")?.personId); assertTrue(db.library().face("face")?.corrected==true)
        } finally { db.close() }
    }
    @Test fun changedCloudBytesBecomeANewCloudEntryWhileLocalSnapshotIsPreserved() = runBlocking {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).allowMainThreadQueries().build()
        try {
            val photo=Photo("local","photo.jpg","/original","/thumb","image/jpeg",8,1,"sha",sourceBindingKey="binding",sourceRemoteId="remote",cachedVersion="1")
            db.photos().insert(photo)
            db.cloud().savePhoto(CloudPhoto("binding:remote","binding","remote","folder","photo.jpg","image/jpeg",8,"first","1",null,null,null,null,photoId="local"))
            CloudSync(context,db).save("binding","folder",DriveFile("remote","new.jpg","image/jpeg",9,"second","2",listOf("folder")),"scan")
            assertNull(db.cloud().photo("binding:remote")?.photoId)
            assertEquals(photo,db.photos().find("local"))
        } finally { db.close() }
    }
    @Test fun metadataOnlyCloudChangesKeepOfflineFileAndAdvanceVerifiedVersion() = runBlocking {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).allowMainThreadQueries().build()
        try {
            val photo=Photo("local","photo.jpg","/original","/thumb","image/jpeg",8,1,"sha",sourceBindingKey="binding",sourceRemoteId="remote",cachedVersion="1")
            db.photos().insert(photo)
            db.cloud().savePhoto(CloudPhoto("binding:remote","binding","remote","folder","photo.jpg","image/jpeg",8,"same","1",null,null,null,null,photoId="local"))
            CloudSync(context,db).save("binding","folder",DriveFile("remote","renamed.jpg","image/jpeg",8,"same","2",listOf("folder")),"scan")
            assertEquals("local",db.cloud().photo("binding:remote")?.photoId)
            assertEquals("2",db.photos().find("local")?.cachedVersion)
            assertEquals("/original",db.photos().find("local")?.originalPath)
        } finally { db.close() }
    }

}

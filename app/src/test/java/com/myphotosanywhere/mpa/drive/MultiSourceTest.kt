package com.myphotosanywhere.mpa.drive

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.core.LibrarySettings
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.people.FaceMatching
import com.myphotosanywhere.mpa.people.PeopleRepository
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class MultiSourceTest {
    private lateinit var db: PhotoDatabase
    private val context get() = RuntimeEnvironment.getApplication()
    private fun source(account: String,folder: String) = DriveSource.from(DriveConnection(
        accountId=account,email="$account@example.test",mode=DriveMode.APP_FILES.name,folderId=folder,folderName=folder))
    @Before fun open() { db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).allowMainThreadQueries().build() }
    @After fun close() { db.close() }

    @Test fun browserSelectionDoesNotChangeTheSingleBackupDestinationOrExistingCheckpoint() = runBlocking {
        val a=source("alice","family"); val b=source("bob","travel")
        db.drive().saveSource(a); db.drive().saveSource(b)
        db.drive().chooseBackupDestination(a.key)
        val queued=DriveBackup("${a.key}:photo",a.key,"photo",a.accountId,a.email,a.folderId,a.mode,
            remoteId="reserved",sessionUri="upload-session",uploadedBytes=40)
        db.drive().insertBackup(queued)
        db.drive().saveConnection(b.connection())
        assertEquals(a.key,db.drive().backupDestination()?.key)
        assertEquals(setOf(a.key,b.key),db.drive().sources().map { it.key }.toSet())
        db.drive().chooseBackupDestination(b.key)
        val imported=Photo("photo","photo.jpg","/original","/thumb","image/jpeg",8,1,"sha")
        db.photos().insert(imported)
        DriveCoordinator(context,db).enqueue(imported)
        assertEquals(listOf(queued),db.drive().backupsForPhoto(imported.id))
        assertEquals(listOf(b.key),db.drive().sources().filter { it.backupEnabled }.map { it.key })
        assertEquals(queued,db.drive().backup(queued.id))
        try { db.drive().chooseBackupDestination("not-connected"); fail("Unknown source accepted") }
        catch(_: IllegalArgumentException) { }
        assertEquals(b.key,db.drive().backupDestination()?.key)
    }

    @Test fun removingOneSourcePreservesOtherSourcesAndGlobalCorrections() = runBlocking {
        val a=source("alice","family"); val b=source("bob","travel")
        db.drive().saveSource(a); db.drive().saveSource(b); db.drive().chooseBackupDestination(b.key)
        db.cloud().saveSync(SyncState(a.key,"token-a")); db.cloud().saveSync(SyncState(b.key,"token-b"))
        val download=DownloadTask("${b.key}:remote",b.key,"remote",true)
        db.cloud().saveDownload(download)
        db.library().savePerson(Person("shared-person","Sam"))
        db.library().saveFace(FaceRecord("face","photo","/face",null,personId="shared-person",corrected=true))
        db.drive().disconnectSource(a.key)
        assertNull(db.drive().source(a.key))
        assertNotNull(db.drive().source(b.key))
        assertEquals(b.key,db.drive().backupDestination()?.key)
        assertEquals("token-b",db.cloud().sync(b.key)?.changeToken)
        assertEquals(download,db.cloud().download(download.key))
        assertEquals("shared-person",db.library().face("face")?.personId)
        assertTrue(db.library().face("face")?.corrected==true)
    }

    @Test fun matchingUsesConfirmedExamplesFromEveryAccountAndRespectsRejectedSuggestions() = runBlocking {
        val a=source("alice","family"); val b=source("bob","travel")
        db.drive().saveSource(a); db.drive().saveSource(b)
        for((id,source) in listOf("reference" to a,"query" to b)) db.photos().insert(
            Photo(id,"$id.jpg","/original-$id","/thumb-$id","image/jpeg",8,1,"sha-$id",sourceBindingKey=source.key))
        val embedding=FaceMatching.encode(FloatArray(FaceMatching.DIMENSIONS) { if(it==0) 1f else 0f })
        db.library().savePerson(Person("sam","Sam"))
        db.library().saveFace(FaceRecord("reference-face","reference","/face-a",embedding,personId="sam",corrected=true))
        db.library().saveFace(FaceRecord("query-face","query","/face-b",embedding))
        db.library().saveFace(FaceRecord("rejected-face","query","/face-c",embedding,corrected=true))
        val settings=LibrarySettings(context)
        PeopleRepository(context,db,MediaCache(context,db,settings),settings).refreshSuggestions()
        assertEquals("sam",db.library().face("query-face")?.suggestedPersonId)
        assertNull(db.library().face("query-face")?.personId) // Confirmation remains a user decision.
        assertNull(db.library().face("rejected-face")?.suggestedPersonId)
    }

    @Test fun overlappingFoldersShareTheVerifiedLocalIdentityButOtherAccountsAndChangedBytesDoNot() = runBlocking {
        val parent=source("alice","parent"); val child=source("alice","child"); val other=source("bob","child")
        for(s in listOf(parent,child,other)) db.drive().saveSource(s)
        val photo=Photo("local","photo.jpg","/original","/thumb","image/jpeg",8,1,"sha",
            sourceBindingKey=parent.key,sourceRemoteId="remote",cachedVersion="1")
        db.photos().insert(photo)
        db.library().savePerson(Person("sam","Sam"))
        db.library().saveFace(FaceRecord("face",photo.id,"/face",null,personId="sam",corrected=true))
        db.cloud().savePhoto(CloudPhoto("${parent.key}:remote",parent.key,"remote","child","photo.jpg","image/jpeg",
            8,"verified","1",null,null,null,null,photoId=photo.id))
        val file=DriveFile("remote","photo.jpg","image/jpeg",8,"verified","1",listOf("child"))
        val sync=CloudSync(context,db)
        sync.save(child.key,"child",file,"scan")
        sync.save(other.key,"child",file,"scan")
        assertEquals(photo.id,db.cloud().photo("${child.key}:remote")?.photoId)
        assertNull(db.cloud().photo("${other.key}:remote")?.photoId)
        sync.save(child.key,"child",file.copy(md5="changed",version="2"),"next")
        assertNull(db.cloud().photo("${child.key}:remote")?.photoId)
        assertEquals(photo,db.photos().find(photo.id))
        assertEquals("sam",db.library().face("face")?.personId)
    }

    @Test fun aFreshSharedCopyCannotRelinkAStaleSnapshot() = runBlocking {
        val a=source("alice","parent"); val b=source("alice","child")
        db.drive().saveSource(a); db.drive().saveSource(b)
        for(id in listOf("old","fresh")) db.photos().insert(Photo(id,"photo.jpg","/$id","/thumb","image/jpeg",8,1,"sha-$id"))
        val old=CloudPhoto("${a.key}:remote",a.key,"remote","child","photo.jpg","image/jpeg",8,"old","1",null,null,null,null,photoId="old")
        db.cloud().savePhoto(old)
        db.cloud().savePhoto(old.copy(key="${b.key}:remote",bindingKey=b.key,md5="fresh",version="2",photoId="fresh"))
        CloudSync(context,db).save(a.key,"child",DriveFile("remote","photo.jpg","image/jpeg",8,"fresh","2",listOf("child")),"scan")
        assertEquals("fresh",db.cloud().photo(old.key)?.photoId)
    }
}

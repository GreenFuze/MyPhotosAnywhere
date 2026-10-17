package com.myphotosanywhere.mpa.device

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.trash.DriveTrashRecord
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
class LibraryPresenceTest {
    private lateinit var db: PhotoDatabase
    private val source=DriveSource.from(DriveConnection(accountId="account",email="a@test",mode="APP_FILES",folderId="folder",folderName="Folder"))
    private val photo=Photo("device","camera.jpg","/protected-original","content://media/external/file/1","image/jpeg",10,1,"a".repeat(64))
    private val task=DriveBackup("task",source.key,photo.id,source.accountId,source.email,source.folderId,source.mode,
        remoteId="remote",remoteMd5="1".repeat(32),remoteVersion="1",uploadedBytes=10)
    @Before fun setup()=runBlocking {
        db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).build()
        db.drive().saveSource(source);db.photos().insert(photo);db.drive().insertBackup(task)
        db.deviceMedia().authorize(ManualDeviceBackup(task.id,photo.id,source.key,photo.sha256,photo.bytes))
    }
    @After fun close() { db.close() }
    private suspend fun present()=db.presence().inDrive(photo.id).first()
    @Test fun pendingUploadsAndMismatchedCapturedProofNeverReceiveAnInDriveBadge()=runBlocking {
        assertFalse(present());db.drive().updateBackup(task.copy(state="UPLOADING"));assertFalse(present())
        db.drive().updateBackup(task.copy(state="COMPLETE"));assertTrue(present())
        db.photos().update(photo.copy(sha256="b".repeat(64)));assertFalse(present());db.photos().update(photo)
        db.drive().updateBackup(task.copy(state="COMPLETE",uploadedBytes=9));assertFalse(present())
        db.drive().updateBackup(task.copy(state="COMPLETE",accountId="another-account"));assertFalse(present())
        db.drive().updateBackup(task.copy(state="COMPLETE",remoteVersion=null));assertFalse(present())
        db.drive().updateBackup(task.copy(state="COMPLETE"));assertTrue(present())
        db.drive().saveSource(source.copy(enabled=false));assertFalse(present())
        assertEquals(photo,db.photos().find(photo.id))
    }
    @Test fun knownRemoteChangesDeletionAndDriveTrashOverrideAnOlderCompletedReceipt()=runBlocking {
        db.drive().updateBackup(task.copy(state="COMPLETE"));assertTrue(present())
        val remote=CloudPhoto("${source.key}:remote",source.key,"remote",source.folderId,"camera.jpg","image/jpeg",photo.bytes,
            task.remoteMd5,"1",null,null,null,null)
        db.cloud().savePhoto(remote);assertTrue(present())
        db.cloud().savePhoto(remote.copy(version="2"));assertFalse(present())
        db.cloud().savePhoto(remote.copy(removed=true));assertFalse(present())
        db.cloud().savePhoto(remote);assertTrue(present())
        val trash=DriveTrashRecord(remote.key,source.key,source.accountId,"remote",photo.id,photo.displayName,photo.mimeType,photo.bytes,
            task.remoteMd5,photo.sha256,"1",photo.thumbnailPath,photo.capturedAt,confirmed=true)
        db.trash().save(trash);assertFalse(present())
        db.trash().save(trash.copy(confirmed=false));assertTrue(present())
        assertEquals(photo,db.photos().find(photo.id));assertFalse(source.backupEnabled)
    }
}

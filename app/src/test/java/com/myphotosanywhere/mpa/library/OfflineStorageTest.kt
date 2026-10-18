package com.myphotosanywhere.mpa.library

import android.app.Application
import android.graphics.Bitmap
import androidx.room.Room
import com.myphotosanywhere.mpa.core.LibrarySettings
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File
import java.io.IOException

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class OfflineStorageTest {
    private class Fixture: AutoCloseable {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        val settings=LibrarySettings(context)
        private val prior=settings.state.value
        val files=mutableListOf<File>()
        init { settings.update(prior.copy(faceIndexing=false,objectIndexing=false,thumbnailBudget=0)) }
        suspend fun photo(id: String,local: Boolean=false,outside: Boolean=false): Photo {
            val directory=File(context.filesDir,if(outside) "camera" else "originals").apply { check(isDirectory || mkdirs()) }
            val file=File.createTempFile("offline-$id",".png",directory).also { files+=it }
            val preview=File(context.filesDir,"thumbnails/${file.name}").also { check(requireNotNull(it.parentFile).isDirectory || requireNotNull(it.parentFile).mkdirs());files+=it }
            val image=Bitmap.createBitmap(90,120,Bitmap.Config.ARGB_8888)
            try { image.eraseColor(android.graphics.Color.BLUE);file.outputStream().use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { image.recycle() }
            file.copyTo(preview)
            val photo=Photo(id,"$id.png",file.path,preview.path,"image/png",file.length(),123,DriveBackupWorker.digest(file,"SHA-256"),
                capturedAt=122,favorite=true,placeLabel="Preserved",labels="garden",sourceBindingKey=if(local) null else "source",sourceRemoteId=if(local) null else id,cachedVersion=if(local) null else "v1",importedMetadata="{\"keep\":true}")
            db.photos().insert(photo)
            if(!local) {
                db.drive().saveSource(DriveSource("source","account","storage@example.test","APP_FILES","folder","Folder"))
                db.cloud().savePhoto(CloudPhoto("source:$id","source",id,"folder",photo.displayName,photo.mimeType,photo.bytes,DriveBackupWorker.digest(file,"MD5"),"v1",null,null,null,null,photoId=id,sha256=photo.sha256))
            }
            return photo
        }
        fun repository(verify: suspend (Photo,File)->Boolean)=MediaCache(context,db,settings,verify).let { OfflineStorageRepository(context,db,it) }
        override fun close() { db.close();settings.update(prior);files.forEach { check(!it.exists() || it.delete()) } }
    }
    @Test fun clearContinuesAfterOneRemoteFailureAndPreservesPreviewsCameraPinsAndMetadata()=runBlocking<Unit> {
        Fixture().use { f ->
            val failed=f.photo("a-failed");val removed=f.photo("b-remove");val camera=f.photo("camera",outside=true);val local=f.photo("local",local=true)
            val pin=f.photo("pin").copy(pinned=true).also { f.db.photos().update(it) }
            val repository=f.repository { p,_ -> if(p.id==failed.id) throw IOException("Authorization expired") else true }
            val summary=repository.summary();assertEquals(2,summary.removable);assertEquals(failed.bytes+removed.bytes,summary.removableBytes)
            val result=repository.clear(summary.cutoff)
            assertEquals(1,result.removed);assertEquals(removed.bytes,result.freedBytes);assertEquals(1,result.failures.size)
            assertEquals(removed.copy(originalPath=""),f.db.photos().find(removed.id));assertTrue(File(removed.thumbnailPath).isFile)
            assertFalse(File(removed.originalPath).exists())
            for(p in listOf(failed,camera,local,pin)) { assertEquals(p,f.db.photos().find(p.id));assertTrue(File(p.originalPath).isFile);assertTrue(File(p.thumbnailPath).isFile) }
        }
    }
    @Test fun aDownloadUsedDuringDriveVerificationIsRetained()=runBlocking<Unit> {
        Fixture().use { f ->
            val photo=f.photo("recent")
            val repository=f.repository { p,_ -> f.db.photos().update(p.copy(lastAccess=Long.MAX_VALUE));true }
            val estimate=repository.summary();val result=repository.clear(estimate.cutoff)
            assertEquals(0,result.removed);assertEquals(1,result.retained);assertTrue(File(photo.originalPath).isFile)
            assertEquals(photo.originalPath,f.db.photos().find(photo.id)?.originalPath)
        }
    }
    @Test fun cancellationPropagatesAndKeepsUnverifiedOriginals()=runBlocking<Unit> {
        Fixture().use { f ->
            val photo=f.photo("cancel")
            val repository=f.repository { _,_ -> throw CancellationException("Stopped") }
            val estimate=repository.summary()
            try { repository.clear(estimate.cutoff);fail("Cancellation must propagate") } catch(_: CancellationException) { }
            assertEquals(photo,f.db.photos().find(photo.id));assertTrue(File(photo.originalPath).isFile);assertTrue(File(photo.thumbnailPath).isFile)
        }
    }
}

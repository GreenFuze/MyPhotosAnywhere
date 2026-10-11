package com.myphotosanywhere.mpa.importing

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.drive.*
import kotlinx.coroutines.runBlocking
import okhttp3.mockwebserver.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ImportTransferTest {
    private val remote="""{"id":"reserved","name":"photo.jpg","mimeType":"image/jpeg","size":"4","md5Checksum":"08d6c05a21512a79a1dfeb9d2a8f262f","version":"1","parents":["folder"]}"""
    @Test fun resumePartial() = runBlocking<Unit> {
        val context=RuntimeEnvironment.getApplication(); val name="resume-${java.util.UUID.randomUUID()}.db"
        val file=File(context.cacheDir,"resume-bytes").apply { writeBytes(byteArrayOf(1,2,3,4)) }
        val server=MockWebServer(); server.start()
        var db=Room.databaseBuilder(context,PhotoDatabase::class.java,name).allowMainThreadQueries().build()
        try {
            val item=ImportItem("item","job","uri","photo.jpg","photo.jpg","image/jpeg",4,
                sha256=DriveBackupWorker.digest(file,"SHA-256"),md5=DriveBackupWorker.digest(file,"MD5"),staged=file.absolutePath)
            db.imports().saveJob(ImportJob("job","binding","TAKEOUT")); db.imports().insertItem(item)
            server.enqueue(MockResponse().setBody("{\"ids\":[\"reserved\"]}"))
            server.enqueue(MockResponse().setResponseCode(404))
            server.enqueue(MockResponse().setHeader("Location",server.url("/upload?upload_id=x")))
            server.enqueue(MockResponse().setResponseCode(308).setHeader("Range","bytes=0-1"))
            val transfer=ImportTransfer(DriveGateway("test",api=server.url("/drive/"),upload=server.url("/upload")))
            try { transfer.upload(item,"folder",{ checkpoint ->
                db.imports().updateItem(checkpoint)
                if(checkpoint.offset==2L) throw java.io.IOException("simulated death")
            },{}); fail() } catch(_: java.io.IOException) { }
            assertEquals(2L,db.imports().item("item")?.offset)
            db.close(); db=Room.databaseBuilder(context,PhotoDatabase::class.java,name).allowMainThreadQueries().build()
            server.enqueue(MockResponse().setResponseCode(404))
            server.enqueue(MockResponse().setResponseCode(308).setHeader("Range","bytes=0-1"))
            server.enqueue(MockResponse().setResponseCode(200))
            server.enqueue(MockResponse().setBody(remote))
            val result=transfer.upload(requireNotNull(db.imports().item("item")),"folder",{ db.imports().updateItem(it) },{})
            assertEquals("VERIFIED",result.first.state); assertEquals("reserved",result.first.remoteId)
            repeat(6) { server.takeRequest() }
            val resumed=server.takeRequest(); assertEquals("bytes 2-3/4",resumed.getHeader("Content-Range"))
            assertArrayEquals(byteArrayOf(3,4),resumed.body.readByteArray())
        } finally { db.close(); server.shutdown(); context.deleteDatabase(name); file.delete() }
    }
    @Test fun resumeCompleted() = runBlocking<Unit> {
        val context=RuntimeEnvironment.getApplication()
        val name="imp-${java.util.UUID.randomUUID()}.db"
        val file=File(context.cacheDir,"bytes").apply { writeBytes(byteArrayOf(1,2,3,4)) }
        val server=MockWebServer(); server.start()
        var db=Room.databaseBuilder(context,PhotoDatabase::class.java,name).allowMainThreadQueries().build()
        try {
            val item=ImportItem("item","job","uri","photo.jpg","photo.jpg","image/jpeg",4,
                sha256=DriveBackupWorker.digest(file,"SHA-256"),md5=DriveBackupWorker.digest(file,"MD5"),staged=file.absolutePath,
                remoteId="reserved",state="UPLOADING",sessionUri=server.url("/upload?upload_id=x").toString())
            db.imports().saveJob(ImportJob("job","binding","TAKEOUT")); db.imports().insertItem(item)
            db.close(); db=Room.databaseBuilder(context,PhotoDatabase::class.java,name).allowMainThreadQueries().build()
            server.enqueue(MockResponse().setBody(remote)); server.enqueue(MockResponse().setBody(remote))
            val result=ImportTransfer(DriveGateway("test",api=server.url("/drive/"),upload=server.url("/upload")))
                .upload(requireNotNull(db.imports().item("item")),"folder",{ db.imports().updateItem(it) },{})
            assertEquals("VERIFIED",result.first.state); assertEquals(4L,db.imports().item("item")?.offset)
            assertEquals(2,server.requestCount)
            repeat(2) { assertEquals("GET",server.takeRequest().method) }
            assertTrue(file.isFile)
        } finally { db.close(); server.shutdown(); context.deleteDatabase(name); file.delete() }
    }
    @Test fun rejectMismatch() = runBlocking<Unit> {
        val file=File(RuntimeEnvironment.getApplication().cacheDir,"mismatch").apply { writeBytes(byteArrayOf(1,2,3,4)) }
        val server=MockWebServer(); server.start()
        try {
            val item=ImportItem("id","job","uri","photo.jpg","photo.jpg","image/jpeg",4,staged=file.absolutePath,
                sha256=DriveBackupWorker.digest(file,"SHA-256"),md5=DriveBackupWorker.digest(file,"MD5"),remoteId="reserved")
            repeat(2) { server.enqueue(MockResponse().setBody(remote.replace("\"4\"","\"7\""))) }
            try { ImportTransfer(DriveGateway("test",api=server.url("/drive/"),upload=server.url("/upload"))).upload(item,"folder",{},{}); fail() }
            catch(_: IllegalArgumentException) { }
            assertEquals(2,server.requestCount); repeat(2) { assertEquals("GET",server.takeRequest().method) }
            assertTrue(file.isFile)
        } finally { server.shutdown(); file.delete() }
    }
    @Test fun rejectMediaCleanup() = runBlocking<Unit> {
        val server=MockWebServer(); server.start()
        try {
            try { DriveGateway("test",api=server.url("/drive/")).trashTakeoutJson(DriveFile("photo","photo.jpg","image/jpeg"),"etag"); fail() }
            catch(_: IllegalArgumentException) { }
            assertEquals(0,server.requestCount)
        } finally { server.shutdown() }
    }
}

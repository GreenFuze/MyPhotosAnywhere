package com.myphotosanywhere.mpa.drive

import androidx.room.Room
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.library.GalleryVisibility
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
import java.io.File

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=MpaApplication::class)
class CloudDeletionTest {
    @Test fun driveTrashRemovalAndMoveHideCachedEntriesAndCheckpointWithoutDeletingFiles() = runBlocking<Unit> {
        val app=RuntimeEnvironment.getApplication() as MpaApplication
        val db=Room.inMemoryDatabaseBuilder(app,PhotoDatabase::class.java).allowMainThreadQueries().build()
        val server=MockWebServer().apply { start() }
        val file=File.createTempFile("preserved",".jpg",app.cacheDir).apply { writeText("original bytes") }
        try {
            val source=DriveSource.from(DriveConnection(accountId="test",email="test@test",mode="EXISTING_LIBRARY",folderId="folder",folderName="Test"))
            db.drive().saveSource(source);db.cloud().saveFolder(CloudFolder(source.key,"folder",null,"Test","g"))
            db.cloud().saveSync(SyncState(source.key,"before"))
            for(id in listOf("trashed","removed","moved")) {
                db.photos().insert(Photo(id,"$id.jpg",file.absolutePath,"/thumb","image/jpeg",14,1,"sha-$id",
                    sourceBindingKey=source.key,sourceRemoteId=id))
                db.cloud().savePhoto(CloudPhoto("${source.key}:$id",source.key,id,"folder","$id.jpg","image/jpeg",14,"md5","1",null,null,null,null,photoId=id))
            }
            server.enqueue(MockResponse().setBody("""{"newStartPageToken":"after","changes":[
                {"fileId":"trashed","file":{"id":"trashed","mimeType":"image/jpeg","trashed":true,"parents":["folder"]}},
                {"fileId":"removed","removed":true},
                {"fileId":"moved","file":{"id":"moved","mimeType":"image/jpeg","parents":["elsewhere"]}}]}"""))
            val api=DriveGateway("test",OkHttpClient(),server.url("/drive/v3/"),server.url("/upload/drive/v3/files"))
            CloudSync(app,db).reconcile(api,source.connection(),"before")
            assertEquals("after",db.cloud().sync(source.key)?.changeToken)
            val rows=db.cloud().photos(source.key);assertTrue(rows.all { it.removed })
            val presence=GalleryVisibility.Index(rows,setOf(source.key))
            for(photo in db.photos().all()) { assertFalse(presence.showLocal(photo,"All"));assertTrue(presence.showLocal(photo,"Offline")) }
            assertEquals("original bytes",file.readText())
            assertEquals(3,db.photos().all().size)
        } finally { server.shutdown();db.close();file.delete() }
    }
}

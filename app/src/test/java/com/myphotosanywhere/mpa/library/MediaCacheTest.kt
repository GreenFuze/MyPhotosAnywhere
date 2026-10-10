package com.myphotosanywhere.mpa.library

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.core.LibrarySettings
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.CloudPhoto
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class MediaCacheTest {
    @Test fun zeroBudgetPreservesImportsDraftsPinsAndActiveViewsButEvictsVerifiedDownloads() = runBlocking {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).allowMainThreadQueries().build()
        val settings=LibrarySettings(context).apply { update(state.value.copy(originalBudget=0)) }
        val files=mutableListOf<File>()
        try {
            for(id in listOf("import","draft","pin","reader","evict")) {
                val file=File.createTempFile("cache-$id",".jpg",context.filesDir);files.add(file);file.writeText("original")
                val photo=Photo(id,"$id.jpg",file.absolutePath,"${file.absolutePath}.thumb","image/jpeg",8,1,"hash",
                    sourceBindingKey=if(id=="import") null else "binding",sourceRemoteId=if(id=="import") null else id,
                    cachedVersion="1",pinned=id=="pin")
                db.photos().insert(photo)
                if(id!="import") db.cloud().savePhoto(CloudPhoto("binding:$id","binding",id,"folder","$id.jpg","image/jpeg",8,
                    "md5","1",null,null,null,null,photoId=id))
            }
            db.library().saveDraft(EditDraft("draft","recipe",1))
            val cache=MediaCache(context,db,settings) { _,_ -> true }
            val reader=cache.acquireReader("reader")
            try {
                cache.enforce()
                for(id in listOf("import","draft","pin","reader")) {
                    assertTrue(File(requireNotNull(db.photos().find(id)).originalPath).exists())
                }
                assertEquals("",db.photos().find("evict")?.originalPath)
                assertFalse(files.last().exists())
                assertNotNull(db.photos().find("evict")) // The browsable index survives eviction.
            } finally { reader.close() }
        } finally { db.close();files.forEach { it.delete() } }
    }
    @Test fun failedRemoteVerificationKeepsAnUnpinnedDownloadAndItsPath() = runBlocking {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).allowMainThreadQueries().build()
        val file=File.createTempFile("offline-cache",".jpg",context.filesDir)
        try {
            file.writeText("original")
            val photo=Photo("id","photo.jpg",file.absolutePath,"${file.absolutePath}.thumb","image/jpeg",8,1,"hash",
                sourceBindingKey="binding",sourceRemoteId="remote",cachedVersion="1")
            db.photos().insert(photo)
            db.cloud().savePhoto(CloudPhoto("binding:remote","binding","remote","folder","photo.jpg","image/jpeg",8,
                "md5","1",null,null,null,null,photoId="id"))
            val settings=LibrarySettings(context).apply { update(state.value.copy(originalBudget=0)) }
            MediaCache(context,db,settings) { _,_ -> false }.enforce()
            assertEquals(file.absolutePath,db.photos().find("id")?.originalPath);assertTrue(file.exists())
        } finally { db.close();file.delete() }
    }
    @Test fun originalsAwaitingAnEnabledScanAreProtectedUntilTheModelCheckpointExists() = runBlocking {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        val file=File.createTempFile("scan-cache",".jpg",context.filesDir)
        try {
            file.writeText("original")
            val photo=Photo("pending","photo.jpg",file.absolutePath,"","image/jpeg",8,1,"hash",
                sourceBindingKey="source",sourceRemoteId="remote",cachedVersion="1")
            db.photos().insert(photo)
            db.cloud().savePhoto(CloudPhoto("source:remote","source","remote","folder","photo.jpg","image/jpeg",8,"md5","1",null,null,null,null,photoId=photo.id))
            val settings=LibrarySettings(context).apply { update(state.value.copy(originalBudget=0,faceIndexing=true)) }
            val cache=MediaCache(context,db,settings) { _,_ -> true }
            cache.enforce(); assertTrue(file.isFile)
            db.library().saveScan(FaceScan(photo.id,photo.sha256,modelVersion=com.myphotosanywhere.mpa.people.PeopleRepository.SCAN_VERSION))
            cache.enforce(); assertFalse(file.isFile); assertEquals("",db.photos().find(photo.id)?.originalPath)
        } finally { db.close(); file.delete() }
    }

}

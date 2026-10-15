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
    @Test fun validatedReadsProtectTheCurrentFileAndDoubleCloseCannotReleaseAnotherView() = runBlocking<Unit> {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        val file=File.createTempFile("validated-cache",".jpg",context.filesDir)
        try {
            file.writeText("original")
            val photo=Photo("validated","photo.jpg",file.absolutePath,"","image/jpeg",8,1,"hash",
                sourceBindingKey="binding",sourceRemoteId="remote",cachedVersion="1")
            db.photos().insert(photo)
            db.cloud().savePhoto(CloudPhoto("binding:remote","binding","remote","folder","photo.jpg","image/jpeg",8,
                "md5","1",null,null,null,null,photoId=photo.id))
            val settings=LibrarySettings(context).apply { update(state.value.copy(originalBudget=0,faceIndexing=false,objectIndexing=false)) }
            val cache=MediaCache(context,db,settings) { _,_ -> true }
            val first=requireNotNull(cache.openOriginal(photo.id));val second=requireNotNull(cache.openOriginal(photo.id))
            assertEquals(file,first.file)
            first.close();first.close();cache.enforce()
            assertTrue(file.isFile);assertEquals(file.absolutePath,db.photos().find(photo.id)?.originalPath)
            second.close();cache.enforce()
            assertFalse(file.exists());assertEquals("",db.photos().find(photo.id)?.originalPath)
            assertNull(cache.openOriginal(photo.id));assertNull(cache.openOriginal("missing"))
        } finally { db.close();file.delete() }
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

    @Test fun previewCleanupAndCacheTouchesNeverDeleteOrRetimestampLocalOriginals()=runBlocking<Unit> {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        val original=File.createTempFile("shared-original-preview",".jpg",context.filesDir)
        val second=File.createTempFile("second-local-original",".jpg",context.filesDir)
        val derived=File.createTempFile("disposable-preview",".jpg",context.cacheDir)
        val settings=LibrarySettings(context);val previous=settings.state.value
        try {
            original.writeText("local original one");second.writeText("local original two");derived.writeText("derived preview")
            check(original.setLastModified(1_600_000_000_000L));val stamp=original.lastModified()
            val first=Photo("shared-original","One.jpg",original.path,original.path,"image/jpeg",original.length(),1,"first")
            val other=Photo("second-original","Two.jpg",second.path,derived.path,"image/jpeg",second.length(),1,"second")
            db.photos().insert(first);db.photos().insert(other)
            db.cloud().savePhoto(CloudPhoto("source:preview","source","preview","folder","Preview.jpg","image/jpeg",original.length(),null,null,null,null,null,null,thumbnailPath=original.path))
            settings.update(previous.copy(originalBudget=0,thumbnailBudget=0,faceIndexing=false,objectIndexing=false))
            val cache=MediaCache(context,db,settings) { _,_ -> throw AssertionError("A local-only original cannot be evicted") }
            assertEquals(original,cache.thumbnail(first));assertEquals(stamp,original.lastModified())
            cache.openThumbnailOriginal(first.id)?.use { assertEquals(original,it.file) } ?: fail("Expected protected thumbnail reader")
            assertEquals(first,db.photos().find(first.id))
            cache.enforce(clear=true)
            assertEquals("local original one",original.readText());assertEquals("local original two",second.readText())
            assertEquals(stamp,original.lastModified());assertFalse(derived.exists())
            assertEquals(first,db.photos().find(first.id));assertEquals(other,db.photos().find(other.id))
        } finally { settings.update(previous);db.close();original.delete();second.delete();derived.delete() }
    }

}

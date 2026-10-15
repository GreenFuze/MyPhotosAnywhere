package com.myphotosanywhere.mpa.data

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.core.LibrarySettings
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
class ThumbnailAvailabilityTest {
    @Test fun missingOriginalIsRecoverableAndOnlyItsVerifiedLinkedPreviewMayBeUsed()=runBlocking<Unit> {
        val context=RuntimeEnvironment.getApplication()
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        val missing=File(context.filesDir,"missing-original-fixture.jpg")
        val missingPreview=File(context.filesDir,"missing-preview-fixture.jpg")
        val preview=File.createTempFile("linked-preview",".jpg",context.cacheDir)
        preview.writeText("cached preview bytes")
        val photo=Photo("missing-original-fixture","Photo.jpg",missing.path,missingPreview.path,"image/jpeg",123,100,"original-sha",
            sourceBindingKey="source",sourceRemoteId="remote",cachedVersion="1")
        val cloud=CloudPhoto("source:remote","source","remote","folder","Photo.jpg","image/jpeg",123,null,"1",null,null,null,null,
            thumbnailPath=preview.path,photoId=photo.id)
        try {
            assertFalse(missing.exists());assertFalse(missingPreview.exists())
            db.photos().insert(photo)
            val cache=MediaCache(context,db,LibrarySettings(context))
            assertNull(cache.thumbnail(photo))
            assertEquals(photo,db.photos().find(photo.id))
            db.cloud().savePhoto(cloud)
            assertEquals(preview,cache.thumbnail(photo))
            for(other in listOf(cloud.copy(photoId="different-photo"),cloud.copy(bytes=999),cloud.copy(version="2"),cloud.copy(sha256="different-sha"))) {
                db.cloud().savePhoto(other);assertNull(cache.thumbnail(photo))
            }
            assertEquals(photo,db.photos().find(photo.id))
            assertEquals("cached preview bytes",preview.readText())
            assertFalse(missing.exists());assertFalse(missingPreview.exists())
        } finally { db.close();preview.delete() }
    }
}

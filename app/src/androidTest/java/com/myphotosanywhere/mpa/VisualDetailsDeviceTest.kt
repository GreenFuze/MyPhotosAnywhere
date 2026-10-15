package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import androidx.exifinterface.media.ExifInterface
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.library.PhotoFactsRepository
import com.myphotosanywhere.mpa.timeline.TimestampProvenance
import com.myphotosanywhere.mpa.drive.CloudPhoto
import com.myphotosanywhere.mpa.drive.DriveSource
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class VisualDetailsDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication

    @Test fun fileFactsUseVerifiedLinksAndBoundsWithoutChangingMetadata()=runBlocking<Unit> {
        val file=File.createTempFile("facts-rotation",".jpg",app.filesDir)
        val bitmap=Bitmap.createBitmap(240,360,Bitmap.Config.ARGB_8888)
        try { file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.JPEG,90,it)) } } finally { bitmap.recycle() }
        ExifInterface(file).apply { setAttribute(ExifInterface.TAG_ORIENTATION,ExifInterface.ORIENTATION_ROTATE_90.toString());saveAttributes() }
        val photo=Photo("facts-local","facts.jpg",file.path,file.path,"image/jpeg",file.length(),2_000_000_000_000L,"facts",
            sourceBindingKey="facts-source",sourceRemoteId="facts-file")
        val source=DriveSource("facts-source","facts-account","facts@test","APP_FILES","facts-folder","Facts")
        val exact=CloudPhoto(key="facts-source:facts-file",bindingKey=source.key,remoteId="facts-file",parentId="facts-folder",
            name="facts.jpg",mimeType="image/jpeg",bytes=file.length(),md5=null,version="1",modifiedTime=null,capturedAt=1_900_000_000_000L,
            latitude=null,longitude=null,photoId=photo.id)
        try {
            app.database.drive().saveSource(source);app.database.photos().insert(photo);app.database.cloud().savePhoto(exact)
            app.database.cloud().savePhoto(exact.copy(key="a-alias:facts-alias",bindingKey="a-alias",remoteId="facts-alias",capturedAt=1_700_000_000_000L))
            val repository=PhotoFactsRepository(app.timeline,app.cache)
            val facts=repository.read(photo)
            assertEquals(exact.capturedAt,facts.millis);assertEquals(TimestampProvenance.LINKED_CAPTURE,facts.provenance)
            assertEquals(360 to 240,facts.dimensions)
            assertEquals(photo,app.database.photos().find(photo.id)?.copy(lastAccess=photo.lastAccess))
            val bytes=file.readBytes()
            app.database.photos().update(photo.copy(capturedAt=1_800_000_000_000L))
            assertEquals(TimestampProvenance.CAPTURE,repository.read(requireNotNull(app.database.photos().find(photo.id))).provenance)
            assertArrayEquals(bytes,file.readBytes())
            app.database.photos().update(photo.copy(originalPath=""))
            assertNull(repository.read(photo.copy(originalPath="")).dimensions)
            assertTrue(file.isFile)
        } finally {
            app.database.photos().find(photo.id)?.let { app.database.photos().update(it.copy(archived=true,originalPath="")) }
            app.database.drive().disconnectSource(source.key);file.delete()
        }
    }

    @Test fun collectionShortcutsAndDetailsAreVisualAndReachable() {
        compose.onNodeWithTag("nav:Collections").performClick()
        compose.waitUntil(10000) { compose.onAllNodesWithTag("collections:Offline available").fetchSemanticsNodes().isNotEmpty() }
        for(title in listOf("Favorites","Trash","Archive","Offline available")) compose.onNodeWithTag("collections:$title").assertIsDisplayed()
        screenshot("collections-compact")
        compose.onNodeWithTag("collections:Archive").performClick()
        compose.onNodeWithTag("nav:Collections").performClick()
        compose.onNodeWithTag("collections:People").performScrollTo().performClick()
        compose.onNodeWithText("Imported people").assertExists()
        compose.onNodeWithText("Named faces").performScrollTo().assertIsDisplayed()
        screenshot("people-visual")
    }

    private fun screenshot(name: String) {
        val bitmap=requireNotNull(InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot())
        try {
            val values=android.content.ContentValues().apply {
                put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"$name.png")
                put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png")
                put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
            }
            val uri=requireNotNull(app.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
            requireNotNull(app.contentResolver.openOutputStream(uri)).use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) }
        } finally { bitmap.recycle() }
    }
}

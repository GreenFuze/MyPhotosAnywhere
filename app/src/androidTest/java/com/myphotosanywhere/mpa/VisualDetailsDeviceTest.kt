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
            val repository=PhotoFactsRepository(app.timeline,app.cache,app.deviceMedia)
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
        compose.onNodeWithTag("people-list").assertExists()
        compose.waitUntil(10000) {
            compose.onAllNodes(hasTestTag("people-list") and SemanticsMatcher.expectValue(androidx.compose.ui.semantics.SemanticsProperties.StateDescription,"People ready")).fetchSemanticsNodes().size==1
        }
        compose.onAllNodesWithText("Imported people").assertCountEquals(0)
        compose.onAllNodesWithText("Named faces").assertCountEquals(0)
        compose.onNodeWithTag("people-list").performScrollToNode(hasText("Unassigned faces",substring=true))
        compose.onNodeWithText("Unassigned faces",substring=true).assertIsDisplayed()
        screenshot("people-visual")
    }

    @Test fun albumMenuReflectsPinChangesAndRemovalKeepsOriginal()=runBlocking<Unit> {
        val file=File.createTempFile("album-menu",".png",app.filesDir)
        val bitmap=Bitmap.createBitmap(160,240,Bitmap.Config.ARGB_8888)
        try { file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { bitmap.recycle() }
        val hash=com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(file,"SHA-256")
        val photo=Photo("visual-album-photo","Album photo.png",file.path,file.path,"image/png",file.length(),4_000_000_000_000L,hash)
        val album=com.myphotosanywhere.mpa.data.Album("visual-album-menu","Visual album",4_000_000_000_000L)
        fun saved()=runBlocking { app.database.library().allAlbums().find { it.id==album.id } }
        try {
            app.database.photos().insert(photo);app.database.library().saveAlbum(album)
            app.database.library().addToAlbum(com.myphotosanywhere.mpa.data.AlbumPhoto(album.id,photo.id))
            compose.onNodeWithTag("nav:Collections").performClick()
            val model=compose.runOnUiThread { androidx.lifecycle.ViewModelProvider(compose.activity)[com.myphotosanywhere.mpa.library.LibraryFeaturesModel::class.java] }
            compose.waitUntil(10000) { model.albums.value.any { it.id==album.id } }
            compose.onNodeWithTag("collections:grid").performScrollToNode(hasTestTag("album:manage:${album.id}"))
            compose.onNodeWithTag("album:manage:${album.id}").performClick()
            compose.onNodeWithText("Keep offline").performClick()
            compose.waitUntil(10000) { saved()?.pinned==true && !model.busy.value && compose.onAllNodesWithText("Unpin offline album").fetchSemanticsNodes().isNotEmpty() }
            screenshot("album-management")
            compose.onNodeWithText("Album name").performTextReplacement("Renamed visual album")
            compose.onNodeWithText("Save").performClick()
            compose.waitUntil(10000) { !model.busy.value && model.albums.value.any { it.id==album.id && it.name=="Renamed visual album" && it.pinned } }
            assertEquals(album.copy(name="Renamed visual album",pinned=true),saved())
            assertEquals(listOf(com.myphotosanywhere.mpa.data.AlbumPhoto(album.id,photo.id)),app.database.library().members(album.id))
            compose.waitUntil(10000) {
                compose.runOnUiThread { androidx.core.view.ViewCompat.getRootWindowInsets(compose.activity.window.decorView)?.isVisible(androidx.core.view.WindowInsetsCompat.Type.ime())!=true }
            }
            compose.onNodeWithTag("collections:grid").performScrollToNode(hasTestTag("album:manage:${album.id}"))
            compose.onNodeWithTag("album:manage:${album.id}").assertIsDisplayed().performClick()
            compose.waitUntil(10000) { compose.onAllNodesWithText("Unpin offline album").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithText("Unpin offline album").performClick()
            compose.waitUntil(10000) { saved()?.pinned==false && !model.busy.value && compose.onAllNodesWithText("Keep offline").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithText("Remove album").performClick()
            compose.onNodeWithText("Confirm removal; keep photos").performClick()
            compose.waitUntil(10000) { saved()==null }
            assertTrue(app.database.library().members(album.id).isEmpty())
            assertEquals(photo,app.database.photos().find(photo.id)?.copy(lastAccess=photo.lastAccess))
            assertTrue(file.isFile);assertEquals(hash,com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(file,"SHA-256"))
        } finally {
            app.database.library().clearAlbum(album.id);app.database.library().deleteAlbum(album.id)
            app.database.photos().find(photo.id)?.let { app.database.photos().update(it.copy(archived=true,originalPath="")) };file.delete()
        }
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

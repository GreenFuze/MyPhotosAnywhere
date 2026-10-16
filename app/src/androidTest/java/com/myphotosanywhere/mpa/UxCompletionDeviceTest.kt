package com.myphotosanywhere.mpa

import android.content.pm.ActivityInfo
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.activity.compose.setContent
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.lifecycle.ViewModelProvider
import androidx.room.Room
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.importing.*
import com.myphotosanywhere.mpa.library.LibraryFeaturesModel
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class UxCompletionDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private val app get()=instrumentation.targetContext.applicationContext as MpaApplication
    private fun waitTag(tag: String)=compose.waitUntil(30000) { compose.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty() }
    private inner class Fixture(val name: String): AutoCloseable {
        val id="ux-$name"
        val source=DriveSource("$id-source","$id-account","ux@test","APP_FILES","$id-folder","UX $name")
        val file=File(app.filesDir,"originals/$id")
        val preview=File(app.filesDir,"thumbnails/$id.png")
        val photo: Photo
        val cloud: CloudPhoto
        init {
            requireNotNull(file.parentFile).mkdirs();requireNotNull(preview.parentFile).mkdirs()
            val bitmap=Bitmap.createBitmap(320,480,Bitmap.Config.ARGB_8888)
            try { bitmap.eraseColor(android.graphics.Color.rgb(24,120,170));file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } }
            finally { bitmap.recycle() }
            file.copyTo(preview,overwrite=true)
            photo=Photo(id,"$id.png",file.path,preview.path,"image/png",file.length(),4_000_000_000_000L,
                DriveBackupWorker.digest(file,"SHA-256"),capturedAt=3_999_999_000_000L,sourceBindingKey=source.key,sourceRemoteId="remote",cachedVersion="1",pinned=true)
            cloud=CloudPhoto("${source.key}:remote",source.key,"remote",source.folderId,photo.displayName,photo.mimeType,photo.bytes,
                DriveBackupWorker.digest(file,"MD5"),"1",null,photo.capturedAt,null,null,photoId=id,sha256=photo.sha256)
        }
        suspend fun insert() { app.database.drive().saveSource(source);app.database.photos().insert(photo);app.database.cloud().savePhoto(cloud) }
        fun open() {
            compose.onNodeWithTag("shell:filters").performClick()
            compose.onNodeWithText("UX $name · ux@test").performScrollTo().performClick()
            compose.onNodeWithText("Done").performScrollTo().performClick()
            waitTag("photo:$id");compose.onNodeWithTag("photo:$id").performClick();waitTag("viewer-screen")
        }
        override fun close() {
            runBlocking {
                app.database.photos().find(id)?.let { app.database.photos().update(it.copy(archived=true,originalPath="")) }
                app.database.drive().disconnectSource(source.key)
            }
            file.delete();preview.delete()
        }
    }

    @Test fun removeDownloadConfirmationSurvivesRecreationCancelKeepsViewerAndPinBlocksDeletion()=runBlocking<Unit> {
        Fixture("protected-copy").use { fixture ->
            fixture.insert();fixture.open()
            compose.onNodeWithTag("viewer-details").performClick();waitTag("details:remove-copy")
            compose.onNodeWithTag("details:remove-copy").performScrollTo().performClick();waitTag("device-copy:cancel")
            screenshot("device-copy-confirmation")
            compose.activityRule.scenario.recreate();waitTag("device-copy:cancel")
            compose.onNodeWithTag("device-copy:cancel").performClick();waitTag("photo-details")
            compose.onNodeWithTag("viewer-screen").assertExists();assertTrue(fixture.file.isFile)
            compose.onNodeWithTag("details:remove-copy").performScrollTo().performClick()
            compose.onNodeWithTag("device-copy:confirm").performClick()
            val model=compose.runOnUiThread { ViewModelProvider(compose.activity)[LibraryFeaturesModel::class.java] }
            compose.waitUntil(10000) { !model.busy.value && model.message.value?.contains("kept offline")==true }
            compose.onNodeWithTag("viewer-screen").assertDoesNotExist()
            assertTrue(fixture.file.isFile);assertEquals(fixture.photo.sha256,DriveBackupWorker.digest(fixture.file,"SHA-256"))
            val retained=requireNotNull(app.database.photos().find(fixture.id))
            assertEquals(fixture.photo,retained.copy(lastAccess=fixture.photo.lastAccess))
            assertEquals(fixture.cloud,app.database.cloud().photo(fixture.cloud.key))
        }
    }

    @Test fun nativeRemovalRegeneratesCorruptPreviewOnlyAfterVerifiedRemoteAndPreservesAnnotations()=runBlocking<Unit> {
        Fixture("preview-regeneration").use { fixture ->
            val db=Room.inMemoryDatabaseBuilder(app,PhotoDatabase::class.java).build()
            val previous=app.settings.state.value
            try {
                app.settings.update(previous.copy(faceIndexing=false,objectIndexing=false))
                val photo=fixture.photo.copy(pinned=false,placeLabel="Reviewed place",favorite=true)
                db.drive().saveSource(fixture.source);db.photos().insert(photo);db.cloud().savePhoto(fixture.cloud)
                fixture.preview.writeText("corrupt preview")
                val cache=MediaCache(app,db,app.settings) { _,file -> file.length()==photo.bytes }
                cache.removeDownloadedCopy(requireNotNull(DeviceCopyIdentity.from(photo)))
                val retained=requireNotNull(db.photos().find(photo.id))
                assertFalse(fixture.file.exists());assertEquals(photo.copy(originalPath="",thumbnailPath=retained.thumbnailPath),retained)
                val preview=File(retained.thumbnailPath)
                val bitmap=BitmapFactory.decodeFile(preview.path)
                try { assertNotNull(bitmap);assertTrue(requireNotNull(bitmap).width<=480 && bitmap.height<=480) }
                finally { bitmap?.recycle() }
                assertEquals(fixture.cloud,db.cloud().photo(fixture.cloud.key));preview.delete()
            } finally { app.settings.update(previous);db.close() }
        }
    }

    @Test fun savedMetadataRepairIsReachableInHistoryAndDoesNotRestartVerifiedUpload()=runBlocking<Unit> {
        Fixture("saved-labels").use { fixture ->
            fixture.insert()
            val job=ImportJob("ux-saved-metadata",fixture.source.key,"TAKEOUT",state="COMPLETE")
            val item=ImportItem("ux-saved-item",job.id,"retained","photo.png","photo.png","image/png",fixture.photo.bytes,
                metadata="""{"title":"photo.png","photoTakenTime":{"timestamp":"12"},"people":[{"name":"Imported person"}]}""",
                state="COMPLETE",sha256=fixture.photo.sha256,remoteId=fixture.photo.sourceRemoteId,offset=fixture.photo.bytes,photoId=fixture.id)
            app.database.imports().saveJob(job);app.database.imports().insertItem(item)
            val model=compose.runOnUiThread { ViewModelProvider(compose.activity)[ImportModel::class.java] }
            compose.runOnUiThread { compose.activity.setContent { MaterialTheme { androidx.compose.foundation.layout.Column { ImportControls(ImportSurface.HISTORY,model) } } } }
            waitTag("import:details:${job.id}");compose.onNodeWithTag("import:details:${job.id}").performClick()
            waitTag("repair-metadata:${job.id}");compose.onNodeWithTag("repair-metadata:${job.id}").performClick()
            compose.waitUntil(10000) { model.metadataRepair.value!=null && !model.busy.value }
            assertEquals(1,requireNotNull(model.metadataRepair.value).applied)
            screenshot("saved-metadata-repair")
            compose.onNodeWithText("Close").performClick()
            assertEquals(job,app.database.imports().job(job.id));assertEquals(item,app.database.imports().item(item.id))
            val photo=requireNotNull(app.database.photos().find(fixture.id))
            assertEquals(fixture.photo.capturedAt,photo.capturedAt);assertTrue(photo.importedMetadata.contains("Imported person"))
            assertTrue(fixture.file.isFile);assertEquals(fixture.photo.sha256,DriveBackupWorker.digest(fixture.file,"SHA-256"))
            // Keep the completed synthetic history out of subsequent production-shell fixtures.
            ImportHistory(app).dismiss(app.database,job.id)
        }
    }

    @Test fun actualLandscapeRotationKeepsViewerIdentityDetailsAndGalleryAnchor()=runBlocking<Unit> {
        Fixture("landscape").use { fixture ->
            try {
                fixture.insert();fixture.open();compose.onNodeWithTag("viewer-details").performClick();waitTag("photo-details")
                compose.runOnUiThread { compose.activity.requestedOrientation=ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE }
                compose.waitUntil(15000) { compose.activity.resources.configuration.orientation==android.content.res.Configuration.ORIENTATION_LANDSCAPE }
                waitTag("photo-details");compose.onNode(hasText(fixture.photo.displayName) and hasAnyAncestor(hasTestTag("photo-details"))).assertExists()
                compose.onNodeWithTag("photo-details-close").assertIsDisplayed();screenshot("details-landscape")
                compose.onNodeWithTag("photo-details-close").performClick();waitTag("viewer-screen")
                compose.onNodeWithText(fixture.photo.displayName).assertExists()
                compose.onNodeWithTag("viewer-close").performClick();waitTag("photo:${fixture.id}")
                compose.onNodeWithTag("photo:${fixture.id}").assertIsDisplayed()
                assertTrue(fixture.file.isFile);assertEquals(fixture.photo.sha256,DriveBackupWorker.digest(fixture.file,"SHA-256"))
            } finally {
                compose.runOnUiThread { compose.activity.requestedOrientation=ActivityInfo.SCREEN_ORIENTATION_PORTRAIT }
                compose.waitUntil(15000) { compose.activity.resources.configuration.orientation==android.content.res.Configuration.ORIENTATION_PORTRAIT }
            }
        }
    }

    private fun screenshot(name: String) {
        compose.waitForIdle()
        val bitmap=requireNotNull(instrumentation.uiAutomation.takeScreenshot())
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

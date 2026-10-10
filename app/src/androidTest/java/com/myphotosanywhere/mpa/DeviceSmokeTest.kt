package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import android.graphics.Color
import android.net.Uri
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.face.FaceDetection
import com.google.mlkit.vision.face.FaceDetectorOptions
import com.myphotosanywhere.mpa.drive.DriveBackupWorker
import com.myphotosanywhere.mpa.people.FaceEmbedding
import com.myphotosanywhere.mpa.people.FaceMatching
import kotlinx.coroutines.*
import kotlinx.coroutines.tasks.await
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class DeviceSmokeTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication

    @Test fun galleryCombinesSourcesAndFiltersIndependentlyOfTheStorageBrowser() = runBlocking<Unit> {
        val a=com.myphotosanywhere.mpa.drive.DriveSource.from(com.myphotosanywhere.mpa.drive.DriveConnection(
            accountId="test-a",email="a@test",mode="APP_FILES",folderId="folder-a",folderName="A"))
        val b=com.myphotosanywhere.mpa.drive.DriveSource.from(com.myphotosanywhere.mpa.drive.DriveConnection(
            accountId="test-b",email="b@test",mode="APP_FILES",folderId="folder-b",folderName="B"))
        val ids=listOf("source-test-a","source-test-b")
        try {
            app.database.drive().saveSource(a); app.database.drive().saveSource(b)
            app.database.drive().saveConnection(b.connection())
            for((id,source) in ids.zip(listOf(a,b))) app.database.photos().insert(com.myphotosanywhere.mpa.data.Photo(
                id,"$id.jpg","","","image/jpeg",8,1,"sha-$id",sourceBindingKey=source.key))
            compose.waitUntil(30000) { compose.onAllNodesWithTag("photo:${ids[0]}").fetchSemanticsNodes().isNotEmpty() &&
                compose.onAllNodesWithTag("photo:${ids[1]}").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithText("A · a@test").performScrollTo().performClick()
            compose.onNodeWithTag("photo:${ids[0]}").assertExists()
            compose.onNodeWithTag("photo:${ids[1]}").assertDoesNotExist()
            compose.onNodeWithText("B · b@test").performScrollTo().performClick()
            compose.onNodeWithTag("photo:${ids[1]}").assertExists()
            compose.onNodeWithTag("photo:${ids[0]}").assertDoesNotExist()
            compose.onNodeWithText("All sources").performScrollTo().performClick()
            compose.onNodeWithTag("photo:${ids[0]}").assertExists()
            compose.onNodeWithTag("photo:${ids[1]}").assertExists()
        } finally {
            for(id in ids) app.database.photos().find(id)?.let { app.database.photos().update(it.copy(archived=true)) }
            app.database.drive().disconnectSource(a.key); app.database.drive().disconnectSource(b.key)
            app.database.drive().disconnect()
        }
    }

    @Test fun importEditSaveAndBrowseCopyWithoutChangingOriginal() = runBlocking<Unit> {
        val fixture=File.createTempFile("fixture",".png",app.cacheDir)
        val source=Bitmap.createBitmap(120,80,Bitmap.Config.ARGB_8888).apply { eraseColor(Color.RED) }
        try { fixture.outputStream().use { check(source.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { source.recycle() }
        try {
            val photo=app.repository.importPhoto(Uri.fromFile(fixture),nameOverride="device-fixture.png",mimeOverride="image/png")
            val hash=DriveBackupWorker.digest(File(photo.originalPath),"SHA-256")
            compose.waitUntil(30000) { compose.onAllNodesWithTag("photo:${photo.id}").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("photo:${photo.id}").performClick()
            compose.onNodeWithText("View full screen").performClick()
            compose.onNodeWithTag("viewer-screen").assertIsDisplayed()
            compose.onNodeWithTag("viewer-close").performClick()
            compose.onNodeWithTag("photo-edit").performClick()
            compose.waitUntil(30000) {
                compose.onAllNodes(hasTestTag("editor-save") and isEnabled()).fetchSemanticsNodes().isNotEmpty()
            }
            compose.onNodeWithTag("editor-save").performClick()
            compose.waitUntil(30000) { compose.onAllNodesWithText("device-fixture-edited.jpg").fetchSemanticsNodes().isNotEmpty() }
            val result=app.database.photos().all().first { it.parentId==photo.id }
            assertEquals("MPA",result.editProvider);assertTrue(File(result.originalPath).isFile)
            assertEquals(hash,DriveBackupWorker.digest(File(photo.originalPath),"SHA-256"))
            val screenshot=InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot()
            assertNotNull(screenshot)
            if(screenshot!=null) try {
                if(android.os.Build.VERSION.SDK_INT>=29) {
                    val values=android.content.ContentValues().apply {
                        put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"device-preview.png")
                        put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png")
                        put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
                    }
                    val uri=requireNotNull(app.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
                    app.contentResolver.openOutputStream(uri)?.use { check(screenshot.compress(Bitmap.CompressFormat.PNG,100,it)) }
                        ?: error("Cannot save device screenshot.")
                }
            } finally { screenshot.recycle() }
        } finally { fixture.delete() }
    }
    @Test fun bundledModelRunsOnAndroidAndDetectorCanProcessAnImageOffline() = runBlocking<Unit> {
        withTimeout(60000) {
            val bitmap=Bitmap.createBitmap(112,112,Bitmap.Config.ARGB_8888).apply { eraseColor(Color.rgb(110,80,60)) }
            try {
                FaceEmbedding(app).use { model ->
                    val first=FaceMatching.decode(model.embed(bitmap));val second=FaceMatching.decode(model.embed(bitmap))
                    assertEquals(128,first.size);assertEquals(1f,FaceMatching.cosine(first,second),.001f)
                }
                val detector=FaceDetection.getClient(FaceDetectorOptions.Builder().setPerformanceMode(FaceDetectorOptions.PERFORMANCE_MODE_ACCURATE).build())
                try { detector.process(InputImage.fromBitmap(bitmap,0)).await() } finally { detector.close() }
            } finally { bitmap.recycle() }
        }
    }
    @Test fun portableXmpRoundTripsOnAndroidAndRecoversReviewedAnnotations() = runBlocking<Unit> {
        val source=com.myphotosanywhere.mpa.drive.DriveSource.from(com.myphotosanywhere.mpa.drive.DriveConnection(
            accountId="xmp-device",email="xmp@test",mode="APP_FILES",folderId="xmp-folder",folderName="XMP"))
        val id="xmp-device-photo"
        try {
            app.database.drive().saveSource(source)
            app.database.photos().insert(com.myphotosanywhere.mpa.data.Photo(id,"xmp-fixture.jpg","","","image/jpeg",10,1,"a".repeat(64)))
            app.database.cloud().savePhoto(com.myphotosanywhere.mpa.drive.CloudPhoto("${source.key}:remote",source.key,"remote","xmp-folder",
                "xmp-fixture.jpg","image/jpeg",10,"md5","1",null,null,null,null,photoId=id))
            val original=app.metadata.snapshot(source)
            val asset=original.assets.single().copy(favorite=true,place="Home & café",faces=listOf(
                com.myphotosanywhere.mpa.metadata.PortableFace("xmp-device-face","xmp-device-person",true,
                    com.myphotosanywhere.mpa.people.FaceRegion(.1f,.2f,.5f,.6f),1600,1200,"test-model")))
            val catalog=original.copy(assets=listOf(asset),people=listOf(com.myphotosanywhere.mpa.metadata.PortablePerson("xmp-device-person","Sam")))
            val xml=com.myphotosanywhere.mpa.metadata.XmpCodec.encode(catalog)
            assertEquals(catalog,com.myphotosanywhere.mpa.metadata.XmpCodec.decode(xml))
            app.metadata.applyCatalog(source,com.myphotosanywhere.mpa.metadata.XmpCodec.decode(xml),1)
            assertTrue(requireNotNull(app.database.photos().find(id)).favorite)
            assertEquals("Home & café",app.database.photos().find(id)?.placeLabel)
            assertEquals(id,app.database.library().face("xmp-device-face")?.photoId)
            assertEquals("xmp-device-person",app.database.library().face("xmp-device-face")?.personId)
            assertEquals("a".repeat(64),app.database.photos().find(id)?.sha256)
        } finally {
            app.database.photos().find(id)?.let { app.database.photos().update(it.copy(archived=true)) }
            app.database.drive().disconnectSource(source.key)
        }
    }

}

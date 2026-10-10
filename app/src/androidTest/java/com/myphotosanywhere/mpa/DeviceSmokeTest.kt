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
            if(screenshot!=null) try { File(app.filesDir,"device-preview.png").outputStream().use {
                screenshot.compress(Bitmap.CompressFormat.PNG,100,it)
            } } finally { screenshot.recycle() }
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
}

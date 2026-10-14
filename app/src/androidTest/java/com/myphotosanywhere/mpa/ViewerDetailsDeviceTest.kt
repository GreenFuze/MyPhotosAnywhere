package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import android.graphics.Color
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.core.net.toUri
import androidx.lifecycle.ViewModelStore
import androidx.test.espresso.Espresso
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.drive.CloudPhoto
import com.myphotosanywhere.mpa.drive.DownloadTask
import com.myphotosanywhere.mpa.drive.DriveBackupWorker
import com.myphotosanywhere.mpa.drive.DriveSource
import com.myphotosanywhere.mpa.library.LibraryFeaturesModel
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class ViewerDetailsDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    private fun waitTag(tag: String)=compose.waitUntil(30000) { compose.onAllNodes(if(tag.startsWith("photo:") || tag.startsWith("cloud:")) hasTestTag(tag) and isEnabled() else hasTestTag(tag)).fetchSemanticsNodes().isNotEmpty() }
    private fun waitGone(tag: String)=compose.waitUntil(10000) { compose.onAllNodesWithTag(tag).fetchSemanticsNodes().isEmpty() }
    private fun currentImage(): SemanticsNodeInteraction {
        val matcher=hasTestTag("original-image") and SemanticsMatcher("visible image") { it.boundsInRoot.width>0 && it.boundsInRoot.height>0 }
        // The filename changes when the pager crosses its midpoint, before the new original is necessarily placed.
        compose.waitUntil(30000) { compose.onAllNodes(matcher).fetchSemanticsNodes().size==1 }
        return compose.onNode(matcher)
    }
    private fun systemBack() { InstrumentationRegistry.getInstrumentation().sendKeyDownUpSync(android.view.KeyEvent.KEYCODE_BACK) }
    private fun waitName(name: String)=compose.waitUntil(10000) { compose.onAllNodesWithText(name).fetchSemanticsNodes().isNotEmpty() }

    private inner class Fixture(val suffix: String): AutoCloseable {
        val source=DriveSource("details-$suffix","details-$suffix","fixture@test","APP_FILES","details-$suffix","Details fixture")
        val file=File.createTempFile("details-$suffix",".png",app.filesDir)
        val hash: String
        val photos: List<Photo>
        init {
            val bitmap=Bitmap.createBitmap(240,360,Bitmap.Config.ARGB_8888)
            try { bitmap.eraseColor(Color.rgb(18,120,88));file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } }
            finally { bitmap.recycle() }
            hash=DriveBackupWorker.digest(file,"SHA-256")
            photos=(0..2).map { index -> Photo("details-$suffix-$index","details-$suffix-$index.png",file.path,file.path,"image/png",file.length(),
                2_000_000_000_000L-index*86400000L,hash,capturedAt=2_000_000_000_000L-index*86400000L,sourceBindingKey=source.key) }
            runBlocking { app.database.drive().saveSource(source);photos.forEach { app.database.photos().insert(it) } }
        }
        fun filter() {
            compose.onNodeWithTag("shell:filters").performClick()
            compose.onNodeWithText("Details fixture · fixture@test").performScrollTo().performClick()
            compose.onNodeWithText("Done").performScrollTo().performClick()
        }
        fun open() { filter();waitTag("photo:${photos[0].id}");compose.onNodeWithTag("photo:${photos[0].id}").performClick();waitTag("original-image") }
        override fun close() { runBlocking {
            photos.forEach { app.database.photos().find(it.id)?.let { row -> app.database.photos().update(row.copy(archived=true)) } }
            app.database.drive().disconnectSource(source.key)
        };file.delete() }
    }

    @Test fun detailsRetainsViewerAcrossMetadataEditsBackAndRotation() {
        Fixture("metadata").use { fixture ->
            fixture.open()
            waitTag("viewer-next");compose.onNodeWithTag("viewer-previous").assertIsNotEnabled();compose.onNodeWithTag("viewer-next").assertIsEnabled();compose.waitForIdle();saveScreenshot("viewer-actions")
            compose.onNodeWithTag("viewer-details").performClick();waitTag("photo-details")
            compose.onNodeWithTag("viewer-screen").assertExists()
            compose.onNodeWithTag("details:availability").assertTextEquals("Original available on this device")
            compose.onNodeWithTag("details:source").assertTextEquals("Source: Details fixture · fixture@test")
            compose.onNodeWithTag("details:favorite").performClick()
            compose.waitUntil(10000) { runBlocking { app.database.photos().find(fixture.photos[0].id)?.favorite==true } }
            compose.onNodeWithText("Name or set location").performScrollTo().performClick()
            compose.onNodeWithText("Place name").performTextReplacement("Garden")
            compose.onNodeWithText("Save").performClick()
            compose.waitUntil(10000) { runBlocking { app.database.photos().find(fixture.photos[0].id)?.placeLabel=="Garden" } }
            compose.activityRule.scenario.recreate();waitTag("photo-details")
            compose.onNodeWithText("Garden").performScrollTo().assertIsDisplayed()
            saveScreenshot("viewer-details")
            systemBack();waitGone("photo-details")
            compose.onNodeWithTag("viewer-screen").assertExists()
            compose.onNodeWithText(fixture.photos[0].displayName).assertExists()
            compose.onNodeWithTag("viewer-favorite").assertIsSelected()
            compose.onNodeWithContentDescription("Next photo").performClick();waitName(fixture.photos[1].displayName)
            waitTag("viewer-favorite")
            compose.onNodeWithTag("viewer-favorite").assertIsNotSelected().performClick()
            compose.waitUntil(10000) { runBlocking { app.database.photos().find(fixture.photos[1].id)?.favorite==true } }
            compose.onNodeWithTag("viewer-close").performClick();waitTag("photo:${fixture.photos[0].id}")
            assertEquals(fixture.hash,DriveBackupWorker.digest(fixture.file,"SHA-256"))
            assertEquals(fixture.photos[0].capturedAt,runBlocking { app.database.photos().find(fixture.photos[0].id)?.capturedAt })
        }
    }

    @Test fun upwardDetailsGestureDoesNotStealHorizontalPagingOrZoomedPan() {
        Fixture("gesture").use { fixture ->
            fixture.open()
            currentImage().performTouchInput { swipeDown() }
            compose.onNodeWithTag("photo-details").assertDoesNotExist()
            currentImage().performTouchInput { swipeUp() }
            waitTag("photo-details")
            compose.onNodeWithTag("photo-details-close").performClick();waitGone("photo-details")
            currentImage().performTouchInput { swipeLeft() }
            waitName(fixture.photos[1].displayName)
            compose.onNodeWithTag("photo-details").assertDoesNotExist()
            currentImage().performTouchInput {
                pinch(Offset(center.x-30,center.y),Offset(center.x+30,center.y),Offset(center.x-100,center.y),Offset(center.x+100,center.y))
            }
            currentImage().performTouchInput { swipeUp() }
            compose.onNodeWithTag("photo-details").assertDoesNotExist()
            compose.onNodeWithText(fixture.photos[1].displayName).assertExists()
            currentImage().performTouchInput { doubleClick() }
            currentImage().performTouchInput { swipeUp() }
            waitTag("photo-details")
            compose.onNodeWithTag("viewer-screen").assertExists()
            compose.onNodeWithTag("photo-details-close").performClick();waitGone("photo-details")
            compose.onNodeWithText(fixture.photos[1].displayName).assertExists()
        }
    }

    @Test fun searchViewerDetailsReturnsToItsSearchContext() {
        Fixture("search").use { fixture ->
            compose.onNodeWithTag("nav:Search").performClick()
            compose.onNodeWithTag("search:query").performTextInput("details-search-0")
            compose.onNodeWithTag("search:query").performImeAction()
            waitTag("photo:${fixture.photos[0].id}")
            compose.onNodeWithTag("photo:${fixture.photos[0].id}").performClick();waitTag("viewer-screen")
            // IME hide is asynchronous; Back must reach Details after the search
            // keyboard has actually left the window, rather than dismiss the IME.
            compose.waitUntil(10000) {
                !androidx.core.view.ViewCompat.getRootWindowInsets(compose.activity.window.decorView)
                    .let { it?.isVisible(androidx.core.view.WindowInsetsCompat.Type.ime())==true }
            }
            compose.onNodeWithTag("viewer-details").performClick();waitTag("photo-details")
            compose.onNodeWithTag("details:source").assertExists()
            systemBack();waitGone("photo-details")
            compose.onNodeWithTag("viewer-screen").assertExists()
            compose.onNodeWithTag("viewer-close").performClick()
            compose.onNodeWithTag("nav:Search").assertIsSelected()
            compose.onNodeWithTag("search:query").assertTextContains("details-search-0")
            compose.onNodeWithTag("photo:${fixture.photos[0].id}").assertIsDisplayed()
        }
    }

    @Test fun cloudDetailsIsAvailableWithoutMaterializingAnOriginal() {
        Fixture("remote").use { fixture ->
            val cloud=CloudPhoto("${fixture.source.key}:remote",fixture.source.key,"remote",fixture.source.folderId, "remote-preview.png","image/png",500,
                null,"1",null,2_000_100_000_000L,null,null,thumbnailPath=fixture.file.path,thumbnailVersion="1")
            runBlocking {
                app.database.cloud().savePhoto(cloud)
                app.database.cloud().saveDownload(DownloadTask(cloud.key,cloud.bindingKey,cloud.remoteId,state="AUTH_REQUIRED",error="Fixture authorization expired"))
            }
            fixture.filter();waitTag("cloud:${cloud.key}")
            compose.onNodeWithTag("cloud:${cloud.key}").performClick();waitTag("viewer-screen")
            compose.onNodeWithTag("viewer-details").performClick();waitTag("photo-details")
            compose.onNodeWithTag("details:availability").assertTextEquals("Original unavailable on this device")
            compose.onNodeWithTag("details:source").assertTextEquals("Source: Details fixture · fixture@test")
            compose.onNodeWithText("Reconnect source").assertExists()
            saveScreenshot("viewer-remote-details")
            compose.activityRule.scenario.recreate();waitTag("photo-details")
            compose.waitUntil(10000) { compose.onAllNodesWithTag("details:availability").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("details:availability").assertTextEquals("Original unavailable on this device")
            systemBack();waitGone("photo-details")
            compose.onNodeWithTag("viewer-screen").assertExists()
            compose.onNodeWithTag("viewer-edit").assertIsNotEnabled()
            compose.onNodeWithTag("viewer-share").assertIsNotEnabled()
            assertNull(runBlocking { app.database.cloud().photo(cloud.key)?.photoId })
            assertEquals("AUTH_REQUIRED",runBlocking { app.database.cloud().download(cloud.key)?.state })
            compose.onNodeWithTag("viewer-close").performClick()
        }
    }

    @Test fun missingOriginalShareFailureIsVisibleAndDoesNotReplaceThePhoto() {
        Fixture("missing").use { fixture ->
            fixture.open()
            compose.waitUntil(10000) { compose.onAllNodes(hasTestTag("viewer-share") and isEnabled()).fetchSemanticsNodes().isNotEmpty() }
            assertTrue(fixture.file.delete())
            compose.onNodeWithTag("viewer-share").performClick()
            waitTag("viewer:message")
            compose.onNodeWithTag("viewer:message").assertTextEquals("Download this photo before opening or editing it.").assertIsDisplayed()
            compose.onNodeWithText(fixture.photos[0].displayName).assertExists()
            compose.onNodeWithTag("viewer-details").performClick();waitTag("photo-details")
            compose.onNodeWithTag("details:availability").assertTextEquals("Original unavailable on this device")
            compose.onNodeWithTag("photo-details-close").performClick();waitGone("photo-details")
            compose.onNodeWithTag("viewer-screen").assertExists()
            assertEquals(fixture.photos[0].id,runBlocking { app.database.photos().find(fixture.photos[0].id)?.id })
        }
    }

    @Test fun exportResolvesOnlyTheRequestedIdentityAndPreservesBytes() {
        Fixture("export").use { fixture ->
            val store=ViewModelStore()
            lateinit var model: LibraryFeaturesModel
            compose.runOnIdle { model=LibraryFeaturesModel(app);store.put("export",model) }
            val output=File.createTempFile("details-export-result",".png",app.cacheDir)
            try {
                model.export(fixture.photos[1].id,output.toUri())
                compose.waitUntil(10000) { model.message.value=="Export saved. Original bytes and metadata preserved." }
                assertEquals(fixture.hash,DriveBackupWorker.digest(output,"SHA-256"))
                assertEquals(fixture.hash,DriveBackupWorker.digest(fixture.file,"SHA-256"))
            } finally { compose.runOnIdle { store.clear() };output.delete() }
        }
    }

    @Test fun immersiveControlsPreserveCanvasZoomPagingDetailsRotationAndReturn() {
        Fixture("immersive").use { fixture ->
            fixture.open();waitTag("viewer-next")
            val before=currentImage().fetchSemanticsNode().boundsInRoot
            currentImage().performTouchInput { click() };waitTag("viewer-show-controls")
            compose.onNodeWithTag("viewer-details").assertDoesNotExist()
            val after=currentImage().fetchSemanticsNode().boundsInRoot
            assertEquals(before.left,after.left,1f);assertEquals(before.top,after.top,1f)
            assertEquals(before.width,after.width,1f);assertEquals(before.height,after.height,1f)
            saveScreenshot("viewer-immersive")
            currentImage().performTouchInput { doubleClick() }
            compose.onNodeWithTag("viewer-show-controls").assertIsDisplayed()
            currentImage().performTouchInput { swipeLeft() }
            compose.onNodeWithTag("viewer-show-controls").performClick()
            compose.onNodeWithTag("viewer-position").assertTextEquals("1 / 3")
            currentImage().performTouchInput { doubleClick() }
            compose.onNodeWithTag("viewer-details").assertIsDisplayed()
            currentImage().performTouchInput { swipeLeft() };waitName(fixture.photos[1].displayName)
            compose.onNodeWithTag("viewer-hide-controls").performClick();waitTag("viewer-show-controls")
            compose.activityRule.scenario.recreate();waitTag("viewer-show-controls")
            compose.onNodeWithTag("viewer-details").assertDoesNotExist()
            currentImage().performTouchInput { swipeUp() };waitTag("photo-details")
            compose.onNodeWithTag("photo-details-close").performClick();waitGone("photo-details")
            waitName(fixture.photos[1].displayName)
            compose.onNodeWithTag("viewer-position").assertTextEquals("2 / 3")
            systemBack();waitGone("viewer-screen");waitTag("photo:${fixture.photos[1].id}")
            compose.onNodeWithTag("photo:${fixture.photos[1].id}").assertIsDisplayed()
            assertEquals(fixture.hash,DriveBackupWorker.digest(fixture.file,"SHA-256"))
            runBlocking { fixture.photos.forEach { expected ->
                val actual=requireNotNull(app.database.photos().find(expected.id))
                assertEquals(expected.sha256,actual.sha256);assertEquals(expected.capturedAt,actual.capturedAt)
                assertEquals(expected.sourceBindingKey,actual.sourceBindingKey);assertFalse(actual.favorite)
            } }
        }
    }

    @Test fun immersiveRemotePreviewRetainsRecoveryAndUpwardDetails() {
        Fixture("immersive-remote").use { fixture ->
            val cloud=CloudPhoto("${fixture.source.key}:remote",fixture.source.key,"remote",fixture.source.folderId,"remote-immersive.png","image/png",500,
                null,"1",null,2_000_100_000_000L,null,null,thumbnailPath=fixture.file.path,thumbnailVersion="1")
            runBlocking {
                app.database.cloud().savePhoto(cloud)
                app.database.cloud().saveDownload(DownloadTask(cloud.key,cloud.bindingKey,cloud.remoteId,state="AUTH_REQUIRED",error="Fixture authorization expired"))
            }
            fixture.filter();waitTag("cloud:${cloud.key}");compose.onNodeWithTag("cloud:${cloud.key}").performClick();waitTag("viewer-next")
            compose.onNodeWithTag("viewer-hide-controls").performClick();waitTag("viewer-show-controls")
            compose.onNodeWithText("Preview only — reconnect this Drive source.").assertIsDisplayed()
            compose.onNodeWithText("Manage source").assertIsDisplayed()
            compose.onNodeWithTag("viewer-media").performTouchInput { click(center) };waitTag("viewer-details")
            compose.onNodeWithTag("viewer-hide-controls").performClick();waitTag("viewer-show-controls")
            compose.onNodeWithTag("viewer-media").performTouchInput {
                swipe(Offset(center.x,center.y+height*.1f),Offset(center.x,center.y-height*.2f),400)
            }
            waitTag("photo-details")
            compose.onNodeWithTag("details:availability").assertTextEquals("Original unavailable on this phone")
            compose.onNodeWithTag("photo-details-close").performClick();waitGone("photo-details")
            compose.onNodeWithTag("viewer-details").assertIsDisplayed()
            runBlocking {
                assertNull(app.database.cloud().photo(cloud.key)?.photoId)
                assertEquals("AUTH_REQUIRED",app.database.cloud().download(cloud.key)?.state)
            }
            compose.onNodeWithTag("viewer-close").performClick();waitGone("viewer-screen")
            assertEquals(fixture.hash,DriveBackupWorker.digest(fixture.file,"SHA-256"))
            runBlocking { app.database.cloud().savePhoto(cloud.copy(removed=true)) }
        }
    }

    private fun saveScreenshot(name: String) {
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

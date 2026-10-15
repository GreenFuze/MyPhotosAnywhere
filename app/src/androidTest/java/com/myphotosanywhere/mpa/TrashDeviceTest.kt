package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import android.graphics.Color
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.trash.DriveTrashRecord
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/** Offline synthetic UI only: no owner's photos, tokens or live Drive mutations. */
@RunWith(AndroidJUnit4::class)
class TrashDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    private fun waitTag(tag: String)=compose.waitUntil(30000) { compose.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty() }
    private fun back()=InstrumentationRegistry.getInstrumentation().sendKeyDownUpSync(android.view.KeyEvent.KEYCODE_BACK)
    private inner class Fixture(val suffix: String,val trashed: Boolean): AutoCloseable {
        val source=DriveSource("trash-$suffix","trash-$suffix","trash-fixture@example.test","APP_FILES","trash-$suffix","Trash fixture")
        val file=File.createTempFile("trash-$suffix",".png",app.filesDir)
        val photo: Photo
        val cloud: CloudPhoto
        init {
            val image=Bitmap.createBitmap(300,480,Bitmap.Config.ARGB_8888)
            try { image.eraseColor(Color.rgb(30,140,120));file.outputStream().use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) } }
            finally { image.recycle() }
            val sha=DriveBackupWorker.digest(file,"SHA-256");val md5=DriveBackupWorker.digest(file,"MD5")
            photo=Photo("trash-$suffix","Trash fixture.png",file.path,file.path,"image/png",file.length(),2_000_000_000_000L,sha,
                capturedAt=2_000_000_000_000L,sourceBindingKey=source.key,sourceRemoteId=if(trashed) "remote" else null,
                cachedVersion=if(trashed) "1" else null,favorite=true,pinned=true)
            cloud=CloudPhoto("${source.key}:remote",source.key,"remote",source.folderId,photo.displayName,photo.mimeType,
                photo.bytes,md5,"1",null,photo.capturedAt,null,null,file.path,photoId=photo.id,removed=trashed,sha256=sha)
            runBlocking {
                app.database.drive().saveSource(source);app.database.photos().insert(photo)
                if(trashed) {
                    app.database.cloud().savePhoto(cloud)
                    app.database.trash().save(DriveTrashRecord(cloud.key,source.key,source.accountId,cloud.remoteId,photo.id,
                        photo.displayName,photo.mimeType,photo.bytes,md5,sha,"1",file.path,photo.capturedAt,confirmed=true,updatedAt=1))
                }
            }
        }
        override fun close() {
            runBlocking {
                app.database.openHelper.writableDatabase.execSQL("DELETE FROM drive_trash WHERE bindingKey=?",arrayOf(source.key))
                app.database.photos().find(photo.id)?.let { app.database.photos().update(it.copy(archived=true)) }
                app.database.drive().disconnectSource(source.key)
            }
            file.delete()
        }
    }

    @Test fun cachedTrashPreviewRestoreConfirmationAndBackPreserveOriginal() {
        Fixture("browser",true).use { fixture ->
            compose.onNodeWithTag("nav:Collections").performClick();waitTag("collections:Trash")
            compose.onNodeWithTag("collections:Trash").performClick();waitTag("trash:screen")
            waitTag("trash:item:${fixture.cloud.key}")
            compose.waitForIdle();save("trash-browser")
            compose.onNodeWithTag("trash:item:${fixture.cloud.key}").performClick()
            waitTag("trash:restore");compose.onNodeWithTag("trash:restore").performScrollTo().performClick()
            waitTag("trash:confirmation")
            compose.onNodeWithText("trash-fixture@example.test · Trash fixture").assertExists()
            compose.waitForIdle();save("trash-restore")
            back();waitTag("trash:screen")
            runBlocking {
                val row=requireNotNull(app.database.trash().find(fixture.cloud.key))
                assertTrue(row.confirmed);assertNull(row.desired)
                assertEquals(fixture.photo,app.database.photos().find(fixture.photo.id))
            }
            assertTrue(fixture.file.isFile)
            compose.activityRule.scenario.recreate();waitTag("trash:screen");waitTag("trash:item:${fixture.cloud.key}")
            assertTrue(fixture.file.isFile)
        }
    }

    @Test fun viewerTrashForLocalOnlyOriginalExplainsProtectionWithoutQueuing() {
        Fixture("local",false).use { fixture ->
            compose.onNodeWithTag("shell:filters").performClick()
            compose.onNodeWithText("Trash fixture · trash-fixture@example.test").performScrollTo().performClick()
            compose.onNodeWithText("Done").performScrollTo().performClick()
            waitTag("photo:${fixture.photo.id}");compose.onNodeWithTag("photo:${fixture.photo.id}").performClick()
            waitTag("viewer-trash");compose.onNodeWithTag("viewer-trash").performClick();waitTag("trash:confirmation")
            compose.onNodeWithTag("trash:confirm").assertIsNotEnabled()
            compose.onNodeWithText("This original is only on this device.",substring=true).assertExists()
            compose.waitForIdle();save("trash-local-protected")
            back()
            runBlocking { assertNull(app.database.trash().find(fixture.cloud.key));assertEquals(fixture.photo.originalPath,app.database.photos().find(fixture.photo.id)?.originalPath) }
            assertTrue(fixture.file.isFile)
        }
    }

    private fun save(name: String) {
        val image=requireNotNull(InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot())
        try {
            val values=android.content.ContentValues().apply {
                put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"$name.png")
                put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png")
                put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
            }
            val uri=requireNotNull(app.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
            requireNotNull(app.contentResolver.openOutputStream(uri)).use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) }
        } finally { image.recycle() }
    }
}

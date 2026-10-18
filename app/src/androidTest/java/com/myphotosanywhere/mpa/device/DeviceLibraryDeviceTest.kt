package com.myphotosanywhere.mpa.device

import android.content.ContentValues
import android.graphics.Bitmap
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.lifecycle.ViewModelProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.MainActivity
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.IOException

/** Run after the ordinary suite: full media grants must not contaminate other gallery fixtures. */
@RunWith(AndroidJUnit4::class)
@DeviceLibraryStage
class DeviceLibraryDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private val app get()=instrumentation.targetContext.applicationContext as MpaApplication
    private val uris=mutableListOf<Uri>()
    private val foreignUris=mutableListOf<Uri>()
    private val ids=mutableSetOf<String>()
    private fun grant() {
        app.deviceMedia.permissions.request().filterNot { it==android.Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED }.forEach {
            instrumentation.uiAutomation.grantRuntimePermission(app.packageName,it)
        }
        assertTrue(app.deviceMedia.permissions.current().full)
    }
    private fun camera(): Uri {
        val values=ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME,"mpa-camera-${java.util.UUID.randomUUID()}.png")
            put(MediaStore.MediaColumns.MIME_TYPE,"image/png")
            put(MediaStore.MediaColumns.RELATIVE_PATH,"DCIM/Camera")
            put(MediaStore.MediaColumns.IS_PENDING,1)
            put(MediaStore.Images.ImageColumns.DATE_TAKEN,13_000_000_000_000L)
        }
        val uri=requireNotNull(app.contentResolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI,values));uris+=uri
        val image=Bitmap.createBitmap(1200,800,Bitmap.Config.ARGB_8888).apply { eraseColor(Color.GREEN) }
        try { requireNotNull(app.contentResolver.openOutputStream(uri)).use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) } }
        finally { image.recycle() }
        app.contentResolver.update(uri,ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING,0) },null,null)
        return uri
    }
    private suspend fun indexed(uri: Uri): Photo {
        app.deviceMedia.reconcile()
        val mediaId=android.content.ContentUris.parseId(uri)
        val record=app.database.openHelper.readableDatabase.query("SELECT photoId FROM device_media WHERE uri LIKE ? AND available=1",arrayOf("%/$mediaId")).use { cursor ->
            check(cursor.moveToFirst());cursor.getString(0)
        }
        ids+=record
        return requireNotNull(app.database.photos().find(record))
    }
    private fun open(photo: Photo,timeline: TimelineViewModel) {
        compose.runOnIdle { timeline.showNewest(TimelineQuery()) }
        compose.waitUntil(30000) {
            compose.onAllNodes(hasTestTag("photo:${photo.id}") and isEnabled()).fetchSemanticsNodes().isNotEmpty() &&
                timeline.refreshedGeneration.value==timeline.restoration.value &&
                compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty()
        }
        compose.onNodeWithTag("photo:${photo.id}").performClick()
        compose.waitUntil(10000) { compose.onAllNodesWithTag("viewer-screen").fetchSemanticsNodes().isNotEmpty() }
        assertEquals(photo.id,compose.runOnUiThread { requireNotNull(timeline.viewer.value).selected.photoId })
    }
    private fun cleanup()=runBlocking {
        for(uri in foreignUris) shell("content delete --uri $uri")
        for(uri in uris) app.contentResolver.delete(uri,null,null)
        app.deviceMedia.reconcile()
        for(id in ids) {
            val photo=app.database.photos().find(id)
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM manual_device_backups WHERE photoId=?",arrayOf(id))
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM drive_backups WHERE photoId=?",arrayOf(id))
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM photos WHERE id=?",arrayOf(id))
            photo?.originalPath?.takeIf { it.isNotBlank() }?.let { File(it).delete() }
        }
    }
    private fun shell(command: String): String {
        // UiAutomation executes an argv string, not a shell expression. A readable script
        // preserves quoting/redirection on API29 too, before executeShellCommandRw exists.
        val script=File(requireNotNull(app.externalCacheDir),"fixture-${java.util.UUID.randomUUID()}.sh")
        script.writeText("set -eu\nexec 2>&1\n$command\nprintf '\\nMPA_FIXTURE_OK\\n'\n")
        try {
            val output=android.os.ParcelFileDescriptor.AutoCloseInputStream(
                instrumentation.uiAutomation.executeShellCommand("sh ${script.absolutePath}")
            ).use { it.readBytes().toString(Charsets.UTF_8) }
            check(output.endsWith("\nMPA_FIXTURE_OK\n")) { "Shell fixture command failed: $output" }
            check(!output.contains("Error while accessing provider:")) { "Content fixture operation failed: $output" }
            return output.removeSuffix("\nMPA_FIXTURE_OK\n")
        } finally { check(!script.exists() || script.delete()) }
    }
    private fun foreign(folder: String): Uri {
        check(folder in listOf("DCIM/Camera/","Pictures/Screenshots/"))
        val name="foreign-${java.util.UUID.randomUUID()}.png"
        val stage=File(requireNotNull(app.externalCacheDir),name)
        val image=Bitmap.createBitmap(720,1080,Bitmap.Config.ARGB_8888).apply { eraseColor(Color.CYAN) }
        try { stage.outputStream().use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { image.recycle() }
        try {
            shell("content insert --uri content://media/external/images/media --bind _display_name:s:$name --bind mime_type:s:image/png --bind relative_path:s:$folder --bind is_pending:i:1 --bind owner_package_name:s:com.android.shell --bind datetaken:l:13000000002000")
            // content insert intentionally discards its returned URI. Resolve this unique,
            // shell-owned pending row using the same shell identity before publishing it.
            val output=shell("content query --uri 'content://media/external/images/media?includePending=1' --projection _id --where \"_display_name='$name'\"")
            val matches=Regex("_id=([0-9]+)").findAll(output).toList()
            check(matches.size==1) { "Expected one foreign camera fixture: $output" }
            val uri=android.content.ContentUris.withAppendedId(MediaStore.Images.Media.EXTERNAL_CONTENT_URI,matches.single().groupValues[1].toLong())
            foreignUris+=uri
            if(Build.VERSION.SDK_INT==29) {
                // Android10 content write passes a null calling package, which its provider
                // can reject. Only this emulator fixture copies to the fixed UUID path.
                shell("mkdir -p '/sdcard/$folder'\ncp '${stage.absolutePath}' '/sdcard/$folder$name'")
                // A privileged Android10 update skips the scanner used for ordinary app
                // publication. Supply facts of these verified bytes before publishing.
                shell("content update --uri $uri --bind _size:l:${stage.length()} --bind width:i:720 --bind height:i:1080")
            } else shell("content write --uri $uri < '${stage.absolutePath}'")
            shell("content update --uri $uri --bind is_pending:i:0")
            app.contentResolver.query(uri,arrayOf(MediaStore.MediaColumns.OWNER_PACKAGE_NAME,MediaStore.MediaColumns.SIZE,MediaStore.MediaColumns.WIDTH,MediaStore.MediaColumns.HEIGHT),null,null,null)?.use {
                check(it.moveToFirst());assertEquals("com.android.shell",it.getString(0))
                assertEquals(stage.length(),it.getLong(1));assertEquals(720,it.getInt(2));assertEquals(1080,it.getInt(3))
            } ?: error("Cannot inspect foreign camera fixture")
            val bytes=requireNotNull(app.contentResolver.openInputStream(uri)).use { it.readBytes() }
            assertArrayEquals(stage.readBytes(),bytes)
            return uri
        } finally { check(!stage.exists() || stage.delete()) }
    }
    @Test fun otherAppCameraAndScreenshotsAppearInTheirOwnFoldersWithoutDriveBadges()=runBlocking<Unit> {
        grant()
        try {
            val camera=indexed(foreign("DCIM/Camera/"));val screenPhoto=indexed(foreign("Pictures/Screenshots/"))
            assertFalse(app.database.presence().inDrive(camera.id).first());assertFalse(app.database.presence().inDrive(screenPhoto.id).first())
            for(photo in listOf(camera,screenPhoto)) { assertEquals("",photo.originalPath);assertTrue(app.deviceMedia.readable(photo.id)) }
            val folder=requireNotNull(app.database.deviceMedia().find(screenPhoto.id))
            assertEquals("Pictures/Screenshots/",folder.folderPath)
            val query=TimelineQuery(deviceFolder=DeviceFolderKey(folder.volume,folder.folderPath))
            assertTrue(app.timeline.page(query,null,TimelineDirection.AFTER,60).any { it.photoId==screenPhoto.id })
            assertFalse(app.timeline.page(query,null,TimelineDirection.AFTER,60).any { it.photoId==camera.id })
            compose.onNodeWithTag("nav:Collections").performClick()
            compose.onNodeWithTag("collections:On this device").performScrollTo().performClick()
            compose.waitUntil(20000) { compose.onAllNodesWithTag("device:folders").fetchSemanticsNodes().isNotEmpty() }
            val tag="device:folder:${DeviceFolderKey(folder.volume,folder.folderPath).key}"
            compose.onNodeWithTag(tag).performScrollTo().assertIsDisplayed()
            screenshot("device-folders-foreign")
            compose.onNodeWithTag(tag).performClick()
            compose.waitUntil(20000) { compose.onAllNodesWithTag("photo:${screenPhoto.id}").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("photo:${screenPhoto.id}").assertIsDisplayed()
            compose.onNodeWithTag("photo:${camera.id}").assertDoesNotExist()
            screenshot("device-folder-gallery")
            compose.onNodeWithTag("photo:${screenPhoto.id}").performClick()
            compose.waitUntil(10000) { compose.onAllNodesWithTag("viewer-backup").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("viewer-backup").assertIsDisplayed()
            compose.onNodeWithTag("viewer-backup").performClick()
            compose.waitUntil(10000) { compose.onAllNodesWithTag("device:backup-dialog").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithText("Includes the original and available photo information:",substring=true).performScrollTo().assertIsDisplayed()
            screenshot("device-backup-metadata")
            compose.onNodeWithText("Cancel").performClick()
            assertTrue(app.database.drive().backupsForPhoto(screenPhoto.id).isEmpty())
        } finally { cleanup() }
    }
    @Test fun cameraPhotosAppearWithoutCopyingAndOpenRealOriginalsWithDetailsAndBackup()=runBlocking<Unit> {
        grant()
        try {
            val photo=indexed(camera())
            assertEquals("",photo.originalPath);assertEquals("",photo.sha256);assertTrue(photo.thumbnailPath.startsWith("content://media/"))
            assertTrue(app.deviceMedia.readable(photo.id));assertFalse(app.database.presence().inDrive(photo.id).first())
            app.drive.enqueue(photo);assertTrue(app.database.drive().backupsForPhoto(photo.id).isEmpty())
            val timeline=ViewModelProvider(compose.activity)[TimelineViewModel::class.java]
            open(photo,timeline)
            compose.waitUntil(20000) { compose.onAllNodes(hasText("Original · 1200 × 800",substring=true)).fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("viewer-backup").assertIsDisplayed()
            compose.onNodeWithTag("viewer-trash").assertDoesNotExist()
            compose.onNodeWithTag("viewer-details").performClick()
            compose.waitUntil(10000) { compose.onAllNodesWithTag("details:availability").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("details:availability").performScrollTo().assertTextEquals("Original available on this device")
            compose.waitUntil(10000) { compose.onAllNodesWithTag("details:dimensions").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("details:dimensions").performScrollTo().assertTextEquals("1200 × 800")
            compose.onNodeWithTag("details:backup").performScrollTo().assertExists()
            screenshot("device-camera-details")
            assertEquals("",app.database.photos().find(photo.id)?.originalPath)
        } finally { cleanup() }
    }
    @Test fun materializationKeepsIdentityCameraBytesAndProtectedPrivateOriginal()=runBlocking<Unit> {
        grant()
        try {
            val uri=camera();val original=indexed(uri)
            val before=requireNotNull(app.contentResolver.openInputStream(uri)).use { it.readBytes() }
            val copy=app.deviceMedia.materialize(original.id)
            assertEquals(original.id,copy.id);assertEquals(before.size.toLong(),copy.bytes)
            assertArrayEquals(before,File(copy.originalPath).readBytes())
            assertEquals(DriveBackupWorker.digest(File(copy.originalPath),"SHA-256"),copy.sha256)
            assertArrayEquals(before,requireNotNull(app.contentResolver.openInputStream(uri)).use { it.readBytes() })
            app.cache.enforce();assertTrue(File(copy.originalPath).isFile)
            assertEquals(copy.id,indexed(uri).id);assertFalse(app.database.presence().inDrive(copy.id).first())
        } finally { cleanup() }
    }
    @Test fun boundedRecognitionPreviewDoesNotCreatePrivateOriginalOrEnableConsent()=runBlocking<Unit> {
        grant()
        try {
            val photo=indexed(camera());val consent=app.settings.state.value
            app.deviceMedia.withPreview(photo.id) { current,image ->
                assertEquals(photo.id,current.id);assertTrue(maxOf(image.width,image.height)<=1600)
                assertTrue(image.allocationByteCount<=1600*1600*4)
            }
            val reference=requireNotNull(app.database.deviceMedia().find(photo.id))
            try {
                app.deviceMedia.openDescriptor(reference.copy(providerVersion=reference.providerVersion+"-stale")).close()
                fail("A reused provider URI must not bypass its saved source identity")
            } catch(_: IOException) { }
            assertNull(com.myphotosanywhere.mpa.library.previewSource(photo.thumbnailPath))
            val preview=requireNotNull(app.cache.thumbnail(photo))
            assertTrue(preview.isFile)
            assertTrue(preview.canonicalPath.startsWith(File(app.filesDir,"thumbnails").canonicalPath+File.separator))
            assertTrue(android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds=true
                android.graphics.BitmapFactory.decodeFile(preview.path,this)
            }.let { it.outWidth in 1..1600 && it.outHeight in 1..1600 })
            // Android can publish a readable URI before its scanner fills width/height.
            val incompleteDimensions=requireNotNull(app.database.deviceMedia().find(photo.id))
            app.database.deviceMedia().save(incompleteDimensions.copy(width=0,height=0))
            val facts=com.myphotosanywhere.mpa.library.PhotoFactsRepository(app.timeline,app.cache,app.deviceMedia).read(photo)
            assertEquals(1200 to 800,facts.dimensions)
            assertEquals(1200 to 800,app.deviceMedia.imageDimensions(photo.id))
            assertEquals("",app.database.photos().find(photo.id)?.originalPath)
            assertEquals(consent.faceIndexing,app.settings.state.value.faceIndexing)
            assertEquals(consent.objectIndexing,app.settings.state.value.objectIndexing)
        } finally { cleanup() }
    }
    @Test fun incompleteWorkingCopyAndOversizedBudgetKeepCameraOriginalAndCleanStages()=runBlocking<Unit> {
        grant()
        try {
            val uri=camera();val photo=indexed(uri);val before=requireNotNull(app.contentResolver.openInputStream(uri)).use { it.readBytes() }
            app.database.photos().update(photo.copy(bytes=photo.bytes+1))
            try { app.deviceMedia.materialize(photo.id);fail("Incomplete copy must not commit") } catch(_: IOException) { }
            assertEquals("",app.database.photos().find(photo.id)?.originalPath)
            assertTrue(File(app.filesDir,"device-original-staging").listFiles().orEmpty().isEmpty())
            app.database.photos().update(photo.copy(bytes=Long.MAX_VALUE))
            try { app.deviceMedia.materialize(photo.id);fail("Overflowing storage budget must fail safely") } catch(_: IOException) { }
            assertEquals("",app.database.photos().find(photo.id)?.originalPath)
            assertArrayEquals(before,requireNotNull(app.contentResolver.openInputStream(uri)).use { it.readBytes() })
        } finally { cleanup() }
    }
    @Test fun manualBackupCapturesDestinationWithoutEnablingAutomaticCameraUploads()=runBlocking<Unit> {
        grant()
        val source=DriveSource.from(DriveConnection(accountId="manual-device-fixture",email="manual@test",mode="APP_FILES",folderId="manual-device-folder",folderName="Manual fixture",unmeteredOnly=true))
        val other=DriveSource.from(source.connection().copy(folderId="manual-device-other",folderName="Other"))
        try {
            val photo=indexed(camera());app.database.drive().saveSource(source);app.database.drive().saveSource(other)
            assertFalse(source.backupEnabled)
            app.drive.enqueueManual(photo.id,source.key)
            val task=app.database.drive().backupsForPhoto(photo.id).single()
            assertEquals(source.key,task.bindingKey);assertEquals(source.folderId,task.folderId)
            val proof=requireNotNull(app.database.deviceMedia().authorization(task.id))
            assertEquals(task.photoId,proof.photoId);assertEquals(source.key,proof.bindingKey)
            assertFalse(app.database.presence().inDrive(photo.id).first())
            app.database.drive().saveConnection(other.connection())
            try { app.drive.enqueueManual(photo.id,other.key);fail("Captured work must never retarget") } catch(_: IOException) { }
            assertEquals(task.id,app.database.drive().backupsForPhoto(photo.id).single().id)
            assertFalse(requireNotNull(app.database.drive().source(source.key)).backupEnabled)
        } finally {
            androidx.work.WorkManager.getInstance(app).cancelAllWorkByTag("${DriveCoordinator.BACKUP_TAG}:${source.key}").result.get()
            cleanup();app.database.drive().disconnectSource(source.key);app.database.drive().disconnectSource(other.key);app.database.drive().disconnect()
        }
    }
    @Test fun cameraVideoGetsABoundedPosterAndPlaysItsUriWithoutCopyingTheOriginal()=runBlocking<Unit> {
        grant()
        try {
            val values=ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME,"mpa-camera-video.mp4");put(MediaStore.MediaColumns.MIME_TYPE,"video/mp4")
                put(MediaStore.MediaColumns.RELATIVE_PATH,"DCIM/Camera");put(MediaStore.MediaColumns.IS_PENDING,1)
                put(MediaStore.Video.VideoColumns.DATE_TAKEN,13_000_000_001_000L)
            }
            val uri=requireNotNull(app.contentResolver.insert(MediaStore.Video.Media.EXTERNAL_CONTENT_URI,values));uris+=uri
            val bytes=instrumentation.context.assets.open("device-camera.mp4").use { it.readBytes() }
            requireNotNull(app.contentResolver.openOutputStream(uri)).use { it.write(bytes) }
            app.contentResolver.update(uri,ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING,0) },null,null)
            val photo=indexed(uri);val poster=requireNotNull(app.cache.thumbnail(photo))
            assertTrue(poster.isFile);assertTrue(poster.length()<2*1024*1024)
            val bounds=android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds=true }
            android.graphics.BitmapFactory.decodeFile(poster.path,bounds)
            assertTrue(bounds.outWidth>0 && bounds.outHeight>0);assertTrue(maxOf(bounds.outWidth,bounds.outHeight)<=480)
            val timeline=ViewModelProvider(compose.activity)[TimelineViewModel::class.java]
            open(photo,timeline)
            fun player(): android.widget.VideoView? {
                if(Build.VERSION.SDK_INT<29) return null
                fun find(view: android.view.View): android.widget.VideoView? {
                    if(view is android.widget.VideoView) return view
                    if(view is android.view.ViewGroup) for(index in 0 until view.childCount) find(view.getChildAt(index))?.let { return it }
                    return null
                }
                return android.view.inspector.WindowInspector.getGlobalWindowViews().firstNotNullOfOrNull { find(it) }
            }
            compose.waitUntil(20000) { compose.runOnUiThread { player()?.duration?.let { it>0 }==true } }
            val first=compose.runOnUiThread { requireNotNull(player()).also { it.start() } }
            compose.waitUntil(5000) { compose.runOnUiThread { first.isPlaying } }
            compose.activityRule.scenario.recreate()
            compose.waitUntil(20000) { compose.runOnUiThread { player()?.duration?.let { it>0 }==true } }
            assertFalse(compose.runOnUiThread { first.isPlaying })
            compose.onNodeWithTag("viewer-backup").assertIsDisplayed()
            val navigation=listOf("viewer-previous","viewer-position","viewer-next").map { compose.onNodeWithTag(it).fetchSemanticsNode().boundsInWindow }
            val actions=listOf("viewer-share","viewer-favorite","viewer-backup").map { compose.onNodeWithTag(it).assertIsDisplayed().fetchSemanticsNode().boundsInWindow }
            assertTrue("Viewer navigation and actions must occupy separate rows",navigation.all { control -> actions.all { action -> control.bottom<=action.top } })
            val viewerBounds=compose.onNodeWithTag("viewer-screen").fetchSemanticsNode().boundsInWindow
            var restoredFrameReady=false
            try {
                compose.waitUntil("Restored video must show its frame over opaque black letterboxing",10000) { opaqueVideoFrame(viewerBounds) }
                restoredFrameReady=true
            } finally {
                if(!restoredFrameReady) screenshot("device-camera-video")
            }
            screenshot("device-camera-video")
            assertEquals("",app.database.photos().find(photo.id)?.originalPath)
            assertEquals("",app.database.photos().find(photo.id)?.sha256)
            assertArrayEquals(bytes,requireNotNull(app.contentResolver.openInputStream(uri)).use { it.readBytes() })
            compose.onNodeWithTag("viewer-close").performClick()
            compose.onNodeWithTag("photo:${photo.id}").assertExists()
        } finally { cleanup() }
    }
    companion object {
        @org.junit.AfterClass @JvmStatic fun retainReferencesForSeparateRevocationInvocation()=runBlocking<Unit> {
            val app=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
            DeviceAccessFixture.prepare(app)
        }
    }
    /** Check the actual compositor, not just MediaPlayer preparation or Compose semantics. */
    private fun opaqueVideoFrame(bounds: androidx.compose.ui.geometry.Rect): Boolean {
        val image=instrumentation.uiAutomation.takeScreenshot() ?: return false
        try {
            fun pixel(x: Float,y: Float)=image.getPixel(
                (bounds.left+bounds.width*x).toInt().coerceIn(0,image.width-1),
                (bounds.top+bounds.height*y).toInt().coerceIn(0,image.height-1))
            val center=pixel(.5f,.5f)
            val frame=Color.green(center)>Color.red(center)+20 && Color.green(center)>Color.blue(center)+10
            val opaque=listOf(.2f,.8f).all { y -> listOf(.25f,.5f,.75f).all { x ->
                val color=pixel(x,y)
                Color.red(color)<=8 && Color.green(color)<=8 && Color.blue(color)<=8
            } }
            return frame && opaque
        } finally { image.recycle() }
    }
    private fun screenshot(name: String) {
        val image=requireNotNull(instrumentation.uiAutomation.takeScreenshot())
        try {
            com.myphotosanywhere.mpa.NativeScreenshotEvidence(app).write(name,image)
        } finally { image.recycle() }
    }
}

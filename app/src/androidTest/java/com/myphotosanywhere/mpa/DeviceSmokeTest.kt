package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import android.graphics.Color
import android.net.Uri
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
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

    private fun allowImportNotifications() {
        if(android.os.Build.VERSION.SDK_INT>=33) InstrumentationRegistry.getInstrumentation().uiAutomation
            .grantRuntimePermission(app.packageName,android.Manifest.permission.POST_NOTIFICATIONS)
    }

    @android.annotation.SuppressLint("MissingPermission")
    @Test fun backgroundImportNotificationUpdatesVerifiedProgressAndOpensStorage() {
        allowImportNotifications()
        val notices=com.myphotosanywhere.mpa.importing.ImportNotifications(app)
        val manager=app.getSystemService(android.app.NotificationManager::class.java)
        val id=89101
        try {
            assertTrue(notices.enabled())
            if(android.os.Build.VERSION.SDK_INT>=29) assertEquals(android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC,
                notices.foreground(id,com.myphotosanywhere.mpa.importing.ImportProgress("TAKEOUT","RUNNING",1,3,"Preparing")).foregroundServiceType)
            manager.notify(id,notices.notification(com.myphotosanywhere.mpa.importing.ImportProgress("TAKEOUT","RUNNING",1,3,"Uploading photo · 50%")))
            compose.waitUntil(5000) { manager.activeNotifications.any { it.id==id } }
            val active=manager.activeNotifications.single { it.id==id }.notification
            assertEquals(R.drawable.ic_import_notification,active.smallIcon.resId)
            assertEquals(1,active.extras.getInt(android.app.Notification.EXTRA_PROGRESS))
            assertEquals(3,active.extras.getInt(android.app.Notification.EXTRA_PROGRESS_MAX))
            assertTrue(active.flags and android.app.Notification.FLAG_ONGOING_EVENT != 0)
            manager.notify(id,notices.notification(com.myphotosanywhere.mpa.importing.ImportProgress("TAKEOUT","RUNNING",2,3,"Preparing")))
            compose.waitUntil(5000) { manager.activeNotifications.single { it.id==id }.notification.extras.getInt(android.app.Notification.EXTRA_PROGRESS)==2 }
            active.contentIntent.send()
            compose.waitUntil(10000) { compose.onAllNodesWithText("Imports",useUnmergedTree=true).fetchSemanticsNodes().isNotEmpty() }
        } finally { manager.cancel(id) }
    }

    @Test fun storageImportProgressFollowsRoomCheckpointsAheadOfOlderHistory() {
        val id="in-app-progress-device"
        val dao=app.database.imports()
        val job=com.myphotosanywhere.mpa.importing.ImportJob(id,"synthetic","TAKEOUT",state="RUNNING",created=1)
        val item=com.myphotosanywhere.mpa.importing.ImportItem("$id-video",id,"content://fixture","clip.mp4","clip.mp4","video/mp4",100,
            state="UPLOADING",offset=25)
        val history=(1..11).map { job.copy(id="$id-history-$it",state="CANCELLED",created=100L+it) }
        fun waitText(tag: String,text: String) {
            compose.waitUntil(10000) { compose.onAllNodes(hasTestTag(tag) and hasText(text)).fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag(tag).performScrollTo().assertTextEquals(text).assertIsDisplayed()
        }
        try {
            // Insert after Activity resume: fixtures observe Room without scheduling real transfers.
            runBlocking { dao.saveJob(job); history.forEach { dao.saveJob(it) } }
            compose.onNodeWithTag("shell:activity").performClick()
            waitText("import-count:$id","0/0 verified in Drive")
            compose.onNodeWithTag("import-bar:$id").assertRangeInfoEquals(androidx.compose.ui.semantics.ProgressBarRangeInfo.Indeterminate)
            runBlocking {
                dao.insertItem(item)
                dao.insertItem(item.copy(id="$id-pending",state="PENDING",offset=0))
                dao.insertItem(item.copy(id="$id-unselected",state="COMPLETE",selected=false))
            }
            waitText("import-detail:$id","Uploading clip.mp4 · 25%")
            waitText("import-count:$id","0/2 verified in Drive")
            runBlocking { dao.updateItem(item.copy(offset=75)) }
            waitText("import-detail:$id","Uploading clip.mp4 · 75%")
            runBlocking { dao.updateItem(item.copy(state="COMPLETE",offset=100)) }
            waitText("import-count:$id","1/2 verified in Drive")
            compose.onNodeWithTag("import-bar:$id").assertRangeInfoEquals(androidx.compose.ui.semantics.ProgressBarRangeInfo(0.5f,0f..1f))
            runBlocking { dao.updateItem(item.copy(id="$id-pending",name="next.mp4",state="UPLOADING",offset=50)) }
            waitText("import-detail:$id","Uploading next.mp4 · 50%")
            compose.onNodeWithTag("import-progress:$id").performScrollTo().assertIsDisplayed()
            val screenshot=requireNotNull(InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot())
            try {
                if(android.os.Build.VERSION.SDK_INT>=29) {
                    val values=android.content.ContentValues().apply {
                        put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"import-preview.png")
                        put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png")
                        put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
                    }
                    val uri=requireNotNull(app.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
                    app.contentResolver.openOutputStream(uri)?.use { check(screenshot.compress(Bitmap.CompressFormat.PNG,100,it)) }
                        ?: error("Cannot save import progress screenshot.")
                }
            } finally { screenshot.recycle() }
            runBlocking { dao.saveJob(job.copy(error="Connection interrupted · Retrying automatically")) }
            waitText("import-detail:$id","Connection interrupted · Retrying automatically")
            runBlocking { dao.saveJob(job.copy(state="PAUSED",created=Long.MAX_VALUE)) }
            waitText("import-detail:$id","Paused · Resume when ready")
            waitText("import-count:$id","1/2 verified in Drive")
            val legacy="Background import stopped. Open MPA and resume; transfer checkpoints are preserved."
            runBlocking { dao.saveJob(job.copy(state="FAILED",error=legacy,created=Long.MAX_VALUE)) }
            waitText("import-detail:$id","Background import stopped. Resume this import; transfer checkpoints are preserved.")
            waitText("import-count:$id","1/2 verified in Drive")
            compose.onAllNodesWithText(legacy).assertCountEquals(0)
            assertEquals(legacy,runBlocking { requireNotNull(dao.job(id)).error })
            assertEquals(50L,runBlocking { dao.items(id).single { it.id=="$id-pending" }.offset })
            val recoveryImage=requireNotNull(InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot())
            try {
                val values=android.content.ContentValues().apply {
                    put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"import-recovery.png")
                    put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png")
                    put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
                }
                val uri=requireNotNull(app.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
                requireNotNull(app.contentResolver.openOutputStream(uri)).use { check(recoveryImage.compress(Bitmap.CompressFormat.PNG,100,it)) }
            } finally { recoveryImage.recycle() }
        } finally {
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM import_items WHERE job=?",arrayOf(id))
            (history.map { it.id }+id).forEach {
                app.database.openHelper.writableDatabase.execSQL("DELETE FROM import_jobs WHERE id=?",arrayOf(it))
            }
        }
    }

    @Test fun foregroundImportWorkerCommitsCompletionAndLeavesDismissibleSummary() = runBlocking<Unit> {
        allowImportNotifications()
        val id="notification-device-test"
        val dao=app.database.imports()
        val notices=com.myphotosanywhere.mpa.importing.ImportNotifications(app)
        val work=androidx.work.OneTimeWorkRequestBuilder<com.myphotosanywhere.mpa.importing.ImportWorker>()
            .setInputData(androidx.work.workDataOf("job" to id)).build()
        val manager=androidx.work.WorkManager.getInstance(app)
        try {
            dao.saveJob(com.myphotosanywhere.mpa.importing.ImportJob(id,"synthetic","TAKEOUT",state="RUNNING"))
            manager.enqueue(work).result.get()
            withTimeout(30000) {
                while(manager.getWorkInfoById(work.id).get()?.state?.isFinished!=true) delay(100)
            }
            assertEquals(androidx.work.WorkInfo.State.SUCCEEDED,manager.getWorkInfoById(work.id).get()?.state)
            assertEquals("COMPLETE",dao.job(id)?.state)
            val notifications=app.getSystemService(android.app.NotificationManager::class.java)
            withTimeout(5000) { while(notifications.activeNotifications.none { it.tag==id }) delay(50) }
            val summary=notifications.activeNotifications.single { it.tag==id }.notification
            assertTrue(summary.extras.getCharSequence(android.app.Notification.EXTRA_TEXT).toString().contains("Import complete"))
            assertEquals(0,summary.flags and android.app.Notification.FLAG_ONGOING_EVENT)
        } finally {
            manager.cancelWorkById(work.id).result.get()
            notices.clearSummary(id)
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM import_jobs WHERE id=?",arrayOf(id))
        }
    }

    @Test fun driveConnectionRecoveryPreservesCheckpointsAndPostsWaitingStatus() = runBlocking<Unit> {
        allowImportNotifications()
        val id="connection-device-test"
        val dao=app.database.imports()
        val notices=com.myphotosanywhere.mpa.importing.ImportNotifications(app)
        val item=com.myphotosanywhere.mpa.importing.ImportItem("connection-video",id,"content://original-zip","clip.mp4","clip.mp4","video/mp4",100,
            state="UPLOADING",remoteId="reserved",sessionUri="private-session",offset=42,staged="private-stage",sha256="sha",md5="md5")
        val completed=item.copy(id="connection-complete",state="COMPLETE",offset=100,staged=null,photoId="verified-photo")
        val failure=requireNotNull(com.myphotosanywhere.mpa.drive.DriveConnectionException.from(java.net.UnknownHostException("fixture")))
        try {
            dao.saveJob(com.myphotosanywhere.mpa.importing.ImportJob(id,"original-destination","TAKEOUT",state="RUNNING"))
            dao.insertItem(item); dao.insertItem(completed)
            val recovery=com.myphotosanywhere.mpa.importing.ImportConnectionRecovery(dao)
            assertTrue(recovery.record(id,0,failure))
            assertEquals(item,dao.item(item.id)); assertEquals(completed,dao.item(completed.id))
            assertEquals("RUNNING",dao.job(id)?.state)
            val waiting=com.myphotosanywhere.mpa.importing.ImportProgress.from(requireNotNull(dao.job(id)),dao.items(id)).copy(state="WAITING")
            notices.finish(id,waiting)
            val manager=app.getSystemService(android.app.NotificationManager::class.java)
            withTimeout(5000) { while(manager.activeNotifications.none { it.tag==id }) delay(50) }
            val notification=manager.activeNotifications.single { it.tag==id }.notification
            val content=notification.extras.getCharSequence(android.app.Notification.EXTRA_TEXT).toString()
            assertTrue(content.contains("1/2 verified")); assertTrue(content.contains("Cannot resolve Google Drive"))
            assertTrue(content.contains("Retrying automatically"))
            assertEquals(0,notification.flags and android.app.Notification.FLAG_ONGOING_EVENT)
            assertFalse(recovery.record(id,3,failure))
            assertEquals("FAILED",dao.job(id)?.state)
            assertEquals(item,dao.item(item.id)); assertEquals(completed,dao.item(completed.id))
        } finally {
            notices.clearSummary(id)
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM import_items WHERE job=?",arrayOf(id))
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM import_jobs WHERE id=?",arrayOf(id))
        }
    }

    @Test fun stoppedImportErrorsCanBeDismissedAndFreshFailuresReappear() {
        allowImportNotifications()
        val id="dismiss-device-test"
        val dao=app.database.imports()
        val stage=File(app.cacheDir,"dismiss-device-stage").apply { writeBytes(byteArrayOf(1,2,3)) }
        val source=File(app.cacheDir,"dismiss-device-source.zip").apply { writeBytes(byteArrayOf(4,5,6)) }
        val job=com.myphotosanywhere.mpa.importing.ImportJob(id,"original-destination","TAKEOUT",state="FAILED",error="Old import error")
        val item=com.myphotosanywhere.mpa.importing.ImportItem("dismiss-device-item",id,Uri.fromFile(source).toString(),"clip.mp4","clip.mp4","video/mp4",100,
            state="FAILED",error="Old item error",remoteId="reserved",sessionUri="session",offset=42,staged=stage.absolutePath,sha256="sha",md5="md5")
        val download=com.myphotosanywhere.mpa.importing.TakeoutDownload(id,"encrypted-link",bytes=100,total=500,state="READY",error="Old download error")
        val notices=com.myphotosanywhere.mpa.importing.ImportNotifications(app)
        val notifications=app.getSystemService(android.app.NotificationManager::class.java)
        try {
            runBlocking { dao.saveJob(job); dao.insertItem(item); dao.saveDownload(download) }
            notices.finish(id,com.myphotosanywhere.mpa.importing.ImportProgress.from(job,listOf(item)))
            compose.waitUntil(5000) { notifications.activeNotifications.any { it.tag==id } }
            compose.onNodeWithTag("shell:activity").performClick()
            compose.onNodeWithTag("dismiss-import-errors:$id").performScrollTo().performClick()
            compose.waitUntil(5000) { compose.onAllNodesWithTag("dismiss-import-errors:$id").fetchSemanticsNodes().isEmpty() }
            compose.onNodeWithTag("dismiss-import-errors:$id").assertDoesNotExist()
            compose.waitUntil(5000) { notifications.activeNotifications.none { it.tag==id } }
            runBlocking {
                assertEquals(job.copy(error=null),dao.job(id))
                assertEquals(item.copy(error=null),dao.item(item.id))
                assertEquals(download.copy(error=null),dao.download(id))
            }
            assertTrue(source.isFile); assertTrue(stage.isFile)
            compose.activityRule.scenario.recreate()
            compose.onNodeWithTag("shell:activity").performClick()
            compose.onNodeWithTag("dismiss-import-errors:$id").assertDoesNotExist()
            runBlocking { dao.saveJob(job.copy(error="Fresh import error")) }
            compose.waitUntil(5000) { compose.onAllNodesWithTag("dismiss-import-errors:$id").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("dismiss-import-errors:$id").performScrollTo().assertIsDisplayed()
            compose.onNodeWithText("Fresh import error").assertIsDisplayed()
        } finally {
            notices.clearSummary(id)
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM import_items WHERE job=?",arrayOf(id))
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM takeout_downloads WHERE job=?",arrayOf(id))
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM import_jobs WHERE id=?",arrayOf(id))
            source.delete(); stage.delete()
        }
    }

    @Test fun takeoutZipStreamsOneEntryAndKeepsSupplementalMetadata() = runBlocking<Unit> {
        val archive=File(app.cacheDir,"synthetic-takeout.zip")
        val staged=File(app.cacheDir,"synthetic-stage.jpg")
        val bitmap=Bitmap.createBitmap(16,16,Bitmap.Config.ARGB_8888)
        val bytes=java.io.ByteArrayOutputStream().also { bitmap.compress(Bitmap.CompressFormat.JPEG,90,it) }.toByteArray()
        bitmap.recycle()
        try {
            java.util.zip.ZipOutputStream(archive.outputStream()).use { zip ->
                zip.putNextEntry(java.util.zip.ZipEntry("Takeout/Google Photos/Test/photo.jpg")); zip.write(bytes); zip.closeEntry()
                zip.putNextEntry(java.util.zip.ZipEntry("Takeout/Google Photos/Test/photo.jpg.supplemental-metadata.json"))
                zip.write("{\"title\":\"photo.jpg\",\"description\":\"Device test\",\"photoTakenTime\":{\"timestamp\":\"123\"}}".toByteArray()); zip.closeEntry()
            }
            val documents=com.myphotosanywhere.mpa.importing.TakeoutDocuments(app)
            val manifest=documents.manifest("device-job",Uri.fromFile(archive),false)
            assertEquals(2,manifest.size)
            val media=manifest.single { it.mime=="image/jpeg" }
            assertTrue(media.selected); assertNotNull(media.metadata)
            assertEquals("MATCHED",manifest.single { it.mime=="application/json" }.state)
            documents.stage(media,staged,{})
            assertArrayEquals(bytes,staged.readBytes()); assertTrue(archive.isFile)
        } finally { archive.delete(); staged.delete() }
    }

    @Test fun storageExposesBothGoogleImportActions() {
        compose.onNodeWithTag("shell:storage").performClick()
        compose.onNodeWithTag("management:Import photos").performClick()
        compose.onNodeWithTag("import-photos-picker").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("import-takeout").performScrollTo().assertIsDisplayed()
    }

    @Test fun takeoutUsesDirectDocumentSelectionWithoutUrlForm() {
        val contract=com.myphotosanywhere.mpa.importing.TakeoutSelectionContract()
        val intent=contract.createIntent(app,Unit)
        assertEquals(android.content.Intent.ACTION_OPEN_DOCUMENT,intent.action)
        assertTrue(intent.hasCategory(android.content.Intent.CATEGORY_OPENABLE))
        assertTrue(intent.getBooleanExtra(android.content.Intent.EXTRA_ALLOW_MULTIPLE,false))
        assertTrue(intent.flags and android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
        assertTrue(intent.flags and android.content.Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION != 0)
        compose.onNodeWithTag("shell:storage").performClick()
        compose.onNodeWithTag("management:Import photos").performClick()
        compose.onNodeWithTag("import-takeout").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("takeout-url").assertDoesNotExist()
        compose.onNodeWithTag("takeout-url-input").assertDoesNotExist()
    }

    @Test fun downloadedZipUsesExistingMetadataPipelineAndKeystoreSurvivesRecreation() = runBlocking<Unit> {
        val store=com.myphotosanywhere.mpa.importing.TakeoutArchiveStore(app)
        val archive=store.file("owned-device").apply { parentFile!!.mkdirs() }
        val stage=File(app.cacheDir,"owned-stage.jpg")
        val userSource=File(app.cacheDir,"unrelated-source.zip").apply { writeBytes(byteArrayOf(7,8,9)) }
        val vault=com.myphotosanywhere.mpa.importing.TakeoutLinkVault()
        val link="https://example.test/export.zip?signature=secret"
        val sealed=vault.seal(link)
        assertFalse(sealed.contains("secret"))
        assertEquals(link,com.myphotosanywhere.mpa.importing.TakeoutLinkVault().open(sealed))
        try {
            java.util.zip.ZipOutputStream(archive.outputStream()).use { zip ->
                zip.putNextEntry(java.util.zip.ZipEntry("Takeout/archive_browser.html")); zip.write("<html>Export help</html>".toByteArray()); zip.closeEntry()
                zip.putNextEntry(java.util.zip.ZipEntry("Album/p.jpg")); zip.write(byteArrayOf(1,2,3,4)); zip.closeEntry()
                zip.putNextEntry(java.util.zip.ZipEntry("Album/p.jpg.json")); zip.write("{\"title\":\"p.jpg\",\"description\":\"Kept\",\"photoTakenTime\":{\"timestamp\":\"123\"}}".toByteArray()); zip.closeEntry()
            }
            val documents=com.myphotosanywhere.mpa.importing.TakeoutDocuments(app)
            val manifest=documents.manifest("owned-device",Uri.fromFile(archive),false)
            assertEquals("INFORMATION",manifest.single { it.path=="Takeout/archive_browser.html" }.state)
            val media=manifest.single { it.mime=="image/jpeg" }
            assertTrue(requireNotNull(media.metadata).contains("Kept"))
            documents.stage(media,stage,{})
            assertArrayEquals(byteArrayOf(1,2,3,4),stage.readBytes())
            assertFalse(com.myphotosanywhere.mpa.importing.TakeoutArchiveStore.canClean(manifest))
            assertTrue(com.myphotosanywhere.mpa.importing.TakeoutArchiveStore.canClean(manifest.map { if(it.id==media.id) it.copy(state="COMPLETE") else it }))
            store.delete("owned-device"); assertFalse(archive.exists())
            assertArrayEquals(byteArrayOf(7,8,9),userSource.readBytes())
            try { store.file("../outside"); fail() } catch(_: IllegalArgumentException) { }
        } finally { archive.delete(); stage.delete(); userSource.delete() }
    }

    @Test fun storageShowsSavedDriveDiscoveryAndRefreshAboveSettings() = runBlocking<Unit> {
        val source=com.myphotosanywhere.mpa.drive.DriveSource.from(com.myphotosanywhere.mpa.drive.DriveConnection(
            accountId="progress-device",email="progress@test",mode="EXISTING_LIBRARY",folderId="progress-folder",folderName="Progress"))
        try {
            app.database.drive().saveSource(source)
            app.database.cloud().saveSync(com.myphotosanywhere.mpa.drive.SyncState(source.key,
                scanGeneration="saved-generation",scanToken="before",scanPages=3,status="WAITING"))
            compose.onNodeWithTag("shell:activity").performClick()
            compose.waitUntil(30000) { compose.onAllNodesWithText("3 folder pages saved; discovery will resume.").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("scan-now").assertIsDisplayed()
            compose.onNodeWithText("Drive synchronization").assertIsDisplayed()
            compose.onNodeWithTag("drive-refresh:${source.key}").assertIsDisplayed()
            compose.onNodeWithTag("drive-refresh:${source.key}").assert(hasText("Refresh Drive") or hasText("Retry refresh"))
            compose.onNodeWithText("3 folder pages saved; discovery will resume.").assertExists()
            compose.onNodeWithText("No completed sync yet").assertExists()
        } finally { app.database.drive().disconnectSource(source.key) }
    }

    @Test fun galleryCombinesSourcesAndFiltersIndependentlyOfTheStorageBrowser() = runBlocking<Unit> {
        val a=com.myphotosanywhere.mpa.drive.DriveSource.from(com.myphotosanywhere.mpa.drive.DriveConnection(
            accountId="test-a",email="a@test",mode="APP_FILES",folderId="folder-a",folderName="A"))
        val b=com.myphotosanywhere.mpa.drive.DriveSource.from(com.myphotosanywhere.mpa.drive.DriveConnection(
            accountId="test-b",email="b@test",mode="APP_FILES",folderId="folder-b",folderName="B"))
        val ids=listOf("source-test-a","source-test-b")
        // Keep the two subjects ahead of earlier tests' photos on smaller lazy-grid viewports.
        val fixtureTime=System.currentTimeMillis()
        try {
            app.database.drive().saveSource(a); app.database.drive().saveSource(b)
            app.database.drive().saveConnection(b.connection())
            for((id,source) in ids.zip(listOf(a,b))) app.database.photos().insert(com.myphotosanywhere.mpa.data.Photo(
                id,"$id.jpg","","","image/jpeg",8,fixtureTime,"sha-$id",sourceBindingKey=source.key))
            try {
                compose.waitUntil(30000) { compose.onAllNodes(hasTestTag("photo:${ids[0]}") and isEnabled()).fetchSemanticsNodes().isNotEmpty() &&
                    compose.onAllNodes(hasTestTag("photo:${ids[1]}") and isEnabled()).fetchSemanticsNodes().isNotEmpty() }
            } catch (e: Exception) {
                android.util.Log.e("GalleryFixture", "Synthetic rows: ${ids.map { app.database.photos().find(it) }}")
                compose.onRoot(useUnmergedTree=true).printToLog("GalleryFixture")
                throw e
            }
            compose.onNodeWithTag("shell:filters").performClick()
            compose.onNodeWithText("A · a@test").performScrollTo().performClick()
            compose.onNodeWithText("Done").performScrollTo().performClick()
            compose.waitUntil(30000) { compose.onAllNodes(hasTestTag("photo:${ids[0]}") and isEnabled()).fetchSemanticsNodes().isNotEmpty() &&
                compose.onAllNodesWithTag("photo:${ids[1]}").fetchSemanticsNodes().isEmpty() }
            compose.onNodeWithTag("photo:${ids[0]}").assertExists()
            compose.onNodeWithTag("photo:${ids[1]}").assertDoesNotExist()
            compose.onNodeWithTag("shell:filters").performClick()
            compose.onNodeWithText("B · b@test").performScrollTo().performClick()
            compose.onNodeWithText("Done").performScrollTo().performClick()
            compose.waitUntil(30000) { compose.onAllNodes(hasTestTag("photo:${ids[1]}") and isEnabled()).fetchSemanticsNodes().isNotEmpty() &&
                compose.onAllNodesWithTag("photo:${ids[0]}").fetchSemanticsNodes().isEmpty() }
            compose.onNodeWithTag("photo:${ids[1]}").assertExists()
            compose.onNodeWithTag("photo:${ids[0]}").assertDoesNotExist()
            compose.onNodeWithTag("shell:filters").performClick()
            compose.onNodeWithText("All sources").performScrollTo().performClick()
            compose.onNodeWithText("Done").performScrollTo().performClick()
            compose.waitUntil(30000) { compose.onAllNodes(hasTestTag("photo:${ids[0]}") and isEnabled()).fetchSemanticsNodes().isNotEmpty() &&
                compose.onAllNodes(hasTestTag("photo:${ids[1]}") and isEnabled()).fetchSemanticsNodes().isNotEmpty() }
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
            compose.waitUntil(30000) { compose.onAllNodes(hasTestTag("photo:${photo.id}") and isEnabled()).fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("photo:${photo.id}").performClick()
            compose.onNodeWithTag("viewer-screen").assertIsDisplayed()
            compose.waitUntil(30000) { compose.onAllNodesWithTag("original-image").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("original-image").assertExists()
            compose.onNodeWithTag("viewer-details").performClick()
            compose.waitUntil(30000) { compose.onAllNodesWithTag("photo-edit").fetchSemanticsNodes().isNotEmpty() }
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

    @Test fun googleSetupDetailsDescribeTheInstalledVersionAndSigningCertificate() {
        compose.onNodeWithTag("shell:storage").performClick()
        compose.onNodeWithText("Google setup details").performScrollTo().performClick()
        compose.onNodeWithText("Google setup for this installed app").assertIsDisplayed()
        compose.onNodeWithText("Package: com.myphotosanywhere.mpa",substring=true).assertExists()
        compose.onNodeWithText("MPA 0.5.23 (34)",substring=true).assertExists()
        val info=com.myphotosanywhere.mpa.drive.GoogleSetupInfo.installed(app)
        assertEquals("com.myphotosanywhere.mpa",info.packageName)
        assertTrue(info.sha1.isNotEmpty())
        assertTrue(info.sha1.all { it.matches(Regex("[0-9A-F]{2}(:[0-9A-F]{2}){19}")) })
        compose.onNodeWithText("Copy setup details").performClick()
        val clipboard=app.getSystemService(android.content.Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager
        // Clipboard publication crosses a system-service boundary; await the actual value.
        compose.waitUntil(15000) {
            compose.runOnUiThread { clipboard.primaryClip?.getItemAt(0)?.text?.toString() }==info.text()
        }
        assertEquals(info.text(),compose.runOnUiThread { clipboard.primaryClip?.getItemAt(0)?.text?.toString() })
        compose.onNodeWithText("Close").performClick()
    }

    @Test fun scanActionIsVisibleWithoutScrollingAndRequestsConsent() = runBlocking<Unit> {
        val original=app.settings.state.value
        try {
            app.settings.update(original.copy(faceIndexing=false,objectIndexing=false))
            compose.onNodeWithTag("nav:Collections").performClick()
            compose.onNode(hasText("People") and hasClickAction()).performClick()
            compose.onNodeWithTag("scan-now").assertIsDisplayed().performClick()
            compose.onNodeWithText("Enable on-device scanning").assertIsDisplayed()
            compose.onNodeWithText("Cancel").performClick()
            compose.onNodeWithTag("shell:activity").performClick()
            compose.onNodeWithTag("scan-now").assertIsDisplayed().performClick()
            compose.onNodeWithText("Enable on-device scanning").assertIsDisplayed()
            compose.onNodeWithText("Cancel").performClick()
        } finally { app.settings.update(original) }
    }

    @Test fun peopleExplainMissingRecognitionDataAndOfferARescanWithoutDeletingNames() = runBlocking<Unit> {
        val original=app.settings.state.value
        val photo="diagnostic-photo"; val face="diagnostic-face"
        try {
            app.settings.update(original.copy(faceIndexing=true,indexingPaused=true))
            app.database.library().saveFace(com.myphotosanywhere.mpa.data.FaceRecord(face,photo,"",null,
                modelVersion=com.myphotosanywhere.mpa.people.PeopleRepository.MODEL_VERSION))
            compose.onNodeWithTag("nav:Collections").performClick()
            compose.onNode(hasText("People") and hasClickAction()).performClick()
            // Diagnostics can be below the fold when schedule controls expand on compact devices.
            // Scroll to the actual content rather than assuming the whole header is in the semantics viewport.
            compose.onNodeWithTag("people-list").performScrollToNode(hasText("unassigned faces lack usable matching data",substring=true))
            compose.onNodeWithText("unassigned faces lack usable matching data",substring=true).assertIsDisplayed()
            compose.onNodeWithTag("people-list").performScrollToNode(hasTestTag("face-why:$face"))
            compose.onNodeWithTag("face-why:$face").performScrollTo().performClick()
            compose.waitUntil(30000) { compose.onAllNodesWithText("Face detected, but no usable recognition data",substring=true).fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithText("Close").performClick()
            compose.onNodeWithTag("people-list").performScrollToNode(hasTestTag("rescan-faces"))
            compose.onNodeWithTag("rescan-faces").performScrollTo().performClick()
            compose.onNodeWithText("Rescan all faces?").assertIsDisplayed()
            compose.onNodeWithText("Cancel").performClick()
            assertNotNull(app.database.library().face(face))
            assertTrue(app.settings.state.value.indexingPaused)
        } finally {
            app.database.library().deletePhotoFaces(photo)
            app.settings.update(original)
        }
    }

    @Test fun originalRegionsRetainSinglePixelDetailBeyondTheOld2048Limit() {
        val file=File.createTempFile("full-resolution",".png",app.cacheDir)
        val bitmap=Bitmap.createBitmap(4096,128,Bitmap.Config.ARGB_8888)
        try {
            bitmap.eraseColor(Color.WHITE)
            for(y in 0 until 128) bitmap.setPixel(3001,y,Color.BLACK)
            file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) }
        } finally { bitmap.recycle() }
        try {
            com.myphotosanywhere.mpa.library.OriginalImageSource(file).use { source ->
                assertEquals(4096,source.width)
                assertTrue(source.supportsRegions)
                val rect=android.graphics.Rect(2990,0,3020,128)
                val detail=requireNotNull(source.region(rect,1))
                try {
                    assertEquals(30,detail.width)
                    assertEquals(Color.BLACK,detail.getPixel(11,64))
                    assertEquals(Color.WHITE,detail.getPixel(10,64))
                    assertEquals(Color.WHITE,detail.getPixel(12,64))
                } finally { detail.recycle() }
            }
        } finally { file.delete() }
    }

    @Test fun storageExposesXmpPrivacyAndSavesMidnightScheduleWithPause() = runBlocking<Unit> {
        val original=app.settings.state.value
        try {
            app.settings.update(original.copy(overnightIndexing=false))
            compose.onNodeWithTag("shell:storage").performClick()
            compose.onNodeWithTag("management:Settings").performClick()
            compose.onNodeWithTag("management:Recognition").performClick()
            compose.onNodeWithTag("index-night-switch").performScrollTo().performClick()
            compose.onNodeWithTag("index-start").performScrollTo().performTextReplacement("22:00")
            compose.onNodeWithTag("index-end").performScrollTo().performTextReplacement("06:00")
            compose.onNodeWithText("Save window").performScrollTo().performClick()
            assertTrue(app.settings.state.value.overnightIndexing)
            assertEquals(22*60,app.settings.state.value.nightStart); assertEquals(6*60,app.settings.state.value.nightEnd)
            compose.onNodeWithText("Pause scans").performScrollTo().performClick()
            assertTrue(app.settings.state.value.indexingPaused)
            compose.runOnUiThread { compose.activity.onBackPressedDispatcher.onBackPressed() }
            compose.onNodeWithTag("management:Backup and metadata").performClick()
            compose.waitUntil(10000) {
                compose.runOnUiThread { androidx.core.view.ViewCompat.getRootWindowInsets(compose.activity.window.decorView)?.isVisible(androidx.core.view.WindowInsetsCompat.Type.ime())!=true }
            }
            compose.waitForIdle()
            compose.onNodeWithText("Portable annotations").performScrollTo().assertIsDisplayed()
            compose.onNodeWithText("Back up annotations to connected folders").performScrollTo().assertIsDisplayed()
        } finally { app.settings.update(original); app.people.schedule() }
    }

}

package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import android.graphics.Color
import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.unit.*
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.importing.*
import com.myphotosanywhere.mpa.management.ManagementSettingsMenu
import com.myphotosanywhere.mpa.people.*
import androidx.room.withTransaction
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class ManagementDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    private fun back()=compose.runOnUiThread { compose.activity.onBackPressedDispatcher.onBackPressed() }
    private fun waitTag(tag: String) { compose.waitUntil(20000) { compose.onAllNodes(if(tag.startsWith("photo:") || tag.startsWith("cloud:")) hasTestTag(tag) and isEnabled() else hasTestTag(tag)).fetchSemanticsNodes().isNotEmpty() } }
    @Test fun freeSpacePreviewAndConfirmationSurviveRotationAndCancelKeepsEveryByte()=runBlocking<Unit> {
        val options=app.settings.state.value
        val id="cleanup-menu-"+java.util.UUID.randomUUID()
        val file=File(app.filesDir,"originals/$id.png");val preview=File(app.filesDir,"thumbnails/$id.png")
        val source=DriveSource("$id-source","$id-account","cleanup@example.test","APP_FILES","$id-folder","Cleanup fixture")
        try {
            app.settings.update(options.copy(faceIndexing=false,objectIndexing=false))
            check(requireNotNull(file.parentFile).isDirectory || requireNotNull(file.parentFile).mkdirs())
            check(requireNotNull(preview.parentFile).isDirectory || requireNotNull(preview.parentFile).mkdirs())
            val image=Bitmap.createBitmap(96,128,Bitmap.Config.ARGB_8888)
            try { image.eraseColor(Color.BLUE);file.outputStream().use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { image.recycle() }
            file.copyTo(preview)
            val hash=DriveBackupWorker.digest(file,"SHA-256")
            val photo=Photo(id,"Camera.png",file.path,preview.path,"image/png",file.length(),1,hash,
                sourceBindingKey=source.key,sourceRemoteId=id,cachedVersion="1",favorite=true,labels="preserved")
            app.database.drive().saveSource(source);app.database.photos().insert(photo)
            app.database.cloud().savePhoto(CloudPhoto("${source.key}:$id",source.key,id,source.folderId,photo.displayName,photo.mimeType,photo.bytes,
                DriveBackupWorker.digest(file,"MD5"),"1",null,null,null,null,photoId=id,sha256=hash))
            compose.onNodeWithTag("shell:storage").performClick();waitTag("management:Free up space")
            compose.onNodeWithTag("management:Free up space").performScrollTo().performClick();waitTag("offline:estimate")
            compose.onNodeWithTag("offline:clear").performScrollTo().assertIsEnabled()
            screenshotManagement("offline-cleanup-preview")
            compose.onNodeWithTag("offline:clear").performClick();waitTag("offline:confirm")
            compose.onNodeWithTag("offline:confirm").assertIsDisplayed();screenshotManagement("offline-cleanup-confirm")
            val model=androidx.lifecycle.ViewModelProvider(compose.activity)[com.myphotosanywhere.mpa.library.OfflineStorageModel::class.java]
            val cutoff=checkNotNull(model.summary.value).cutoff
            compose.activityRule.scenario.recreate();waitTag("offline:confirm")
            val restored=androidx.lifecycle.ViewModelProvider(compose.activity)[com.myphotosanywhere.mpa.library.OfflineStorageModel::class.java]
            compose.waitUntil(10000) { !restored.loading.value }
            assertSame(model,restored);assertEquals(cutoff,restored.summary.value?.cutoff)
            compose.onNodeWithTag("offline:cancel").performClick()
            assertEquals(photo,app.database.photos().find(id));assertEquals(hash,DriveBackupWorker.digest(file,"SHA-256"));assertTrue(preview.isFile)
            compose.onNodeWithTag("offline:confirm").assertDoesNotExist()
            compose.runOnUiThread { restored.clear(cutoff-1) }
            waitTag("offline:result");compose.onNodeWithTag("offline:result").performScrollTo().assertTextContains("estimate changed")
            assertFalse(restored.working.value);assertEquals(photo,app.database.photos().find(id));assertEquals(hash,DriveBackupWorker.digest(file,"SHA-256"))
        } finally {
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM cloud_photos WHERE bindingKey=?",arrayOf(source.key))
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM photos WHERE id=?",arrayOf(id))
            app.database.drive().disconnectSource(source.key);app.settings.update(options)
            check(!file.exists() || file.delete());check(!preview.exists() || preview.delete())
        }
    }
    @Test fun sourcesSettingsAndFocusedPagesRestoreTheirOriginAcrossRotation() {
        compose.onNodeWithTag("nav:Collections").performClick();compose.onNodeWithTag("shell:storage").performClick()
        compose.onNodeWithText("MyPhotosAnywhere").assertIsDisplayed();compose.onNodeWithTag("shell:navigation").assertDoesNotExist();screenshotManagement("management-sources")
        compose.onNodeWithTag("management:Settings").performClick()
        waitTag("management:10");compose.onNodeWithTag("management:Storage and cache").assertIsDisplayed();compose.waitForIdle();screenshotManagement("management-menu");compose.onNodeWithTag("management:Storage and cache").performClick()
        compose.onNodeWithText("Offline storage").assertIsDisplayed();back()
        compose.onNodeWithTag("management:Preferences").performClick();compose.onNodeWithTag("management:Recognition").performClick();compose.onNodeWithTag("scan-now").assertIsDisplayed();back();back()
        compose.onNodeWithTag("management:Backup and metadata").performClick();compose.onNodeWithText("Portable annotations").assertIsDisplayed();back()
        compose.onNodeWithTag("management:Privacy and permissions").performClick();compose.onNodeWithText("Allow online maps").assertIsDisplayed();back()
        compose.onNodeWithTag("management:About MPA").performClick();compose.onNodeWithText("MPA 0.5.33 (44)",substring=true).assertExists()
        compose.activityRule.scenario.recreate();compose.onNodeWithText("MPA 0.5.33 (44)",substring=true).assertExists();back();back();back()
        compose.onNodeWithTag("nav:Collections").assertIsSelected()
    }
    @Test fun photoViewPreferencesRestoreTheGalleryIdentityWithoutChangingSourcesOrRecognition()=runBlocking<Unit> {
        val originalLayout=app.galleryPresentation.layout.value;val originalDensity=app.galleryPresentation.density.value
        val options=app.settings.state.value;val sources=app.database.drive().sources()
        val prefix="menu-anchor-"+java.util.UUID.randomUUID();val ids=(0 until 90).map { "$prefix-$it" }
        val file=File.createTempFile("menu-anchor-",".png",app.cacheDir);val image=Bitmap.createBitmap(96,96,Bitmap.Config.ARGB_8888)
        try { image.eraseColor(Color.GREEN);file.outputStream().use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { image.recycle() }
        val hash=DriveBackupWorker.digest(file,"SHA-256");val time=java.time.Instant.parse("2201-01-01T00:00:00Z").toEpochMilli()
        try {
            app.galleryPresentation.updateLayout(com.myphotosanywhere.mpa.timeline.GalleryLayout.SQUARE)
            app.galleryPresentation.update(com.myphotosanywhere.mpa.timeline.GridDensity.DEFAULT)
            app.database.withTransaction { ids.forEachIndexed { i,id -> app.database.photos().insert(Photo(id,"$id.png",file.path,file.path,"image/png",file.length(),time-i,hash)) } }
            waitTag("photo:${ids.first()}");compose.onNodeWithTag("photos:grid").performScrollToNode(hasTestTag("photo:${ids[35]}"));waitTag("photo:${ids[35]}")
            repeat(2) { InstrumentationRegistry.getInstrumentation().sendKeyDownUpSync(android.view.KeyEvent.KEYCODE_TAB) }
            compose.waitForIdle();compose.onNodeWithTag("shell:storage").assertIsDisplayed().performClick();waitTag("management:Settings")
            compose.onNodeWithTag("management:Settings").performClick()
            compose.onNodeWithTag("management:Preferences").performClick();compose.onNodeWithTag("management:Photos view").performClick()
            compose.onNodeWithTag("preference:layout:SQUARE").assertIsSelected();compose.onNodeWithTag("shell:navigation").assertDoesNotExist()
            screenshotManagement("menu-photo-view");compose.onNodeWithTag("preference:layout:ROWS").performClick()
            compose.waitUntil(10000) { app.galleryPresentation.layout.value==com.myphotosanywhere.mpa.timeline.GalleryLayout.ROWS }
            compose.activityRule.scenario.recreate();compose.onNodeWithTag("preference:layout:ROWS").assertIsSelected()
            repeat(4) { back();compose.waitForIdle() };waitTag("photo:${ids[35]}");compose.onNodeWithTag("photo:${ids[35]}").assertIsDisplayed()
            screenshotManagement("menu-gallery-return");assertEquals(options,app.settings.state.value);assertEquals(sources,app.database.drive().sources())
            assertEquals(hash,DriveBackupWorker.digest(file,"SHA-256"))
            ids.forEach { assertEquals(hash,app.database.photos().find(it)?.sha256) }
        } finally {
            app.database.withTransaction { ids.forEach { app.database.openHelper.writableDatabase.execSQL("DELETE FROM photos WHERE id=?",arrayOf(it)) } }
            file.delete();app.galleryPresentation.updateLayout(originalLayout);app.galleryPresentation.update(originalDensity)
        }
    }
    @Test fun activityDetailsDoNotMutateCapturedJobsOrChangeNewImportDestination() {
        val source=DriveSource("management-destination","management-account","management@test","APP_FILES","management-folder","Management fixture",backupEnabled=true)
        val job=ImportJob("management-paused","captured-old-source","TAKEOUT",state="PAUSED",error="Source needs reconnection")
        try {
            runBlocking { app.database.drive().saveSource(source);app.database.imports().saveJob(job) }
            compose.onNodeWithTag("shell:activity").performClick();waitTag("import:details:${job.id}")
            compose.onNodeWithTag("import:details:${job.id}").performScrollTo().performClick()
            compose.onNodeWithText("Import selected / Resume").performScrollTo().assertIsDisplayed()
            compose.onNodeWithTag("import-progress:${job.id}").performScrollTo();screenshotManagement("management-activity")
            compose.onNodeWithTag("management:Import photos").performScrollTo().performClick()
            compose.onNodeWithText("New import destination: Management fixture (management@test)").performScrollTo().assertIsDisplayed()
            compose.onNodeWithTag("import-takeout").performScrollTo().assertIsDisplayed();compose.onNodeWithTag("takeout-url-input").assertDoesNotExist()
            screenshotManagement("management-import");compose.activityRule.scenario.recreate()
            compose.onNodeWithTag("import-takeout").performScrollTo().assertIsDisplayed();runBlocking { assertEquals(job,app.database.imports().job(job.id)) }
        } finally { runBlocking { app.database.drive().disconnectSource(source.key) };app.database.openHelper.writableDatabase.execSQL("DELETE FROM import_jobs WHERE id=?",arrayOf(job.id)) }
    }
    @Test fun sourceDisconnectRequiresExplicitScopeConfirmationAndCancelPreservesData() {
        val source=DriveSource("management-disconnect","management-disconnect","disconnect@test","APP_FILES","disconnect-folder","Disconnect fixture")
        try {
            runBlocking { app.database.drive().saveSource(source);app.database.drive().saveConnection(source.connection()) }
            compose.onNodeWithTag("shell:storage").performClick();waitTag("source:manage:${source.key}");compose.onNodeWithTag("source:manage:${source.key}").performScrollTo().performClick();compose.waitUntil(20000) { compose.onAllNodes(hasTestTag("source:remove") and isEnabled()).fetchSemanticsNodes().size==1 };compose.onNodeWithTag("source:remove").performScrollTo().assertTextEquals("Remove this source").assertIsEnabled().performClick()
            compose.onNodeWithText("Disconnect source").assertIsDisplayed();compose.onNodeWithText("Drive originals are not deleted.",substring=true).assertExists()
            compose.onNodeWithText("Keep source").performClick();runBlocking { assertEquals(source,app.database.drive().source(source.key)) }
        } finally { runBlocking { app.database.drive().disconnectSource(source.key);app.database.drive().disconnect() } }
    }
    @Test fun importedNamesUseTheSamePersonChooserButFacesRequireConfirmation() {
        val id="management-naming-fixture";val faceId="$id-face"
        val raw="""{"title":"naming.jpg","photoTakenTime":{"timestamp":"1"},"people":[{"name":"Imported fixture name"}]}"""
        val codec=TakeoutCodec();val photo=codec.apply(Photo(id,"naming.jpg","","","image/jpeg",1,1,"naming-hash"),codec.parse(raw))
        try {
            val canonical=runBlocking {
                app.database.photos().insert(photo);app.database.library().saveFace(FaceRecord(faceId,id,"",null))
                app.personLabels.drain();app.people.refreshSuggestions()
                requireNotNull(app.database.personLabels().alias(ImportedPersonLabel("Imported fixture name",null).key)?.personId)
            }
            compose.onNodeWithTag("nav:Collections").performClick();compose.onNodeWithTag("collections:People").performClick()
            val features=androidx.lifecycle.ViewModelProvider(compose.activity)[com.myphotosanywhere.mpa.library.LibraryFeaturesModel::class.java]
            compose.waitUntil(10000) { features.faces.value.any { it.id==faceId } };compose.waitForIdle()
            compose.onNodeWithTag("people-list").performScrollToNode(hasTestTag("person-review:$canonical"))
            compose.onNodeWithTag("person-review:$canonical").performClick();compose.onNodeWithText("Name these faces").assertIsDisplayed()
            compose.onNodeWithText("Use Imported fixture name").assertExists()
            runBlocking { assertNull(app.database.library().face(faceId)?.personId) }
            compose.onAllNodes(hasText("Imported people") and hasAnyAncestor(isDialog())).assertCountEquals(0)
            compose.onAllNodes(hasTestTag("imported-person:${ImportedPersonLabel("Imported fixture name",null).key}") and hasAnyAncestor(isDialog())).assertCountEquals(0)
            compose.onNodeWithText("Confirm name").performClick()
            compose.waitUntil(10000) { runBlocking { app.database.library().face(faceId)?.personId==canonical } }
            runBlocking { assertEquals(photo,app.database.photos().find(id));assertEquals(canonical,app.database.library().face(faceId)?.personId);assertTrue(app.database.library().face(faceId)?.corrected==true) }
        } finally { runBlocking { app.database.library().deletePhotoFaces(id);app.database.photos().update(photo.copy(archived=true)) } }
    }
    @Test fun takeoutZipPeopleAppearWithoutRecognitionAndBrowseWithPreservedDetailsAndMetadata() {
        val originalSettings=app.settings.state.value
        val file=File(app.cacheDir,"takeout-people-device.png");val archive=File(app.cacheDir,"takeout-people-device.zip")
        val bitmap=Bitmap.createBitmap(100,100,Bitmap.Config.ARGB_8888)
        try { bitmap.eraseColor(Color.rgb(100,160,200));file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { bitmap.recycle() }
        val raw="""{"title":"photo.png","photoTakenTime":{"timestamp":"1790000000"},"people":[{"name":"כינרת Exported"},{"name":"Sam Exported"}],"unknown":{"preserved":true}}"""
        val id="takeout-people-device";var saved: Photo?=null
        try {
            app.settings.update(originalSettings.copy(faceIndexing=false,objectIndexing=false))
            java.util.zip.ZipOutputStream(archive.outputStream()).use { zip ->
                zip.putNextEntry(java.util.zip.ZipEntry("Album/photo.png"));zip.write(file.readBytes());zip.closeEntry()
                zip.putNextEntry(java.util.zip.ZipEntry("Album/photo.png.supplemental-metadata.json"));zip.write(raw.toByteArray());zip.closeEntry()
            }
            val media=runBlocking { TakeoutDocuments(app).manifest(id,Uri.fromFile(archive),false) }.single { it.mime=="image/png" }
            val codec=TakeoutCodec();val photo=codec.apply(Photo(id,"photo.png",file.path,file.path,"image/png",file.length(),System.currentTimeMillis(),DriveBackupWorker.digest(file,"SHA-256")),codec.parse(requireNotNull(media.metadata)))
            saved=photo;runBlocking { app.database.photos().insert(photo) }
            val label=ImportedPersonLabel("כינרת Exported",null)
            val canonical=runBlocking { app.personLabels.drain();requireNotNull(app.database.personLabels().alias(label.key)?.personId) }
            val second=runBlocking { requireNotNull(app.database.personLabels().alias(ImportedPersonLabel("Sam Exported",null).key)?.personId) }
            compose.onNodeWithTag("nav:Collections").performClick();compose.onNodeWithTag("collections:People").performClick()
            compose.waitUntil(20000) {
                try {
                    compose.onNodeWithTag("people-list").performScrollToNode(hasTestTag("person:$canonical"))
                    compose.onAllNodesWithTag("person:$canonical").fetchSemanticsNodes().isNotEmpty()
                } catch(_: AssertionError) { false }
            }
            compose.onNodeWithTag("person:$canonical").assertIsDisplayed()
            screenshotManagement("takeout-imported-people");compose.onNodeWithTag("person:$canonical").performClick();waitTag("photo:$id")
            compose.onNodeWithTag("photo:$id").performClick();waitTag("viewer-details");compose.onNodeWithTag("viewer-details").performClick()
            waitTag("details:person:$canonical");compose.onNodeWithTag("details:person:$canonical").performScrollTo().assertTextContains("כינרת Exported")
            compose.onNodeWithTag("details:person:$second").performScrollTo().assertTextContains("Sam Exported")
            compose.onAllNodesWithText("Imported people labels:",substring=true).assertCountEquals(0)
            compose.onNodeWithTag("photo-details-close").performClick();compose.onNodeWithTag("viewer-close").performClick();waitTag("photo:$id")
            compose.onNodeWithTag("nav:Search").performClick();compose.onNodeWithTag("search:query").performImeAction()
            compose.waitUntil(10000) {
                compose.runOnUiThread { androidx.core.view.ViewCompat.getRootWindowInsets(compose.activity.window.decorView)?.isVisible(androidx.core.view.WindowInsetsCompat.Type.ime())!=true }
            }
            back();waitTag("photo:$id")
            compose.activityRule.scenario.recreate();waitTag("photo:$id");back();waitTag("person:$canonical")
            runBlocking {
                val after=requireNotNull(app.database.photos().find(id))
                assertTrue("Viewing updates only the existing cache-access checkpoint",after.lastAccess>photo.lastAccess)
                assertEquals(photo.copy(lastAccess=after.lastAccess),after)
                assertFalse(app.database.library().allFaces().any { it.photoId==id })
            }
            assertFalse(app.settings.state.value.faceIndexing);assertTrue(archive.isFile)
            compose.onNodeWithTag("people-list").performScrollToNode(hasTestTag("manage-person:$canonical"))
            compose.onNodeWithTag("manage-person:$canonical").performClick();compose.onNodeWithTag("person:rename").performClick()
            compose.onNodeWithTag("person:name").performTextReplacement("Shared Exported")
            compose.activityRule.scenario.recreate();waitTag("person:name")
            compose.onNodeWithTag("person:name").assertTextContains("Shared Exported")
            compose.onNodeWithTag("person:save").performClick()
            compose.waitUntil(10000) { runBlocking { app.database.personLabels().person(canonical)?.name=="Shared Exported" } }
            compose.onNodeWithTag("people-list").performScrollToNode(hasTestTag("manage-person:$second"))
            compose.onNodeWithTag("manage-person:$second").performClick();compose.onNodeWithTag("person:merge").performClick()
            compose.onNodeWithTag("person:find").performScrollTo().performTextInput("Shared Exported")
            compose.onNodeWithTag("person:choices").performScrollToNode(hasTestTag("person:merge-target:$canonical"))
            compose.onNodeWithTag("person:merge-target:$canonical").performClick();compose.onNodeWithTag("person:confirm-merge").performClick()
            compose.waitUntil(10000) { runBlocking { app.database.personLabels().person(second)==null } }
            runBlocking {
                assertEquals(canonical,app.database.personLabels().alias(ImportedPersonLabel("Sam Exported",null).key)?.personId)
                val newPhoto=photo.copy(id="$id-new",metadataAssetId=null,lastAccess=0)
                app.database.photos().insert(newPhoto);app.personLabels.drain()
                assertEquals(setOf(canonical),app.database.personLabels().members(newPhoto.id).map { it.personId }.toSet())
                assertEquals(photo.importedMetadata,app.database.photos().find(id)?.importedMetadata)
                assertEquals(2,com.myphotosanywhere.mpa.people.CanonicalPeopleRepository(app.timeline).groups().single { it.person.id==canonical }.photos)
            }
            compose.onNodeWithTag("people-list").performScrollToNode(hasTestTag("person:$canonical"))
            compose.onNodeWithTag("person:$canonical").assertTextContains("Shared Exported")
            screenshotManagement("people-unified-merged")
        } finally { runBlocking {
            saved?.let { app.database.photos().update(it.copy(archived=true)) }
            app.database.photos().find("$id-new")?.let { app.database.photos().update(it.copy(archived=true)) }
        };file.delete();archive.delete();app.settings.update(originalSettings);app.people.schedule() }
    }
}

@RunWith(AndroidJUnit4::class)
class ManagementChromeDeviceTest {
    @get:Rule val compose=createComposeRule()
    @Test fun cleanupConfirmationRemainsReadableAndActionableInLightLargeText()=verifyCleanup(false,LayoutDirection.Ltr)
    @Test fun cleanupConfirmationRemainsReadableAndActionableInDarkRtlLargeText()=verifyCleanup(true,LayoutDirection.Rtl)
    private fun verifyCleanup(dark: Boolean,direction: LayoutDirection) {
        var pixels=0f;var confirmed: Long?=null;var dismissed=false
        compose.setContent {
            pixels=LocalDensity.current.density
            CompositionLocalProvider(LocalDensity provides Density(pixels,2f),LocalLayoutDirection provides direction) {
                MaterialTheme(colorScheme=if(dark) darkColorScheme() else lightColorScheme()) {
                    com.myphotosanywhere.mpa.library.OfflineCleanupConfirmation(123,onConfirm={ confirmed=it },onDismiss={ dismissed=true })
                }
            }
        }
        for(tag in listOf("offline:confirm","offline:cancel")) {
            val bounds=compose.onNodeWithTag(tag).assertIsDisplayed().fetchSemanticsNode().boundsInRoot
            assertTrue(bounds.width>=48*pixels-1);assertTrue(bounds.height>=48*pixels-1)
        }
        compose.onNodeWithTag("offline:protection").performTouchInput { swipeUp() }
        screenshotManagement(if(dark) "offline-cleanup-dark-rtl-large" else "offline-cleanup-light-large")
        compose.onNodeWithTag("offline:cancel").performClick();assertTrue(dismissed);assertNull(confirmed)
        compose.onNodeWithTag("offline:confirm").performClick();assertEquals(123L,confirmed)
    }
    @Test fun managementTargetsRemainUsableInLightLargeText()=verify(false,LayoutDirection.Ltr)
    @Test fun managementTargetsRemainUsableInDarkRtlLargeText()=verify(true,LayoutDirection.Rtl)
    private fun verify(dark: Boolean,direction: LayoutDirection) {
        var pixels=0f;var opened: String?=null
        compose.setContent {
            pixels=LocalDensity.current.density
            CompositionLocalProvider(LocalDensity provides Density(pixels,2f),LocalLayoutDirection provides direction) {
                MaterialTheme(colorScheme=if(dark) darkColorScheme() else lightColorScheme()) {
                    Surface { Column(Modifier.width(320.dp).fillMaxHeight().verticalScroll(rememberScrollState()).padding(16.dp)) { ManagementSettingsMenu { opened=it.title } } }
                }
            }
        }
        for(title in listOf("Preferences","Backup and metadata","Storage and cache","Privacy and permissions","About MPA")) {
            val node=compose.onNodeWithTag("management:$title").performScrollTo().assertIsDisplayed();val bounds=node.fetchSemanticsNode().boundsInRoot
            assertTrue(bounds.width>=48*pixels-1);assertTrue(bounds.height>=48*pixels-1);node.performClick();assertEquals(title,opened)
        }
        screenshotManagement(if(dark) "management-dark-rtl-large" else "management-light-large")
    }
}

private fun screenshotManagement(name: String) {
    val instrumentation=InstrumentationRegistry.getInstrumentation();val image=requireNotNull(instrumentation.uiAutomation.takeScreenshot());val context=instrumentation.targetContext
    try {
        val values=android.content.ContentValues().apply { put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"$name.png");put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png");put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA") }
        val uri=requireNotNull(context.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values));requireNotNull(context.contentResolver.openOutputStream(uri)).use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) }
    } finally { image.recycle() }
}

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
    @Test fun sourcesSettingsAndFocusedPagesRestoreTheirOriginAcrossRotation() {
        compose.onNodeWithTag("nav:Collections").performClick();compose.onNodeWithTag("shell:storage").performClick()
        compose.onNodeWithText("MyPhotosAnywhere").assertIsDisplayed();screenshotManagement("management-sources")
        compose.onNodeWithTag("management:Settings").performClick()
        waitTag("management:10");compose.onNodeWithTag("management:Storage and cache").assertIsDisplayed();compose.waitForIdle();screenshotManagement("management-menu");compose.onNodeWithTag("management:Storage and cache").performClick()
        compose.onNodeWithText("Offline storage").assertIsDisplayed();back()
        compose.onNodeWithTag("management:Recognition").performClick();compose.onNodeWithTag("scan-now").assertIsDisplayed();back()
        compose.onNodeWithTag("management:Backup and metadata").performClick();compose.onNodeWithText("Portable annotations").assertIsDisplayed();back()
        compose.onNodeWithTag("management:Privacy and permissions").performClick();compose.onNodeWithText("Allow online maps").assertIsDisplayed();back()
        compose.onNodeWithTag("management:About MPA").performClick();compose.onNodeWithText("MPA 0.5.20 (31)",substring=true).assertExists()
        compose.activityRule.scenario.recreate();compose.onNodeWithText("MPA 0.5.20 (31)",substring=true).assertExists();back();back();back()
        compose.onNodeWithTag("nav:Collections").assertIsSelected()
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
            compose.onNodeWithTag("shell:storage").performClick();compose.waitUntil(20000) { compose.onAllNodes(hasTestTag("source:remove") and isEnabled()).fetchSemanticsNodes().size==1 };compose.onNodeWithTag("source:remove").performScrollTo().assertTextEquals("Remove this source").assertIsEnabled().performClick()
            compose.onNodeWithText("Disconnect source").assertIsDisplayed();compose.onNodeWithText("Drive originals are not deleted.",substring=true).assertExists()
            compose.onNodeWithText("Keep source").performClick();runBlocking { assertEquals(source,app.database.drive().source(source.key)) }
        } finally { runBlocking { app.database.drive().disconnectSource(source.key);app.database.drive().disconnect() } }
    }
    @Test fun importedLabelsNeverEnterTheReviewedFaceNamingDialog() {
        val id="management-naming-fixture";val faceId="$id-face"
        val raw="""{"title":"naming.jpg","photoTakenTime":{"timestamp":"1"},"people":[{"name":"Imported fixture name"}]}"""
        val codec=TakeoutCodec();val photo=codec.apply(Photo(id,"naming.jpg","","","image/jpeg",1,1,"naming-hash"),codec.parse(raw))
        try {
            runBlocking { app.database.photos().insert(photo);app.database.library().saveFace(FaceRecord(faceId,id,"",null)) }
            compose.onNodeWithTag("nav:Collections").performClick();compose.onNodeWithTag("collections:People").performClick()
            val features=androidx.lifecycle.ViewModelProvider(compose.activity)[com.myphotosanywhere.mpa.library.LibraryFeaturesModel::class.java]
            compose.waitUntil(10000) { features.faces.value.any { it.id==faceId } };compose.waitForIdle()
            compose.onNodeWithTag("people-list").performScrollToNode(hasTestTag("name-faces:$faceId"))
            compose.onNodeWithTag("name-faces:$faceId").performClick();compose.onNodeWithText("Name these faces").assertIsDisplayed()
            compose.onAllNodes(hasText("Imported people") and hasAnyAncestor(isDialog())).assertCountEquals(0)
            compose.onAllNodes(hasTestTag("imported-person:${ImportedPersonLabel("Imported fixture name",null).key}") and hasAnyAncestor(isDialog())).assertCountEquals(0)
            compose.onNodeWithText("Cancel").performClick()
            runBlocking { assertEquals(photo,app.database.photos().find(id));assertNull(app.database.library().face(faceId)?.personId) }
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
            compose.onNodeWithTag("nav:Collections").performClick();compose.onNodeWithTag("collections:People").performClick()
            waitTag("imported-person:${label.key}");compose.onNodeWithTag("imported-person:${label.key}").performScrollTo().assertIsDisplayed()
            screenshotManagement("takeout-imported-people");compose.onNodeWithTag("imported-person:${label.key}").performClick();waitTag("photo:$id")
            compose.onNodeWithTag("photo:$id").performClick();waitTag("viewer-details");compose.onNodeWithTag("viewer-details").performClick()
            waitTag("details:imported-people");compose.onNodeWithTag("details:imported-people").performScrollTo().assertTextEquals("Imported people labels: כינרת Exported, Sam Exported")
            compose.onNodeWithTag("photo-details-close").performClick();compose.onNodeWithTag("viewer-close").performClick();waitTag("photo:$id")
            compose.onNodeWithTag("nav:Search").performClick();compose.onNodeWithTag("search:query").performImeAction()
            compose.waitUntil(10000) {
                compose.runOnUiThread { androidx.core.view.ViewCompat.getRootWindowInsets(compose.activity.window.decorView)?.isVisible(androidx.core.view.WindowInsetsCompat.Type.ime())!=true }
            }
            back();waitTag("photo:$id")
            compose.activityRule.scenario.recreate();waitTag("photo:$id");back();waitTag("imported-person:${label.key}")
            runBlocking {
                val after=requireNotNull(app.database.photos().find(id))
                assertTrue("Viewing updates only the existing cache-access checkpoint",after.lastAccess>photo.lastAccess)
                assertEquals(photo.copy(lastAccess=after.lastAccess),after)
                assertFalse(app.database.library().allFaces().any { it.photoId==id })
            }
            assertFalse(app.settings.state.value.faceIndexing);assertTrue(archive.isFile)
        } finally { runBlocking { saved?.let { app.database.photos().update(it.copy(archived=true)) } };file.delete();archive.delete();app.settings.update(originalSettings);app.people.schedule() }
    }
}

@RunWith(AndroidJUnit4::class)
class ManagementChromeDeviceTest {
    @get:Rule val compose=createComposeRule()
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
        for(group in listOf("Library","Backup and privacy","Application")) compose.onNodeWithText(group).assertExists()
        for(title in listOf("Storage and cache","Recognition","Backup and metadata","Privacy and permissions","About MPA")) {
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

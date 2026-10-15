package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.provider.MediaStore
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.Photo
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class ShellNavigationDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    private fun back()=compose.runOnIdle { compose.activity.onBackPressedDispatcher.onBackPressed() }

    @Test fun collectionsKeepPeoplePlacesAndAiActivityReachableWithBackHistory() {
        compose.onNodeWithTag("nav:Collections").performClick().assertIsSelected()
        compose.onNode(hasText("People") and hasClickAction()).performClick()
        compose.onNodeWithTag("scan-now").assertIsDisplayed()
        back()
        compose.onNode(hasText("Places") and hasClickAction()).assertIsDisplayed().performClick()
        back()
        compose.onNodeWithTag("collections:grid").performScrollToNode(hasText("AI activity"))
        compose.onNodeWithText("AI activity").performClick()
        compose.onNodeWithText("AI activity").assertIsDisplayed()
        back()
        compose.onNodeWithTag("shell:storage").performClick()
        compose.onNodeWithTag("management:Import photos").performClick()
        compose.onNodeWithText("Import Google Photos").assertExists()
        back()
        back()
        compose.onNodeWithTag("collections:grid").performScrollToNode(hasTestTag("collections:People"))
        compose.onNode(hasText("People") and hasClickAction()).assertIsDisplayed()
        compose.onNodeWithTag("nav:Collections").assertIsSelected()
    }

    @Test fun searchQueryRestoresAcrossRecreationAndDoesNotFilterPhotos() {
        compose.onNodeWithTag("nav:Search").performClick()
        compose.onNodeWithTag("search:query").performTextInput("no-match-shell-fixture-93817")
        compose.onNodeWithTag("search:query").performImeAction()
        compose.activityRule.scenario.recreate()
        compose.onNodeWithTag("nav:Search").assertIsSelected()
        compose.onNodeWithTag("search:query").assertTextContains("no-match-shell-fixture-93817")
        compose.onNodeWithTag("nav:Photos").performClick()
        compose.onNodeWithTag("search:query").assertDoesNotExist()
        compose.onNodeWithText("No matching photos").assertDoesNotExist()
        compose.onNodeWithTag("nav:Search").performClick()
        compose.onNodeWithTag("search:query").assertTextContains("no-match-shell-fixture-93817")
    }

    @Test fun pausedImportStaysDiscoverableWithoutChangingItsCapturedDestination() {
        val id="shell-paused-import"
        val job=com.myphotosanywhere.mpa.importing.ImportJob(id,"captured-source","TAKEOUT",state="PAUSED")
        try {
            runBlocking { app.database.imports().saveJob(job) }
            compose.waitUntil(10000) { compose.onAllNodesWithTag("shell:import-activity").fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithTag("shell:import-activity").assertIsDisplayed().performClick()
            compose.onNodeWithText("Imports",useUnmergedTree=true).assertExists()
            runBlocking { assertEquals(job,app.database.imports().job(id)) }
            back()
            compose.onNodeWithTag("shell:import-activity").assertIsDisplayed()
        } finally {
            app.database.openHelper.writableDatabase.execSQL("DELETE FROM import_jobs WHERE id=?",arrayOf(id))
        }
    }

    @Test fun photoFirstViewportAndViewerReturnPreserveMediaAndScroll() {
        val id="shell-grid-fixture"
        val file=File(app.cacheDir,"$id.png")
        val bitmap=Bitmap.createBitmap(320,320,Bitmap.Config.ARGB_8888)
        val canvas=Canvas(bitmap)
        canvas.drawColor(Color.rgb(116,183,221))
        canvas.drawCircle(230f,80f,40f,Paint().apply { color=Color.rgb(250,206,109) })
        canvas.drawRect(0f,200f,320f,320f,Paint().apply { color=Color.rgb(53,119,85) })
        try { file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } }
        finally { bitmap.recycle() }
        val ids=(0..14).map { "$id-$it" }
        val fixtureTime=System.currentTimeMillis()+100000
        try {
            runBlocking {
                ids.forEachIndexed { index,key -> app.database.photos().insert(Photo(key,"$key.png",file.absolutePath,file.absolutePath,
                    "image/png",file.length(),fixtureTime-index,"shell-$index")) }
            }
            compose.waitUntil(10000) { compose.onAllNodes(hasTestTag("photo:${ids.first()}") and isEnabled()).fetchSemanticsNodes().isNotEmpty() }
            compose.onNodeWithText("Your photos. Your storage.").assertDoesNotExist()
            compose.onNodeWithText("Search names, people, places, dates, objects").assertDoesNotExist()
            compose.onNodeWithText("All sources").assertDoesNotExist()
            compose.onNodeWithTag("photo:${ids.first()}").assertIsDisplayed()
            compose.onNodeWithTag("photos:grid").performScrollToIndex(9)
            compose.onNodeWithTag("photo:${ids[9]}").assertIsDisplayed().performClick()
            compose.onNodeWithTag("viewer-screen").assertIsDisplayed()
            compose.onNodeWithTag("viewer-close").performClick()
            compose.onNodeWithTag("photo:${ids[9]}").assertIsDisplayed()
            compose.onNodeWithTag("nav:Collections").performClick()
            compose.onNodeWithTag("nav:Photos").performClick()
            compose.onNodeWithTag("photo:${ids[9]}").assertIsDisplayed()
            val gridBounds=compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInRoot
            val navigationBounds=compose.onNodeWithTag("shell:navigation").fetchSemanticsNode().boundsInRoot
            assertTrue("Photos extend beneath floating navigation",gridBounds.bottom>navigationBounds.top)
            compose.onNodeWithTag("photos:grid").performScrollToIndex(ids.lastIndex)
            compose.waitForIdle()
            val lastBounds=compose.onNodeWithTag("photo:${ids.last()}").fetchSemanticsNode().boundsInRoot
            assertTrue("Final photo can scroll clear of navigation",lastBounds.bottom<=navigationBounds.top+1f)
            compose.onNodeWithTag("photo:${ids.last()}").performClick()
            compose.onNodeWithTag("viewer-screen").assertIsDisplayed()
            compose.onNodeWithTag("viewer-close").performClick()
            compose.onNodeWithTag("photos:grid").performScrollToIndex(0)
            compose.waitForIdle()
            saveScreenshot("shell-preview.png")
            runBlocking { ids.forEach { assertEquals(file.absolutePath,app.database.photos().find(it)?.originalPath) } }
            assertTrue(file.isFile)
        } finally {
            runBlocking { ids.forEach { key -> app.database.photos().find(key)?.let { app.database.photos().update(it.copy(archived=true)) } } }
            file.delete()
        }
    }

    private fun saveScreenshot(name: String) {
        if(android.os.Build.VERSION.SDK_INT<29) return
        val bitmap=requireNotNull(InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot())
        try {
            val values=android.content.ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME,name)
                put(MediaStore.MediaColumns.MIME_TYPE,"image/png")
                put(MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
            }
            val uri=requireNotNull(app.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
            app.contentResolver.openOutputStream(uri)?.use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) }
                ?: error("Cannot save shell screenshot")
        } finally { bitmap.recycle() }
    }
}

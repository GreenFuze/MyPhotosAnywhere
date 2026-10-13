package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import android.graphics.Color
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.lifecycle.ViewModelProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.search.*
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.time.Instant
import java.time.ZoneId

@RunWith(AndroidJUnit4::class)
class SearchDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    private fun waitTag(tag: String)=compose.waitUntil(30000) { compose.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty() }
    private fun waitText(text: String)=compose.waitUntil(30000) { compose.onAllNodesWithText(text).fetchSemanticsNodes().isNotEmpty() }
    private fun model()=compose.runOnIdle { ViewModelProvider(compose.activity)[SearchViewModel::class.java] }
    private fun search(text: String) {
        compose.onNodeWithTag("nav:Search").performClick()
        compose.onNodeWithTag("search:query").performTextReplacement(text);compose.onNodeWithTag("search:query").performImeAction()
        compose.waitUntil(10000) {
            androidx.core.view.ViewCompat.getRootWindowInsets(compose.activity.window.decorView)
                ?.isVisible(androidx.core.view.WindowInsetsCompat.Type.ime())!=true
        }
    }
    private inner class Fixture(val suffix: String): AutoCloseable {
        val source=DriveSource("search-next-$suffix","search-next-$suffix","search@test","APP_FILES","search-next-$suffix","Search fixture $suffix")
        val file=File.createTempFile("search-next-$suffix",".png",app.filesDir).also { file ->
            val bitmap=Bitmap.createBitmap(240,320,Bitmap.Config.ARGB_8888)
            try { bitmap.eraseColor(Color.rgb(50,130,200));file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) } } finally { bitmap.recycle() }
        }
        val hash=DriveBackupWorker.digest(file,"SHA-256")
        val time=Instant.parse("2026-09-21T12:00:00Z").toEpochMilli()
        val photos=(0..2).map { index -> Photo("search-next-$suffix-$index","search-next-$suffix-$index.png",file.path,file.path,"image/png",file.length(),
            time-index*86400000L,hash,capturedAt=time-index*86400000L,sourceBindingKey=source.key,labels="specialfield-$suffix",placeLabel="Preserved place") }
        val cloud=CloudPhoto("${source.key}:cloud",source.key,"cloud",source.folderId,"search-next-$suffix-cloud.png","image/png",file.length(),
            DriveBackupWorker.digest(file,"MD5"),"1",null,time+86400000L,null,null,thumbnailPath=file.path,thumbnailVersion="1",sha256=hash)
        val album=Album("search-next-album-$suffix","Search album $suffix",time)
        init { runBlocking { app.database.drive().saveSource(source);photos.forEach { app.database.photos().insert(it) };app.database.library().saveAlbum(album) } }
        fun verify()=runBlocking { photos.forEach { photo ->
            val saved=requireNotNull(app.database.photos().find(photo.id));assertEquals(photo.sha256,saved.sha256);assertEquals(photo.capturedAt,saved.capturedAt)
            assertEquals(photo.placeLabel,saved.placeLabel);assertEquals(photo.sourceBindingKey,saved.sourceBindingKey)
        };assertEquals(hash,DriveBackupWorker.digest(file,"SHA-256")) }
        override fun close() { runBlocking {
            photos.forEach { app.database.photos().find(it.id)?.let { app.database.photos().update(it.copy(archived=true)) } }
            app.database.cloud().markRemoved(source.key,"cloud");app.database.library().clearAlbum(album.id);app.database.library().deleteAlbum(album.id)
            app.database.drive().disconnectSource(source.key)
        };file.delete() }
    }

    @Test fun mixedSearchViewerKeepsItsOrderedSequenceAndPixelAnchorAcrossImportAndRotation() {
        Fixture("viewer").use { fixture ->
            runBlocking { app.database.cloud().savePhoto(fixture.cloud) }
            search("search-next-viewer");waitTag("photo:${fixture.photos[1].id}")
            val before=compose.onNodeWithTag("photo:${fixture.photos[1].id}").fetchSemanticsNode().boundsInWindow.top
            val beforeGrid=compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInWindow.top
            screenshot("search-results")
            compose.onNodeWithTag("photo:${fixture.photos[1].id}").performClick();waitTag("viewer-screen");waitText("3 / 4")
            val added=fixture.photos[0].copy(id="search-next-viewer-import",displayName="search-next-viewer-import.png",capturedAt=fixture.time+2*86400000L)
            try {
                runBlocking { app.database.photos().insert(added) }
                compose.onNodeWithText("Next").performClick();waitText(fixture.photos[2].displayName);compose.onNodeWithText("4 / 4").assertExists()
                compose.activityRule.scenario.recreate();waitTag("viewer-screen");waitText(fixture.photos[2].displayName)
                compose.onNodeWithText("Previous").performClick();waitText(fixture.photos[1].displayName)
                compose.onNodeWithTag("viewer-close").performClick();waitTag("photo:${fixture.photos[1].id}")
                val after=compose.onNodeWithTag("photo:${fixture.photos[1].id}").fetchSemanticsNode().boundsInWindow.top
                val afterGrid=compose.onNodeWithTag("photos:grid").fetchSemanticsNode().boundsInWindow.top
                assertEquals(before-beforeGrid,after-afterGrid,3f)
                compose.onNodeWithTag("search:query").assertTextContains("search-next-viewer");compose.onNodeWithTag("nav:Search").assertIsSelected()
                fixture.verify()
            } finally { runBlocking { app.database.photos().find(added.id)?.let { app.database.photos().update(it.copy(archived=true)) } } }
        }
    }
    @Test fun sourceTypeAndOrganizationFiltersAreIndependentOfPhotos() {
        Fixture("filters").use { fixture ->
            runBlocking { app.database.photos().update(fixture.photos[1].copy(mimeType="video/mp4",favorite=true)) }
            search("search-next-filters");waitTag("photo:${fixture.photos[0].id}")
            compose.onNodeWithTag("shell:filters").performClick()
            compose.onNodeWithTag("search:type:VIDEOS").performClick();compose.onNodeWithTag("search:organization:Favorites").performClick()
            compose.onNodeWithTag("search:source:${fixture.source.key}").performScrollTo().performClick();screenshot("search-filters")
            compose.onNodeWithTag("search:apply").performScrollTo().performClick();waitTag("photo:${fixture.photos[1].id}")
            compose.waitUntil(30000) {
                compose.onAllNodesWithTag("photo:${fixture.photos[0].id}").fetchSemanticsNodes().isEmpty() &&
                    compose.onAllNodesWithTag("photo:${fixture.photos[1].id}").fetchSemanticsNodes().size==1 &&
                    compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty()
            }
            compose.onNodeWithTag("photo:${fixture.photos[0].id}").assertDoesNotExist()
            compose.onNodeWithTag("nav:Photos").performClick();compose.onNodeWithTag("shell:filters").performClick()
            compose.onNodeWithText("All sources").performScrollTo().assertIsSelected();compose.onNodeWithText("Done").performScrollTo().performClick()
            compose.onNodeWithTag("nav:Search").performClick();waitTag("photo:${fixture.photos[1].id}")
            compose.onNodeWithTag("search:filter-summary").assertTextEquals("Videos · Favorites · Search fixture filters");fixture.verify()
        }
    }
    @Test fun recentQueriesCanBeReusedAndClearedWithoutDurableHistory() {
        Fixture("recent").use {
            search("search-next-recent");waitTag("photos:grid")
            compose.onNodeWithTag("search:clear").performClick();waitTag("search:recent:search-next-recent")
            compose.onNodeWithTag("search:query").performImeAction();screenshot("search-discovery")
            compose.onNodeWithTag("search:recent:search-next-recent").performClick();waitTag("photos:grid")
            compose.activityRule.scenario.recreate();waitTag("search:query");compose.onNodeWithTag("search:query").assertTextContains("search-next-recent")
            compose.onNodeWithTag("search:clear").performClick();waitTag("search:clear-recents")
            compose.onNodeWithTag("search:clear-recents").performClick();compose.onNodeWithTag("search:recent:search-next-recent").assertDoesNotExist()
            assertFalse(File(app.applicationInfo.dataDir,"shared_prefs/search.xml").exists())
        }
    }
    @Test fun searchBackReturnsToItsOriginatingAlbum() {
        Fixture("origin").use { fixture ->
            runBlocking { fixture.photos.take(2).forEach { app.database.library().addToAlbum(AlbumPhoto(fixture.album.id,it.id)) } }
            compose.onNodeWithTag("nav:Collections").performClick()
            compose.onNodeWithTag("collections:grid").performScrollToNode(hasTestTag("album:${fixture.album.id}"))
            compose.onNodeWithTag("album:${fixture.album.id}").performClick();waitTag("photo:${fixture.photos[0].id}")
            search("search-next-origin");waitTag("photo:${fixture.photos[0].id}")
            val other=fixture.album.copy(id="search-other-origin",name="Another search album")
            try {
                runBlocking { app.database.library().saveAlbum(other);app.database.library().addToAlbum(AlbumPhoto(other.id,fixture.photos[2].id)) }
                compose.onNodeWithTag("search:clear").performClick();waitTag("search:suggestions")
                compose.onNodeWithTag("search:suggestions").performScrollToNode(hasTestTag("search:category:Albums"))
                compose.onNodeWithTag("search:category:Albums").performClick()
                compose.onNodeWithTag("collections:grid").performScrollToNode(hasTestTag("album:${other.id}"))
                compose.onNodeWithTag("album:${other.id}").performClick();waitTag("photo:${fixture.photos[2].id}")
                compose.runOnUiThread { compose.activity.onBackPressedDispatcher.onBackPressed() }
                waitTag("collections:grid")
                compose.runOnUiThread { compose.activity.onBackPressedDispatcher.onBackPressed() }
                waitTag("search:query")
                compose.onNodeWithTag("search:query").performImeAction()
                compose.waitUntil(10000) {
                    androidx.core.view.ViewCompat.getRootWindowInsets(compose.activity.window.decorView)
                        ?.isVisible(androidx.core.view.WindowInsetsCompat.Type.ime())!=true
                }
            } finally { runBlocking { app.database.library().clearAlbum(other.id);app.database.library().deleteAlbum(other.id) } }
            compose.onNodeWithTag("search:back").performClick();waitTag("photo:${fixture.photos[0].id}")
            compose.onNodeWithText(fixture.album.name).assertExists();compose.onNodeWithTag("photo:${fixture.photos[2].id}").assertDoesNotExist()
            fixture.verify()
        }
    }
    @Test fun searchSelectionUsesSharedAccessibleActionsAndKeepsQuery() {
        Fixture("selection").use { fixture ->
            search("search-next-selection");waitTag("photo:${fixture.photos[0].id}")
            compose.onNodeWithTag("photo:${fixture.photos[0].id}").performTouchInput { longClick() }
            compose.onNodeWithTag("photo:${fixture.photos[1].id}").performClick();compose.onNodeWithText("2 selected").assertIsDisplayed()
            compose.onNodeWithTag("selection:FAVORITE").performClick();waitTag("selection:result-close");compose.onNodeWithTag("selection:result-close").performClick()
            waitTag("search:query");compose.onNodeWithTag("search:query").assertTextContains("search-next-selection")
            assertTrue(runBlocking { requireNotNull(app.database.photos().find(fixture.photos[0].id)).favorite });fixture.verify()
        }
    }
    @Test fun dateFilteredResultsUseTheIndexedLocalCalendarDay() {
        Fixture("dates").use { fixture ->
            search("search-next-dates")
            val date=Instant.ofEpochMilli(fixture.photos[1].capturedAt!!).atZone(ZoneId.systemDefault()).toLocalDate()
            val search=model();compose.runOnIdle { search.update(search.draft.value.copy(dates=SearchDates(date,date)));search.submit() }
            waitTag("search:filter-summary");waitTag("photo:${fixture.photos[1].id}")
            compose.waitUntil(30000) {
                compose.onAllNodesWithTag("photo:${fixture.photos[0].id}").fetchSemanticsNodes().isEmpty() &&
                    compose.onAllNodesWithTag("photo:${fixture.photos[1].id}").fetchSemanticsNodes().size==1 &&
                    compose.onAllNodesWithTag("timeline:refreshing").fetchSemanticsNodes().isEmpty()
            }
            compose.onNodeWithTag("photo:${fixture.photos[0].id}").assertDoesNotExist();compose.onNodeWithTag("photo:${fixture.photos[2].id}").assertDoesNotExist()
            compose.onNodeWithTag("shell:filters").performClick();compose.onNodeWithTag("search:dates").performScrollTo().performClick()
            compose.onNodeWithText("Use dates").assertExists();androidx.test.espresso.Espresso.pressBack()
            compose.onNodeWithTag("search:apply").performScrollTo().performClick();fixture.verify()
        }
    }
    @Test fun indexedMetadataWorksOfflineAndSearchNeverEagerlyDownloadsOriginals() {
        Fixture("offline").use { fixture ->
            runBlocking { app.database.photos().update(fixture.photos[0].copy(originalPath=""));app.database.cloud().savePhoto(fixture.cloud) }
            search("specialfield-offline");waitTag("photo:${fixture.photos[0].id}")
            assertNull(runBlocking { app.database.cloud().download(fixture.cloud.key) })
            compose.onNodeWithTag("search:query").performTextReplacement("search-next-offline-cloud");compose.onNodeWithTag("search:query").performImeAction();waitTag("cloud:${fixture.cloud.key}")
            compose.onNodeWithTag("cloud:${fixture.cloud.key}").performClick();waitTag("viewer-screen")
            compose.onNodeWithTag("viewer-details").assertIsDisplayed();compose.onNodeWithTag("viewer-close").performClick()
            waitTag("cloud:${fixture.cloud.key}");fixture.verify()
        }
    }
    @Test fun supersededQueryDoesNotReplaceCurrentResultsWithAnOldEmptyState() {
        Fixture("debounce").use { fixture ->
            search("missing-search-next-debounce")
            compose.onNodeWithTag("search:query").performTextReplacement("search-next-debounce");compose.onNodeWithTag("search:query").performImeAction()
            waitTag("photo:${fixture.photos[0].id}");compose.onNodeWithText("No matching photos").assertDoesNotExist();fixture.verify()
        }
    }
    @Test fun cancellingFilterSheetDoesNotApplyItsDraft() {
        compose.onNodeWithTag("nav:Search").performClick();compose.onNodeWithTag("search:query").performImeAction()
        compose.onNodeWithTag("shell:filters").performClick();compose.onNodeWithTag("search:type:VIDEOS").performClick()
        compose.onNodeWithText("Cancel").performScrollTo().performClick();waitTag("search:suggestions")
        val search=model();assertFalse(search.draft.value.filtered)
        compose.runOnIdle { search.update(SearchCriteria("missing-empty-action",TimelineMediaType.VIDEOS,"Favorites"));search.submit() }
        waitTag("timeline:empty-action");compose.onNodeWithTag("timeline:empty-action").assertTextContains("Clear filters").performClick()
        assertEquals("missing-empty-action",search.draft.value.text);assertFalse(search.draft.value.filtered)
        compose.waitUntil(10000) { compose.onAllNodes(hasTestTag("timeline:empty-action") and hasText("Clear search")).fetchSemanticsNodes().isNotEmpty() }
        compose.onNodeWithTag("timeline:empty-action").performClick();waitTag("search:suggestions");assertEquals("",search.draft.value.text)
    }
    private fun screenshot(name: String) {
        val image=requireNotNull(InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot())
        try {
            val values=android.content.ContentValues().apply {
                put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"$name.png");put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png")
                put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
            }
            val uri=requireNotNull(app.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
            requireNotNull(app.contentResolver.openOutputStream(uri)).use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) }
        } finally { image.recycle() }
    }
}

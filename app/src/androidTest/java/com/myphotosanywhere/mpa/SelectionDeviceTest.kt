package com.myphotosanywhere.mpa

import android.content.Intent
import android.graphics.Bitmap
import android.graphics.Color
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.core.content.IntentCompat
import androidx.test.espresso.Espresso
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.selection.*
import com.myphotosanywhere.mpa.timeline.*
import com.myphotosanywhere.mpa.library.CollectionsRepository
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class SelectionDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication
    private fun waitTag(tag: String)=compose.waitUntil(30000) { compose.onAllNodes(if(tag.startsWith("photo:") || tag.startsWith("cloud:")) hasTestTag(tag) and isEnabled() else hasTestTag(tag)).fetchSemanticsNodes().isNotEmpty() }
    private fun waitGone(tag: String)=compose.waitUntil(10000) { compose.onAllNodesWithTag(tag).fetchSemanticsNodes().isEmpty() }
    private fun longPress(tag: String) { compose.onNodeWithTag(tag).performTouchInput { longClick() } }
    private fun finish() { waitTag("selection:result-close");compose.onNodeWithTag("selection:result-close").performClick() }
    private inner class Fixture(val suffix: String): AutoCloseable {
        val source=DriveSource("selection-$suffix","selection-$suffix","selection@test","APP_FILES","selection-$suffix","Selection fixture")
        val album=Album("selection-album-$suffix","Selection album $suffix",2_000_000_000_000L)
        val files=(0..2).map { index -> File.createTempFile("selection-$suffix-$index",".png",app.filesDir).also { file ->
            val image=Bitmap.createBitmap(240,320,Bitmap.Config.ARGB_8888)
            try { image.eraseColor(Color.rgb(20+index*50,130,80));file.outputStream().use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) } }
            finally { image.recycle() }
        } }
        val photos=files.mapIndexed { index,file -> Photo("selection-$suffix-$index","selection-$suffix-$index.png",file.path,file.path,"image/png",
            file.length(),2_100_000_000_000L-index*86400000L,DriveBackupWorker.digest(file,"SHA-256"),capturedAt=2_100_000_000_000L-index*86400000L,
            sourceBindingKey=source.key,placeLabel="Preserved place",labels="preserved label",importedMetadata="preserved metadata") }
        val remote=CloudPhoto("${source.key}:remote",source.key,"remote",source.folderId,"remote-selection.png","image/png",files[0].length(),
            DriveBackupWorker.digest(files[0],"MD5"),"1",null,2_100_100_000_000L,null,null,thumbnailPath=files[0].path,thumbnailVersion="1",sha256=photos[0].sha256)
        init { runBlocking { app.database.drive().saveSource(source);app.database.library().saveAlbum(album);photos.forEach { app.database.photos().insert(it) } } }
        fun filter() {
            compose.onNodeWithTag("shell:filters").performClick()
            compose.onNodeWithText("Selection fixture \u00b7 selection@test").performScrollTo().performClick()
            compose.onNodeWithText("Done").performScrollTo().performClick();waitTag("photo:${photos[0].id}")
        }
        fun verifyPreserved()=runBlocking { photos.forEachIndexed { index,photo ->
            val current=requireNotNull(app.database.photos().find(photo.id))
            assertEquals(photo.capturedAt,current.capturedAt);assertEquals(photo.labels,current.labels);assertEquals(photo.importedMetadata,current.importedMetadata)
            assertEquals(photo.sourceBindingKey,current.sourceBindingKey);assertEquals(photo.sha256,DriveBackupWorker.digest(files[index],"SHA-256"))
        } }
        override fun close() {
            runBlocking { photos.forEach { app.database.photos().find(it.id)?.let { photo -> app.database.photos().update(photo.copy(archived=true)) } }
                app.database.cloud().markRemoved(source.key,"remote");app.database.library().clearAlbum(album.id);app.database.library().deleteAlbum(album.id)
                app.database.drive().disconnectSource(source.key) }
            files.forEach { it.delete() }
        }
    }

    @Test fun selectionSurvivesRecreationAndImportWithoutChangingTargets() {
        Fixture("rotation").use { fixture ->
            fixture.filter();longPress("photo:${fixture.photos[0].id}")
            compose.onNodeWithTag("photo:${fixture.photos[1].id}").performClick()
            compose.onNodeWithText("2 selected").assertIsDisplayed();compose.onNodeWithTag("nav:Photos").assertDoesNotExist()
            runBlocking { app.database.photos().insert(fixture.photos[2].copy(id="selection-inserted",capturedAt=2_200_000_000_000L)) }
            compose.activityRule.scenario.recreate();waitTag("selection:bar")
            compose.onNodeWithText("2 selected").assertIsDisplayed()
            compose.onNodeWithTag("selection:FAVORITE").performClick();finish();waitGone("selection:bar")
            runBlocking { assertTrue(requireNotNull(app.database.photos().find(fixture.photos[0].id)).favorite)
                assertTrue(requireNotNull(app.database.photos().find(fixture.photos[1].id)).favorite)
                assertFalse(requireNotNull(app.database.photos().find(fixture.photos[2].id)).favorite)
                val inserted=requireNotNull(app.database.photos().find("selection-inserted"));assertFalse(inserted.favorite);app.database.photos().update(inserted.copy(archived=true)) }
            fixture.verifyPreserved()
        }
    }

    @Test fun albumAddAndRemovePreserveOriginalsAndCollectionsReflectMembership() {
        Fixture("album").use { fixture ->
            fixture.filter();longPress("photo:${fixture.photos[0].id}");compose.onNodeWithTag("photo:${fixture.photos[1].id}").performClick()
            compose.onNodeWithTag("selection:ALBUM").performClick();waitTag("selection:album:${fixture.album.id}")
            compose.onNodeWithTag("selection:album:${fixture.album.id}").performClick();finish()
            assertEquals(2,runBlocking { app.database.library().members(fixture.album.id).size })
            compose.onNodeWithTag("nav:Collections").performClick()
            compose.onNodeWithTag("collections:grid").performScrollToNode(hasTestTag("album:${fixture.album.id}"))
            compose.onNodeWithTag("album:${fixture.album.id}").assertIsDisplayed()
            saveScreenshot("collections-hub")
            compose.onNodeWithTag("album:${fixture.album.id}").performClick();waitTag("photo:${fixture.photos[0].id}")
            longPress("photo:${fixture.photos[0].id}");compose.onNodeWithText("Remove from album").performClick();finish()
            assertEquals(listOf(fixture.photos[1].id),runBlocking { app.database.library().members(fixture.album.id).map { it.photoId } })
            fixture.verifyPreserved()
        }
    }

    @Test fun mixedRemoteSelectionReportsPartialFailureAndKeepsTheFailedIdentity() {
        Fixture("partial").use { fixture ->
            runBlocking { app.database.cloud().savePhoto(fixture.remote) }
            fixture.filter();waitTag("cloud:${fixture.remote.key}");longPress("cloud:${fixture.remote.key}")
            compose.onNodeWithTag("photo:${fixture.photos[0].id}").performClick()
            saveScreenshot("multi-selection")
            compose.onNodeWithTag("selection:FAVORITE").performClick();waitTag("selection:result")
            compose.onNodeWithText("1 completed. 1 not completed. Originals are preserved.").assertExists()
            compose.onNodeWithText("Download the original first to organize this photo.").assertExists();finish()
            compose.onNodeWithText("1 selected").assertIsDisplayed();compose.onNodeWithTag("cloud:${fixture.remote.key}").assertIsSelected()
            assertNull(runBlocking { app.database.cloud().photo(fixture.remote.key)?.photoId })
            assertNull(runBlocking { app.database.cloud().download(fixture.remote.key) })
            // Semantics disappearance can precede the native dialog/window focus handoff.
            compose.waitUntil(10000) { compose.runOnUiThread { compose.activity.window.decorView.hasWindowFocus() } }
            InstrumentationRegistry.getInstrumentation().sendKeyDownUpSync(android.view.KeyEvent.KEYCODE_BACK);waitGone("selection:bar");fixture.verifyPreserved()
        }
    }

    @Test fun cloudDownloadTransitionKeepsTheSameSelectedItem() {
        Fixture("transition").use { fixture ->
            runBlocking { app.database.cloud().savePhoto(fixture.remote) }
            fixture.filter();waitTag("cloud:${fixture.remote.key}");longPress("cloud:${fixture.remote.key}")
            runBlocking { app.database.cloud().savePhoto(fixture.remote.copy(photoId=fixture.photos[0].id)) }
            waitGone("cloud:${fixture.remote.key}");waitTag("photo:${fixture.photos[0].id}")
            compose.onNodeWithTag("photo:${fixture.photos[0].id}").assertIsSelected()
            compose.onNodeWithTag("selection:FAVORITE").performClick();finish()
            assertTrue(runBlocking { requireNotNull(app.database.photos().find(fixture.photos[0].id)).favorite })
            fixture.verifyPreserved()
        }
    }

    @Test fun backExitsSearchSelectionWithoutLosingQueryOrOpeningViewer() {
        Fixture("search").use { fixture ->
            compose.onNodeWithTag("nav:Search").performClick();compose.onNodeWithTag("search:query").performTextInput("selection-search")
            compose.onNodeWithTag("search:query").performImeAction();waitTag("photo:${fixture.photos[0].id}")
            val description=compose.onNodeWithTag("photo:${fixture.photos[0].id}").fetchSemanticsNode().config[androidx.compose.ui.semantics.SemanticsProperties.ContentDescription]
            assertEquals(1,description.size);assertTrue(description.single().contains(fixture.photos[0].displayName))
            longPress("photo:${fixture.photos[0].id}");compose.onNodeWithTag("photo:${fixture.photos[1].id}").performClick()
            compose.onNodeWithText("2 selected").assertIsDisplayed();compose.onNodeWithTag("viewer-screen").assertDoesNotExist()
            compose.onNodeWithTag("selection:close").performClick();waitGone("selection:bar")
            compose.onNodeWithTag("nav:Search").assertIsSelected();compose.onNodeWithTag("search:query").assertTextContains("selection-search")
        }
    }

    @Test fun archiveAndRestoreChangeOnlyVisibility() {
        Fixture("archive").use { fixture ->
            fixture.filter();longPress("photo:${fixture.photos[0].id}")
            compose.onNodeWithTag("selection:ARCHIVE").performClick();finish();waitGone("photo:${fixture.photos[0].id}")
            compose.onNodeWithTag("nav:Collections").performClick();compose.onNodeWithTag("collections:Archive").performClick()
            waitTag("photo:${fixture.photos[0].id}");longPress("photo:${fixture.photos[0].id}")
            compose.onNodeWithTag("selection:RESTORE").performClick();finish()
            assertFalse(runBlocking { requireNotNull(app.database.photos().find(fixture.photos[0].id)).archived });fixture.verifyPreserved()
        }
    }

    @Test fun offlineActionPinsLocalOriginalAndQueuesCapturedRemoteSource()=runBlocking {
        Fixture("offline").use { fixture ->
            app.database.cloud().savePhoto(fixture.remote)
            val repository=SelectionRepository(app)
            repository.apply(SelectionTarget.from(fixture.photos[0]),BulkAction(BulkKind.OFFLINE))
            repository.apply(SelectionTarget.from(fixture.remote),BulkAction(BulkKind.OFFLINE))
            assertTrue(requireNotNull(app.database.photos().find(fixture.photos[0].id)).pinned)
            val task=requireNotNull(app.database.cloud().download(fixture.remote.key))
            assertTrue(task.pinned);assertEquals(fixture.source.key,task.bindingKey);assertEquals("remote",task.remoteId)
            fixture.verifyPreserved();app.downloads.stop(fixture.source.key)
        }
    }

    @Test fun sharePreparesVerifiedOriginalUrisAndMissingFileNeverSharesSubset()=runBlocking {
        Fixture("share").use { fixture ->
            val repository=SelectionRepository(app)
            var processed=0
            val chooser=repository.share(fixture.photos.take(2).map { SelectionTarget.from(it) }) { processed=it }
            assertEquals(2,processed)
            val send=requireNotNull(IntentCompat.getParcelableExtra(chooser,Intent.EXTRA_INTENT,Intent::class.java))
            assertEquals(Intent.ACTION_SEND_MULTIPLE,send.action);assertEquals("image/png",send.type)
            assertTrue(send.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION!=0)
            assertEquals(2,requireNotNull(send.clipData).itemCount)
            (0..1).forEach { index ->
                val uri=requireNotNull(send.clipData).getItemAt(index).uri;assertEquals("content",uri.scheme)
                assertEquals(fixture.files[index].readBytes().toList(),requireNotNull(app.contentResolver.openInputStream(uri)).use { it.readBytes().toList() })
            }
            val before=File(app.cacheDir,"handoff").listFiles().orEmpty().map { it.name }.toSet()
            val photo=fixture.photos[1];app.database.photos().update(photo.copy(originalPath="/missing/original"))
            try { repository.share(fixture.photos.take(2).map { SelectionTarget.from(it) }) {};fail("Missing original was shared") }
            catch(expected: SelectionRepository.Unavailable) { assertTrue(requireNotNull(expected.message).contains("download")) }
            assertEquals(before,File(app.cacheDir,"handoff").listFiles().orEmpty().map { it.name }.toSet())
            app.database.photos().update(photo)
            fixture.verifyPreserved()
        }
    }

    @Test fun changedCloudContentAndRemovedAlbumFailWithoutMutation()=runBlocking {
        Fixture("changed").use { fixture ->
            app.database.cloud().savePhoto(fixture.remote.copy(photoId=fixture.photos[0].id,md5="changed",sha256="changed",version="2"))
            val repository=SelectionRepository(app)
            try { repository.apply(SelectionTarget.from(fixture.remote),BulkAction(BulkKind.FAVORITE));fail("Changed item was updated") }
            catch(expected: SelectionRepository.Unavailable) { assertTrue(requireNotNull(expected.message).contains("changed")) }
            app.database.library().deleteAlbum(fixture.album.id)
            try { repository.apply(SelectionTarget.from(fixture.photos[0]),BulkAction(BulkKind.ALBUM,fixture.album.id));fail("Missing album was used") }
            catch(expected: SelectionRepository.Unavailable) { assertTrue(requireNotNull(expected.message).contains("album")) }
            assertFalse(requireNotNull(app.database.photos().find(fixture.photos[0].id)).favorite)
            assertTrue(app.database.library().members(fixture.album.id).isEmpty());fixture.verifyPreserved()
        }
    }

    @Test fun selectionSavedIdentityAndBoundedCollectionCoversPreserveRelationships()=runBlocking {
        Fixture("covers").use { fixture ->
            val target=SelectionTarget.from(fixture.remote)
            assertEquals(target,SelectionTarget.restore(target.save()))
            fixture.photos.forEach { app.database.library().addToAlbum(AlbumPhoto(fixture.album.id,it.id)) }
            val preview=CollectionsRepository(app.database,app.timeline).preview(TimelineQuery(album=fixture.album.id)).first()
            assertEquals(3,preview.count);assertEquals(fixture.photos.map { it.id },preview.entries.map { it.photoId })
            assertTrue(preview.entries.size<=4);assertEquals(3,app.database.library().members(fixture.album.id).size)
            fixture.verifyPreserved()
        }
    }


    @Test fun cancellingSharePreparationReleasesLeasesAndRemovesStaging()=runBlocking {
        Fixture("cancel").use { fixture ->
            val before=File(app.cacheDir,"handoff").listFiles().orEmpty().map { it.name }.toSet()
            try {
                SelectionRepository(app).share(fixture.photos.take(2).map { SelectionTarget.from(it) }) { throw CancellationException("Fixture cancellation") }
                fail("Cancelled share was delivered")
            } catch(expected: CancellationException) { assertEquals("Fixture cancellation",expected.message) }
            assertEquals(before,File(app.cacheDir,"handoff").listFiles().orEmpty().map { it.name }.toSet())
            fixture.photos.forEach { photo -> requireNotNull(app.cache.openOriginal(photo.id)).use { assertTrue(it.file.isFile) } }
            fixture.verifyPreserved()
        }
    }

    @Test fun contextualActionsLeaveFinalTileTappableAndExposeAccessibleSelection() {
        Fixture("clearance").use { fixture ->
            val added=(3..26).map { fixture.photos[0].copy(id="selection-clearance-$it",capturedAt=2_100_000_000_000L-it*86400000L) }
            try {
                runBlocking { added.forEach { app.database.photos().insert(it) } }
                fixture.filter()
                val actions=compose.onNodeWithTag("photo:${fixture.photos[0].id}").fetchSemanticsNode().config[androidx.compose.ui.semantics.SemanticsActions.CustomActions]
                compose.runOnIdle { assertTrue(actions.single { it.label=="Select photo" }.action()) }
                compose.onNodeWithTag("photo:${fixture.photos[0].id}").assertIsSelected()
                val statusInset=compose.runOnIdle {
                    requireNotNull(androidx.core.view.ViewCompat.getRootWindowInsets(compose.activity.window.decorView))
                        .getInsets(androidx.core.view.WindowInsetsCompat.Type.statusBars()).top
                }
                val close=compose.onNodeWithTag("selection:close").fetchSemanticsNode().boundsInRoot
                assertTrue("Selection controls clear the status bar",close.top>=statusInset-1)
                compose.onNodeWithTag("photos:grid").performScrollToNode(hasTestTag("photo:${added.last().id}"))
                val tile=compose.onNodeWithTag("photo:${added.last().id}").assertIsDisplayed().fetchSemanticsNode().boundsInRoot
                val action=compose.onNodeWithTag("selection:SHARE").fetchSemanticsNode().boundsInRoot
                assertTrue("Last thumbnail is above contextual actions",tile.bottom<=action.top+1)
                compose.onNodeWithTag("photo:${added.last().id}").performClick().assertIsSelected()
                compose.onNodeWithText("2 selected").assertIsDisplayed();compose.onNodeWithTag("viewer-screen").assertDoesNotExist()
                compose.onNodeWithTag("selection:close").performClick()
            } finally { runBlocking { added.forEach { app.database.photos().find(it.id)?.let { photo -> app.database.photos().update(photo.copy(archived=true)) } } } }
        }
    }


    @Test fun offlineCoverSamplingIsBoundedEvenWhenEveryRecordedOriginalIsMissing()=runBlocking {
        Fixture("offline-covers").use { fixture ->
            val missing=(0..99).map { fixture.photos[0].copy(id="missing-cover-$it",originalPath="/missing/$it",capturedAt=2_200_000_000_000L+it) }
            try {
                missing.forEach { app.database.photos().insert(it) }
                var checks=0
                val timeline=TimelineRepository(app.database) { checks++;false }
                val preview=CollectionsRepository(app.database,timeline).preview(TimelineQuery("Offline",source=fixture.source.key)).first()
                assertEquals(24,checks);assertTrue(preview.entries.isEmpty());assertEquals(-1,preview.count)
            } finally { missing.forEach { app.database.photos().find(it.id)?.let { photo -> app.database.photos().update(photo.copy(archived=true)) } } }
        }
    }

    private fun saveScreenshot(name: String) {
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

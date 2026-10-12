package com.myphotosanywhere.mpa.drive

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.PhotoDatabase
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.sync.Mutex
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.IOException
import java.util.UUID
import java.util.concurrent.TimeUnit

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28], application = Application::class)
class DriveIngestionTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private val name = "ingestion-${UUID.randomUUID()}.db"
    private lateinit var db: PhotoDatabase
    private lateinit var server: MockWebServer
    private lateinit var api: DriveGateway
    private val source = DriveSource.from(DriveConnection(accountId = "account", email = "test@example.test",
        mode = "EXISTING_LIBRARY", folderId = "root", folderName = "Test"))
    private val binding get() = source.key

    @Before fun setup() = runBlocking<Unit> {
        open()
        db.drive().saveSource(source)
        server = MockWebServer().apply { start() }
        api = DriveGateway("test", OkHttpClient.Builder().readTimeout(3, TimeUnit.SECONDS).build(),
            server.url("/drive/v3/"), server.url("/upload/drive/v3/files"))
    }
    private fun open() {
        db = Room.databaseBuilder(context, PhotoDatabase::class.java, name).allowMainThreadQueries().build()
    }
    private fun ingestion(save: (suspend (String, String, DriveFile, String) -> Unit)? = null) =
        DriveIngestion(db, Mutex(), save ?: CloudSync(context, db)::save)
    private fun reply(json: String) { server.enqueue(MockResponse().setBody(json)) }
    private fun media(id: String, parent: String = "root") =
        """{"id":"$id","name":"$id.jpg","mimeType":"image/jpeg","size":"8","md5Checksum":"same","version":"1","parents":["$parent"]}"""
    private suspend fun oldPhoto(id: String = "absent") {
        db.cloud().savePhoto(CloudPhoto("$binding:$id", binding, id, "root", "$id.jpg", "image/jpeg", 8,
            "same", "1", null, null, null, null, seenGeneration = "old"))
    }
    private suspend fun changesReady() {
        db.cloud().saveSync(SyncState(binding, "before", System.currentTimeMillis()))
        db.cloud().saveFolder(CloudFolder(binding, "root", null, "Test", "old"))
    }
    @After fun close() {
        if (::server.isInitialized) server.shutdown()
        if (::db.isInitialized) db.close()
        context.deleteDatabase(name)
    }

    @Test fun restartResumesSavedPageAndReplaysChanges() = runBlocking<Unit> {
        oldPhoto()
        reply("""{"startPageToken":"before"}""")
        reply("""{"nextPageToken":"page-2","files":[${media("a")},{"id":"z-child","name":"Nested","mimeType":"application/vnd.google-apps.folder"}]}""")
        assertFalse(ingestion().run(api, source.connection(), SyncBudget(1)))
        val generation = db.cloud().sync(binding)?.scanGeneration
        assertNotNull(generation)
        assertEquals(1, db.cloud().sync(binding)?.scanPages)
        assertEquals("page-2", db.cloud().nextScanFolder(binding)?.pageToken)
        assertNotNull(db.cloud().photo("$binding:a")) // Browsable before completion.
        assertFalse(requireNotNull(db.cloud().photo("$binding:absent")).removed)

        db.close(); open() // New Room connection and engine, no process-local traversal state.
        reply("""{"files":[${media("b") }]}""")
        assertFalse(ingestion().run(api, source.connection(), SyncBudget(1)))
        assertEquals(generation, db.cloud().sync(binding)?.scanGeneration)
        assertEquals("z-child", db.cloud().nextScanFolder(binding)?.remoteId)
        assertFalse(requireNotNull(db.cloud().photo("$binding:absent")).removed)

        reply("""{"files":[${media("c", "z-child")}]}""")
        assertFalse(ingestion().run(api, source.connection(), SyncBudget(1)))
        assertTrue(requireNotNull(db.cloud().photo("$binding:absent")).removed)
        assertNull(db.cloud().sync(binding)?.scanGeneration)
        assertEquals("before", db.cloud().sync(binding)?.changeToken)
        assertEquals(0L, db.cloud().sync(binding)?.lastSuccess)

        reply("""{"newStartPageToken":"after","changes":[{"fileId":"late","file":${media("late")}}]}""")
        assertTrue(ingestion().run(api, source.connection(), SyncBudget(1)))
        assertEquals(setOf("a", "b", "c", "late"), db.cloud().photos(binding).filterNot { it.removed }.map { it.remoteId }.toSet())
        assertEquals("after", db.cloud().sync(binding)?.changeToken)
        assertTrue(requireNotNull(db.cloud().sync(binding)).lastSuccess > 0)
        val requests = (1..server.requestCount).map { requireNotNull(server.takeRequest().requestUrl) }
        assertEquals(listOf("/drive/v3/changes/startPageToken", "/drive/v3/files", "/drive/v3/files", "/drive/v3/files", "/drive/v3/changes"), requests.map { it.encodedPath })
        assertEquals("page-2", requests[2].queryParameter("pageToken"))
        assertTrue(requests.all { it.queryParameter("alt") == null })
    }

    @Test fun pageFailureRollsBackMediaNewFoldersAndTheCursorTogether() = runBlocking<Unit> {
        oldPhoto()
        reply("""{"startPageToken":"before"}""")
        val page = """{"nextPageToken":"next","files":[{"id":"child","name":"Child","mimeType":"application/vnd.google-apps.folder"},${media("a")},${media("b")}]}"""
        reply(page)
        val save = CloudSync(context, db)
        try {
            ingestion { b, p, f, g -> save.save(b, p, f, g); if (f.id == "b") error("Interrupted commit") }
                .run(api, source.connection(), SyncBudget(1))
            fail("Expected failed page")
        } catch (_: IllegalStateException) { }
        assertNull(db.cloud().photo("$binding:a"))
        assertEquals(1, db.cloud().scanFolderCount(binding))
        assertNull(db.cloud().nextScanFolder(binding)?.pageToken)
        assertEquals(0, db.cloud().sync(binding)?.scanPages)
        assertFalse(requireNotNull(db.cloud().photo("$binding:absent")).removed)
        reply(page)
        assertFalse(ingestion().run(api, source.connection(), SyncBudget(1)))
        assertNotNull(db.cloud().photo("$binding:b"))
        assertEquals(2, db.cloud().scanFolderCount(binding))
    }

    @Test fun twoThousandNestedItemsConvergeAcrossRestartedBoundedPasses() = runBlocking<Unit> {
        oldPhoto()
        reply("""{"startPageToken":"before"}""")
        val folders = (0..3).joinToString(",") {
            """{"id":"z$it","name":"Folder $it","mimeType":"application/vnd.google-apps.folder"}"""
        }
        reply("""{"files":[$folders]}""")
        for (folder in 0..3) for (page in 0..4) {
            val files = (0..99).joinToString(",") { media("f$folder-p$page-$it", "z$folder") }
            val next = if (page < 4) "\"nextPageToken\":\"page-${page + 1}\"," else ""
            reply("""{$next"files":[$files]}""")
        }
        reply("""{"newStartPageToken":"after","changes":[]}""")
        var passes = 0
        var complete: Boolean
        do {
            complete = ingestion().run(api, source.connection(), SyncBudget(3))
            passes++
            if (db.cloud().sync(binding)?.scanGeneration != null)
                assertFalse(requireNotNull(db.cloud().photo("$binding:absent")).removed)
            assertTrue("Worker passes must make progress", passes <= 8)
            if (!complete) { db.close(); open() }
        } while (!complete)
        assertEquals(8, passes)
        assertEquals(2000, db.cloud().photos(binding).count { !it.removed })
        assertTrue(requireNotNull(db.cloud().photo("$binding:absent")).removed)
        assertEquals(21, db.cloud().sync(binding)?.scanPages)
        assertEquals(23, server.requestCount) // No restart from page one or bulk original transfer.
    }

    @Test fun incompleteSearchCannotPublishAPartialPageOrRemoveExistingMedia() = runBlocking<Unit> {
        oldPhoto()
        reply("""{"startPageToken":"before"}""")
        reply("""{"incompleteSearch":true,"files":[${media("partial")}]}""")
        try { ingestion().run(api, source.connection()); fail("Expected incomplete listing") }
        catch (e: IOException) { assertTrue(requireNotNull(e.message).contains("incomplete")) }
        assertNull(db.cloud().photo("$binding:partial"))
        assertFalse(requireNotNull(db.cloud().photo("$binding:absent")).removed)
        assertNotNull(db.cloud().sync(binding)?.scanGeneration)
        assertEquals(0, db.cloud().sync(binding)?.scanPages)
        assertEquals(0L, db.cloud().sync(binding)?.lastScan)
        val listing = requireNotNull((1..server.requestCount).map { server.takeRequest() }.last().requestUrl)
        assertTrue(requireNotNull(listing.queryParameter("fields")).contains("incompleteSearch"))
        assertEquals("true", listing.queryParameter("supportsAllDrives"))
        assertEquals("true", listing.queryParameter("includeItemsFromAllDrives"))
    }

    @Test fun rejectedFileListCursorStartsANewGenerationWithoutTombstones() = runBlocking<Unit> {
        oldPhoto()
        reply("""{"startPageToken":"before"}""")
        reply("""{"nextPageToken":"expired","files":[${media("a")}]}""")
        assertFalse(ingestion().run(api, source.connection(), SyncBudget(1)))
        val oldGeneration = db.cloud().sync(binding)?.scanGeneration
        server.enqueue(MockResponse().setResponseCode(400))
        reply("""{"startPageToken":"fresh"}""")
        assertFalse(ingestion().run(api, source.connection(), SyncBudget(1)))
        assertNotEquals(oldGeneration, db.cloud().sync(binding)?.scanGeneration)
        assertEquals("fresh", db.cloud().sync(binding)?.scanToken)
        assertEquals(0, db.cloud().sync(binding)?.scanPages)
        assertNull(db.cloud().nextScanFolder(binding)?.pageToken)
        assertTrue(db.cloud().photos(binding).none { it.removed })
    }

    @Test fun accessFailurePreservesTheCheckpointAndOtherSources() = runBlocking<Unit> {
        oldPhoto()
        val other = source.copy(key = "other", accountId = "other-account")
        db.drive().saveSource(other)
        db.cloud().saveSync(SyncState(other.key, "other-token", lastSuccess = 123))
        reply("""{"startPageToken":"before"}""")
        server.enqueue(MockResponse().setResponseCode(403))
        try { ingestion().run(api, source.connection()); fail("Expected denied access") }
        catch (e: DriveHttpException) { assertEquals(403, e.status) }
        assertFalse(requireNotNull(db.cloud().photo("$binding:absent")).removed)
        assertEquals(0, db.cloud().sync(binding)?.scanPages)
        assertEquals("other-token", db.cloud().sync(other.key)?.changeToken)
        assertEquals(123L, db.cloud().sync(other.key)?.lastSuccess)
    }

    @Test fun nestedFolderMovesTriggerADurableScanBeforeAnyRemoval() = runBlocking<Unit> {
        changesReady(); oldPhoto()
        db.cloud().saveFolder(CloudFolder(binding, "child", "root", "Child", "old"))
        reply("""{"newStartPageToken":"ignored","changes":[{"fileId":"child","file":{"id":"child","mimeType":"application/vnd.google-apps.folder","parents":["outside"]}}]}""")
        reply("""{"startPageToken":"fresh"}""")
        assertFalse(ingestion().run(api, source.connection(), SyncBudget(1)))
        assertEquals("before", db.cloud().sync(binding)?.changeToken)
        assertEquals("fresh", db.cloud().sync(binding)?.scanToken)
        assertFalse(requireNotNull(db.cloud().photo("$binding:absent")).removed)
        reply("""{"files":[]}""")
        reply("""{"newStartPageToken":"after","changes":[]}""")
        assertTrue(ingestion().run(api, source.connection()))
        assertTrue(requireNotNull(db.cloud().photo("$binding:absent")).removed)
        assertEquals(listOf("root"), db.cloud().folders(binding).map { it.remoteId })
    }

    @Test fun changePagesResumeFromTheAtomicCheckpointAndOnlyTheLastMarksSuccess() = runBlocking<Unit> {
        changesReady()
        reply("""{"nextPageToken":"next","changes":[{"fileId":"a","file":${media("a")}}]}""")
        assertFalse(ingestion().run(api, source.connection(), SyncBudget(1)))
        assertEquals("next", db.cloud().sync(binding)?.changeToken)
        assertEquals(0L, db.cloud().sync(binding)?.lastSuccess)
        reply("""{"newStartPageToken":"after","changes":[{"fileId":"b","file":${media("b")}}]}""")
        assertTrue(ingestion().run(api, source.connection(), SyncBudget(1)))
        assertEquals("after", db.cloud().sync(binding)?.changeToken)
        assertTrue(requireNotNull(db.cloud().sync(binding)).lastSuccess > 0)
        assertEquals(2, db.cloud().photos(binding).size)
    }

    @Test fun failedChangePageCannotAdvanceItsToken() = runBlocking<Unit> {
        changesReady()
        reply("""{"newStartPageToken":"after","changes":[{"fileId":"a","file":${media("a")}}]}""")
        val save = CloudSync(context, db)
        try {
            ingestion { b, p, f, g -> save.save(b, p, f, g); error("Interrupted change commit") }
                .run(api, source.connection())
            fail("Expected interrupted commit")
        } catch (_: IllegalStateException) { }
        assertEquals("before", db.cloud().sync(binding)?.changeToken)
        assertNull(db.cloud().photo("$binding:a"))
        assertEquals(0L, db.cloud().sync(binding)?.lastSuccess)
    }

    @Test fun duplicateFoldersAndCyclesAreTraversedOnlyOnce() = runBlocking<Unit> {
        reply("""{"startPageToken":"before"}""")
        reply("""{"files":[{"id":"child","name":"Child","mimeType":"application/vnd.google-apps.folder"},{"id":"child","name":"Child","mimeType":"application/vnd.google-apps.folder"}]}""")
        reply("""{"files":[{"id":"root","name":"Root","mimeType":"application/vnd.google-apps.folder"}]}""")
        reply("""{"newStartPageToken":"after","changes":[]}""")
        assertTrue(ingestion().run(api, source.connection()))
        assertEquals(2, db.cloud().sync(binding)?.scanPages)
        assertEquals(4, server.requestCount)
    }

    @Test fun nonMediaChangesRemoveOldGalleryRowsAndNeverFollowShortcuts() = runBlocking<Unit> {
        changesReady(); oldPhoto("converted")
        reply("""{"newStartPageToken":"after","changes":[
            {"fileId":"converted","file":{"id":"converted","mimeType":"application/zip","parents":["root"]}},
            {"fileId":"shortcut","file":{"id":"shortcut","mimeType":"application/vnd.google-apps.shortcut","parents":["root"]}},
            {"fileId":"metadata","file":{"id":"metadata","mimeType":"application/json","parents":["root"]}}]}""")
        assertTrue(ingestion().run(api, source.connection()))
        assertTrue(requireNotNull(db.cloud().photo("$binding:converted")).removed)
        assertEquals(1, db.cloud().photos(binding).size)
        assertEquals(1, server.requestCount)
    }

    @Test fun disconnectedSourceFailsBeforeAnyNetworkCall() = runBlocking<Unit> {
        db.drive().disconnectSource(binding)
        try { ingestion().run(api, source.connection()); fail("Expected disconnected source") }
        catch (_: IllegalStateException) { }
        assertEquals(0, server.requestCount)
    }

    @Test fun elapsedTimeStopsBeforeAnotherPageWithoutUsingWallClock() {
        var time = 100L
        val budget = SyncBudget(10, 20) { time }
        assertTrue(budget.takePage())
        time += 20
        assertFalse(budget.takePage())
    }
}

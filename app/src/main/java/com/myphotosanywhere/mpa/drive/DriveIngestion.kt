package com.myphotosanywhere.mpa.drive

import androidx.room.withTransaction
import com.myphotosanywhere.mpa.data.PhotoDatabase
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.IOException
import java.util.UUID

/** Shared page/time allowance for discovery and change replay. Time is checked between requests. */
internal class SyncBudget(
    private val maximumPages: Int = 20,
    private val maximumMillis: Long = 120_000,
    private val clock: () -> Long = { System.nanoTime() / 1_000_000 },
) {
    private val started = clock()
    private var pages = 0
    init { require(maximumPages > 0 && maximumMillis > 0) }
    fun takePage(): Boolean {
        if (pages >= maximumPages || clock() - started >= maximumMillis) return false
        pages++
        return true
    }
}

/** Network reads never hold the cache lock. Rows and durable traversal cursors commit together. */
internal class DriveIngestion(
    private val database: PhotoDatabase,
    private val cacheLock: Mutex,
    private val saveMedia: suspend (String, String, DriveFile, String) -> Unit,
) {
    private val cloud = database.cloud()

    private suspend fun ensureSource(binding: String) {
        currentCoroutineContext().ensureActive()
        check(database.drive().source(binding) != null) { "This Drive source was disconnected. Sync paused." }
    }

    suspend fun run(api: DriveGateway, active: DriveConnection, budget: SyncBudget = SyncBudget()): Boolean {
        val binding = requireNotNull(active.binding()).key
        ensureSource(binding)
        val state = cloud.sync(binding) ?: SyncState(binding)
        if (state.scanGeneration == null &&
            (state.changeToken == null || System.currentTimeMillis() - state.lastScan > DAY_MILLIS)) begin(api, active)
        if (cloud.sync(binding)?.scanGeneration != null && !scan(api, active, budget)) return false
        return reconcile(api, active, requireNotNull(cloud.sync(binding)?.changeToken), budget)
    }

    private suspend fun begin(api: DriveGateway, active: DriveConnection) {
        val binding = requireNotNull(active.binding()).key
        // Capture before any listing, including a scan triggered by a folder-change page.
        val token = api.startToken()
        val generation = UUID.randomUUID().toString()
        database.withTransaction {
            ensureSource(binding)
            cloud.clearScanFolders(binding)
            cloud.enqueueScanFolder(ScanFolder(binding, requireNotNull(active.folderId)))
            cloud.saveFolder(CloudFolder(binding, active.folderId, null, active.folderName ?: "Photo folder", generation))
            cloud.saveSync((cloud.sync(binding) ?: SyncState(binding)).copy(
                scanGeneration = generation, scanToken = token, scanPages = 0, status = "DISCOVERING"))
        }
    }

    private suspend fun abandon(binding: String) = database.withTransaction {
        cloud.clearScanFolders(binding)
        cloud.sync(binding)?.let { cloud.saveSync(it.copy(scanGeneration = null, scanToken = null, changeToken = null)) }
    }

    private suspend fun scan(api: DriveGateway, active: DriveConnection, budget: SyncBudget): Boolean {
        val binding = requireNotNull(active.binding()).key
        while (true) {
            ensureSource(binding)
            val folder = cloud.nextScanFolder(binding) ?: break
            if (!budget.takePage()) return false
            val page = try { api.children(folder.remoteId, folder.pageToken) }
            catch (e: DriveHttpException) {
                // File-list cursors have limited lifetimes. Restart the entire generation so old
                // seen markers cannot make a restarted folder listing authoritative by accident.
                if (folder.pageToken != null && e.status in listOf(400, 410)) {
                    begin(api, active)
                    return false
                }
                // Re-enumerate the tree on retry after a folder moves away or access disappears.
                if (e.status == 404) abandon(binding)
                throw e
            }
            if (page.incompleteSearch) throw IOException("Drive returned incomplete folder results. Retry refresh; existing media is preserved.")
            check(page.nextPage == null || page.nextPage != folder.pageToken) { "Drive repeated a folder page cursor." }
            cacheLock.withLock { database.withTransaction {
                ensureSource(binding)
                val state = requireNotNull(cloud.sync(binding))
                val generation = requireNotNull(state.scanGeneration)
                page.files.filterNot { it.trashed }.forEach { file ->
                    if (file.mimeType == DriveGateway.FOLDER_MIME) {
                        cloud.saveFolder(CloudFolder(binding, file.id, folder.remoteId, file.name, generation))
                        cloud.enqueueScanFolder(ScanFolder(binding, file.id))
                    } else if (isMedia(file)) saveMedia(binding, folder.remoteId, file, generation)
                    else if(file.name.endsWith(".json",true)) discoverJson(binding,folder.remoteId,file)
                }
                require(cloud.scanFolderCount(binding) <= 10_000) { "This library exceeds the 10,000 folder discovery limit. Existing media is preserved." }
                cloud.updateScanFolder(folder.copy(pageToken = page.nextPage, complete = page.nextPage == null))
                cloud.saveSync(state.copy(scanPages = state.scanPages + 1, status = "DISCOVERING"))
            } }
        }
        cacheLock.withLock { database.withTransaction {
            ensureSource(binding)
            val state = requireNotNull(cloud.sync(binding))
            val generation = requireNotNull(state.scanGeneration)
            check(cloud.nextScanFolder(binding) == null)
            cloud.markAbsent(binding, generation)
            cloud.pruneFolders(binding, generation)
            cloud.clearScanFolders(binding)
            cloud.saveSync(state.copy(changeToken = requireNotNull(state.scanToken), lastScan = System.currentTimeMillis(),
                scanGeneration = null, scanToken = null, status = "RECONCILING"))
        } }
        return true
    }

    suspend fun reconcile(api: DriveGateway, active: DriveConnection, start: String,
        budget: SyncBudget = SyncBudget()): Boolean {
        val binding = requireNotNull(active.binding()).key
        var token = start
        while (budget.takePage()) {
            ensureSource(binding)
            val page = try { api.changes(token) } catch (e: DriveHttpException) {
                // Drive v3 change tokens do not normally expire. Retain the conservative existing
                // full-rescan fallback if the server explicitly rejects a cursor with HTTP 410.
                if (e.status == 410) { begin(api, active); return false }
                throw e
            }
            val folders = cloud.folders(binding).map { it.remoteId }.toSet()
            if (page.changes.any { change ->
                (change.file?.mimeType == DriveGateway.FOLDER_MIME &&
                    (change.id in folders || change.file.parents.any { it in folders })) ||
                    (change.removed && change.id in folders)
            }) {
                begin(api, active)
                return false // Never recurse into another unbounded traversal.
            }
            val next = page.nextPage ?: page.newStart ?: error("Drive returned no next change token.")
            check(page.nextPage == null || next != token) { "Drive repeated a change page cursor." }
            cacheLock.withLock { database.withTransaction {
                ensureSource(binding)
                page.changes.forEach { change ->
                    val file = change.file
                    if(file!=null) {
                        val trash=com.myphotosanywhere.mpa.trash.DriveTrashIndex(database)
                        if(!trash.accepts(binding,file)) return@forEach
                        trash.verified(binding,file)
                    }
                    if(file!=null && !file.trashed && file.parents.any { it in folders } && file.name.endsWith(".json",true))
                        discoverJson(binding,file.parents.first { it in folders },file)
                    if (change.removed || file == null || file.trashed ||
                        file.parents.none { it in folders } || !isMedia(file)) cloud.markRemoved(binding, change.id)
                    else saveMedia(binding, file.parents.first { it in folders }, file,
                        cloud.photo("$binding:${file.id}")?.seenGeneration ?: "changes")
                }
                val state = cloud.sync(binding) ?: SyncState(binding)
                cloud.saveSync(state.copy(changeToken = next, status = "RECONCILING",
                    lastSuccess = if (page.nextPage == null) System.currentTimeMillis() else state.lastSuccess))
            } }
            token = page.nextPage ?: return true
        }
        return false
    }

    private fun isMedia(file: DriveFile) = file.mimeType.startsWith("image/") || file.mimeType.startsWith("video/")
    private suspend fun discoverJson(binding: String,parent: String,file: DriveFile) {
        database.imports().insertSidecar(com.myphotosanywhere.mpa.importing.TakeoutSidecar(
            "$binding:${file.id}:${file.version}",binding,file.id,parent,file.name,file.version,file.md5,file.bytes))
    }
    companion object { private const val DAY_MILLIS = 24 * 60 * 60 * 1000L }
}

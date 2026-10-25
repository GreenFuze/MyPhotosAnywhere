package com.myphotosanywhere.mpa.processor.client

import android.database.sqlite.SQLiteDatabase
import androidx.core.content.contentValuesOf
import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.drive.DriveWorkspaceCursor
import com.myphotosanywhere.mpa.drive.DriveWorkspaceMetadata
import com.myphotosanywhere.mpa.drive.DriveWorkspacePage
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import java.io.IOException
import java.security.MessageDigest
import java.util.Collections
import java.util.UUID
import kotlin.coroutines.CoroutineContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

internal class ProcessorWorkspaceConflict : IOException("Previously observed metadata changed or disappeared. Its pending recovery and saved library were preserved.")
internal class ProcessorWorkspaceLimit : IOException("This workspace exceeds the bounded observation inventory. Its pending recovery and saved library were preserved.")

internal data class ProcessorWorkspaceCandidate(val fileId: String,val version: Long,val bytes: Long?,val mimeType: String) {
    init {
        DriveWorkspaceMetadata.opaque(fileId)
        if (mimeType !in MIME || version < 1 || bytes != null && bytes < 0) throw IOException("Invalid observed metadata candidate.")
    }
    val readable: Boolean get() = bytes != null && bytes in 1..(if(mimeType == "application/json") 256L * 1024 else 4L * 1024 * 1024)
    companion object { val MIME = setOf("application/json","application/octet-stream","application/gzip","application/x-gzip") }
}
internal data class ProcessorWorkspaceRead(val candidate: ProcessorWorkspaceCandidate,val sha256: String) {
    init { if (!candidate.readable || !sha256.matches(Regex("[0-9a-f]{64}"))) throw IOException("Invalid observed metadata digest.") }
}

/** Always INCOMPLETE evidence. This private token cannot activate a catalog, certify
 * current authority, skip change replay, or grant original-file writes. A new attempt
 * fences its predecessor before any network call and preserves unresolved history. */
internal data class ProcessorWorkspaceAttempt(val id: UUID,val revision: Long,val connectionSha256: String,
    val inventoryStart: String?,val firstUnresolved: String?,val candidateSha256: String,val readSha256: String,val inventoryListed: Boolean,val eventSequence: Long,val eventSha256: String,
    val inventoryDriveId: String? = null,val unresolvedDriveId: String? = null,
    val historySha256: String = ProcessorWorkspaceObservations.initial(connectionSha256,"history"),
    val replaySha256: String = ProcessorWorkspaceObservations.initial(connectionSha256,"replay")) {
    internal fun head(kind: String): String = when(kind) { "candidate" -> candidateSha256;"read" -> readSha256;"history" -> historySha256;"replay" -> replaySha256;else -> error("Unexpected workspace evidence role.") }
}

/** Serialized pending-evidence journal. Mutations append immutable chained events;
 * startup/recovery and final read enumeration verify the WHOLE chain and every body.
 * Per-object work checks the exact current attempt and touched rows without repeatedly
 * decoding/hashing the entire inventory. No completed/current-authority API exists here. */
internal class ProcessorWorkspaceObservations(private val store: ProcessorLibraryStateStore) {
    private val codec = WorkspaceObservationCodec()
    suspend fun lastAttempt(): ProcessorWorkspaceAttempt? = transaction { database,registration,cancellation -> load(database,registration,true,cancellation).attempt }
    suspend fun begin(expectedConnectionSha256: String): ProcessorWorkspaceAttempt = transaction { database,registration,cancellation ->
        if (registration.descriptor.documentSha256 != expectedConnectionSha256) throw ProcessorWorkspaceConflict()
        if (registration.account == null) throw IOException("Approve the actual Google account before metadata observation.")
        val state = load(database,registration,true,cancellation);val previous = state.attempt;val pin = registration.descriptor.documentSha256
        val attempt = ProcessorWorkspaceAttempt(UUID.randomUUID(),Math.addExact(previous?.revision ?: 0,1),pin,null,previous?.firstUnresolved,
            previous?.candidateSha256 ?: initial(pin,CANDIDATE),previous?.readSha256 ?: initial(pin,READ),false,
            previous?.eventSequence ?: 0,previous?.eventSha256 ?: initial(pin,"events"),
            unresolvedDriveId=previous?.unresolvedDriveId,historySha256=previous?.historySha256 ?: initial(pin,HISTORY),replaySha256=previous?.replaySha256 ?: initial(pin,REPLAY))
        store.requireWriteBudget();putMeta(database,"workspace_attempt_revision",attempt.revision.toString())
        val replay = state.replay()
        val next = if(replay == null) attempt else append(database,attempt,REPLAY,REPLAY_ID,codec.replay(replay.copy(owner=attempt.id,active=false,atEnd=false,batchPages=0,inventoryChanged=false)))
        save(database,next);next
    }
    suspend fun start(attempt: ProcessorWorkspaceAttempt,cursor: DriveWorkspaceCursor): ProcessorWorkspaceAttempt = change(attempt) { database,state ->
        if (attempt.inventoryStart != null || cursor.accountId != state.registration.descriptor.workspace.accountId ||
            attempt.firstUnresolved != null && attempt.unresolvedDriveId != cursor.sharedDriveId) throw ProcessorWorkspaceConflict()
        attempt.copy(inventoryStart=cursor.token,inventoryDriveId=cursor.sharedDriveId,firstUnresolved=attempt.firstUnresolved ?: cursor.token,
            unresolvedDriveId=if(attempt.firstUnresolved == null) cursor.sharedDriveId else attempt.unresolvedDriveId).also { save(database,it) }
    }
    suspend fun rememberCandidates(attempt: ProcessorWorkspaceAttempt,candidates: List<ProcessorWorkspaceCandidate>): ProcessorWorkspaceAttempt {
        if (candidates.size > 100) throw ProcessorWorkspaceLimit()
        val owned = candidates.toList()
        if (owned.size > 100) throw ProcessorWorkspaceLimit()
        if (owned.map { it.fileId }.distinct().size != owned.size) throw ProcessorWorkspaceConflict()
        return change(attempt) { database,state ->
            if (attempt.inventoryStart == null) throw ProcessorWorkspaceConflict()
            candidates(database,state,attempt,owned)
        }
    }
    private fun candidates(database: SQLiteDatabase,state: Snapshot,attempt: ProcessorWorkspaceAttempt,owned: List<ProcessorWorkspaceCandidate>): ProcessorWorkspaceAttempt {
            val changed = owned.filter { value ->
                if (value.fileId == state.registration.descriptor.workspace.folderId) throw ProcessorWorkspaceConflict()
                val old = state.candidate(value.fileId);val read = state.read(value.fileId)?.candidate
                if (old != null && (!sameContent(old,value) || old.version > value.version) || read != null && (!sameContent(read,value) || read.version > value.version))
                    throw ProcessorWorkspaceConflict()
                old != value
            }
            if (state.candidateCount + changed.count { state.candidate(it.fileId) == null } > MAX_DOCUMENTS) throw ProcessorWorkspaceLimit()
            var next = attempt
            val documents = changed.map { it.fileId to codec.candidate(it) }
            if (documents.isNotEmpty()) {
                store.requireWriteBudget(documents.sumOf { it.second.size.toLong() * 3 + 4096 } + 65536)
                for ((id,document) in documents) next = append(database,next,CANDIDATE,id,document)
                next = next.copy(inventoryListed=false);save(database,next)
            }
            return next
    }

    suspend fun sealInventory(attempt: ProcessorWorkspaceAttempt,listed: List<ProcessorWorkspaceCandidate>): ProcessorWorkspaceAttempt {
        if (listed.size > MAX_DOCUMENTS) throw ProcessorWorkspaceLimit()
        val owned = listed.toList()
        if (owned.size > MAX_DOCUMENTS) throw ProcessorWorkspaceLimit()
        if (owned.map { it.fileId }.distinct().size != owned.size) throw ProcessorWorkspaceConflict()
        return change(attempt) { database,state ->
            if (attempt.inventoryStart == null || owned.size != state.candidateCount || owned.any { state.candidate(it.fileId) != it }) throw ProcessorWorkspaceConflict()
            attempt.copy(inventoryListed=true).also { if (it != attempt) save(database,it) }
        }
    }
    suspend fun rememberRead(attempt: ProcessorWorkspaceAttempt,candidate: ProcessorWorkspaceCandidate,bytes: ByteArray): ProcessorWorkspaceAttempt {
        if (!candidate.readable || bytes.size.toLong() != candidate.bytes) throw ProcessorWorkspaceConflict()
        val digest = withContext(Dispatchers.Default) { currentCoroutineContext().ensureActive();hash(bytes.copyOf()) }
        return change(attempt) { database,state ->
            if (!attempt.inventoryListed || state.candidate(candidate.fileId) != candidate) throw ProcessorWorkspaceConflict()
            val observed = ProcessorWorkspaceRead(candidate,digest);val previous = state.read(candidate.fileId)
            if (previous != null && (!sameContent(previous.candidate,candidate) || previous.candidate.version > candidate.version || previous.sha256 != digest)) throw ProcessorWorkspaceConflict()
            if (observed == previous) attempt else {
                val document = codec.read(observed);store.requireWriteBudget(document.size.toLong() * 3 + 8192)
                append(database,attempt,READ,candidate.fileId,document).also { save(database,it) }
            }
        }
    }
    suspend fun reads(attempt: ProcessorWorkspaceAttempt): List<ProcessorWorkspaceRead> = transaction { database,registration,cancellation ->
        val state = load(database,registration,true,cancellation);current(attempt,state)
        if (!attempt.inventoryListed || state.candidateCount != state.readCount) throw ProcessorWorkspaceConflict()
        val result = state.allIds(CANDIDATE).map { id ->
            val value = state.read(id) ?: throw ProcessorWorkspaceConflict()
            if (value.candidate != state.candidate(id)) throw ProcessorWorkspaceConflict()
            value
        }
        Collections.unmodifiableList(result)
    }
    suspend fun workspaceFolder(attempt: ProcessorWorkspaceAttempt): String = transaction { database,registration,cancellation ->
        current(attempt,load(database,registration,false,cancellation));registration.descriptor.workspace.folderId
    }
    suspend fun startReplay(attempt: ProcessorWorkspaceAttempt): ProcessorWorkspaceReplayBatch = transaction { database,registration,cancellation ->
        val snapshot = load(database,registration,true,cancellation);current(attempt,snapshot)
        if(!attempt.inventoryListed || attempt.firstUnresolved == null || snapshot.candidateCount != snapshot.readCount) throw ProcessorWorkspaceConflict()
        for(id in snapshot.allIds(CANDIDATE)) if(snapshot.read(id)?.candidate != snapshot.candidate(id)) throw ProcessorWorkspaceConflict()
        if(snapshot.allIds(HISTORY).any { checkNotNull(snapshot.history(it)).recoveryRequired }) throw ProcessorWorkspaceHistoryRecovery()
        val from = DriveWorkspaceCursor(registration.descriptor.workspace.accountId,attempt.unresolvedDriveId,attempt.firstUnresolved)
        val old = snapshot.replay()
        if(old?.active == true || old?.atEnd == true || old?.inventoryChanged == true) throw ProcessorWorkspaceConflict()
        val state = (old ?: ProcessorWorkspaceReplayState(attempt.id,from,from,0,0,false,false,false)).copy(active=true,batchPages=0)
        val next = append(database,attempt,REPLAY,REPLAY_ID,codec.replay(state));save(database,next)
        ProcessorWorkspaceReplayBatch(next,state)
    }
    suspend fun rememberHistoryPage(batch: ProcessorWorkspaceReplayBatch,page: ProcessorWorkspaceHistoryEvidence,rejectedFence: Boolean): ProcessorWorkspaceReplayBatch = transaction { database,registration,cancellation ->
        val snapshot = load(database,registration,false,cancellation);current(batch.attempt,snapshot)
        if(snapshot.replay() != batch.state || page.from != batch.state.cursor || !page.next.sameScope(batch.state.start) ||
            !rejectedFence && (!batch.state.active || batch.state.atEnd || batch.state.batchPages >= 128) ||
            rejectedFence && (!batch.state.atEnd || batch.state.active)) throw ProcessorWorkspaceConflict()
        if(!page.atEnd && page.next.token == page.from.token) throw ProcessorWorkspaceConflict()
        val updates = linkedMapOf<String,ProcessorWorkspaceHistorySummary>()
        for(value in page.events) {
            cancellation.ensureActive()
            val prior = updates[value.key] ?: snapshot.history(value.key)
            if(prior != null && (prior.kind != value.kind || prior.id != value.id)) throw ProcessorWorkspaceConflict()
            val count = Math.addExact(prior?.occurrences ?: 0,1)
            val chain = hash(("mpa-native-drive-history-v1\u0000" + (prior?.chainSha256 ?: batch.attempt.connectionSha256) + "\u0000" + count + "\u0000" + value.sha256 + "\u0000" + value.recoveryRequired).toByteArray(Charsets.UTF_8))
            updates[value.key] = ProcessorWorkspaceHistorySummary(value.kind,value.id,prior?.recoveryRequired == true || value.recoveryRequired,count,prior?.firstSha256 ?: value.sha256,value.sha256,chain)
        }
        if(snapshot.historyCount + updates.keys.count { snapshot.history(it) == null } > MAX_DOCUMENTS) throw ProcessorWorkspaceLimit()
        store.requireWriteBudget(65536L + updates.size * 8192L + page.candidates.size * 8192L)
        var next = candidates(database,snapshot,batch.attempt,page.candidates)
        for((id,value) in updates) next = append(database,next,HISTORY,id,codec.history(value))
        val recovery = updates.values.any { it.recoveryRequired }
        val state = batch.state.copy(cursor=if(rejectedFence) batch.state.cursor else page.next,totalPages=Math.addExact(batch.state.totalPages,1),
            batchPages=batch.state.batchPages + if(rejectedFence) 0 else 1,active=!rejectedFence && !page.atEnd && !recovery && !page.inventoryChanged,
            atEnd=!rejectedFence && page.atEnd,inventoryChanged=batch.state.inventoryChanged || rejectedFence || page.inventoryChanged)
        next = append(database,next,REPLAY,REPLAY_ID,codec.replay(state));save(database,next)
        ProcessorWorkspaceReplayBatch(next,state)
    }
    suspend fun pauseReplay(batch: ProcessorWorkspaceReplayBatch): ProcessorWorkspaceReplayBatch = replayState(batch) { current ->
        if(!current.active || current.batchPages != 128 || current.atEnd) throw ProcessorWorkspaceConflict()
        current.copy(active=false)
    }
    suspend fun rememberUnchanged(batch: ProcessorWorkspaceReplayBatch,page: ProcessorWorkspaceHistoryEvidence): ProcessorWorkspaceReplayBatch = replayState(batch) { current ->
        if(!current.atEnd || current.active || current.inventoryChanged || page.from != current.cursor || !page.atEnd || !page.next.sameScope(current.cursor) || page.events.isNotEmpty() || page.inventoryChanged) throw ProcessorWorkspaceConflict()
        current.copy(cursor=page.next,totalPages=Math.addExact(current.totalPages,1))
    }
    private suspend fun replayState(batch: ProcessorWorkspaceReplayBatch,action: (ProcessorWorkspaceReplayState)->ProcessorWorkspaceReplayState): ProcessorWorkspaceReplayBatch = transaction { database,registration,cancellation ->
        val snapshot = load(database,registration,true,cancellation);current(batch.attempt,snapshot)
        if(snapshot.replay() != batch.state) throw ProcessorWorkspaceConflict()
        val state = action(batch.state);val next = append(database,batch.attempt,REPLAY,REPLAY_ID,codec.replay(state));save(database,next)
        ProcessorWorkspaceReplayBatch(next,state)
    }
    private suspend fun change(attempt: ProcessorWorkspaceAttempt,action: (SQLiteDatabase,Snapshot)->ProcessorWorkspaceAttempt): ProcessorWorkspaceAttempt =
        transaction { database,registration,cancellation -> val state = load(database,registration,false,cancellation);current(attempt,state);action(database,state) }
    private suspend fun <T> transaction(action: (SQLiteDatabase,ProcessorLibraryRegistration,CoroutineContext)->T): T {
        val cancellation = currentCoroutineContext()
        return store.controllerTransaction { database,registration -> action(database,registration,cancellation) }
    }
    private fun current(attempt: ProcessorWorkspaceAttempt,state: Snapshot) {
        if (attempt != state.attempt || state.registration.account == null) throw ProcessorWorkspaceConflict()
    }
    private fun load(database: SQLiteDatabase,registration: ProcessorLibraryRegistration,full: Boolean,cancellation: CoroutineContext): Snapshot {
        cancellation.ensureActive()
        val document = blob(database,"workspace_attempt","singleton=1",null,65536)
        val marker = database.rawQuery("SELECT length(value),CASE WHEN length(value) BETWEEN 1 AND 19 THEN value END FROM client_meta WHERE key='workspace_attempt_revision'",null).use { row ->
            if (!row.moveToFirst()) null else { state(!row.isNull(1),"Workspace revision exceeds its bound.");row.getString(1) }
        }
        val attempt = document?.let { decode { codec.attempt(it.bytes) } }
        val counts = database.rawQuery("SELECT (SELECT count(*) FROM workspace_candidate),(SELECT count(*) FROM workspace_read),(SELECT count(*) FROM workspace_event),(SELECT coalesce(max(sequence),0) FROM workspace_event),(SELECT count(*) FROM workspace_history),(SELECT count(*) FROM workspace_replay)",null).use { row ->
            state(row.moveToFirst(),"Workspace counters are missing.")
            val values = LongArray(6) { row.getLong(it) }
            state(values[0] in 0..MAX_DOCUMENTS.toLong() && values[1] in 0..MAX_DOCUMENTS.toLong() && values[2] in 0..MAX_EVENTS.toLong() && values[2] == values[3] && values[4] in 0..MAX_DOCUMENTS.toLong() && values[5] in 0..1L,"Workspace history is missing or exceeds its bound.")
            values
        }
        if (attempt == null) state(marker == null && counts.all { it == 0L },"Workspace evidence lost its pending owner.")
        else {
            state(marker == attempt.revision.toString() && attempt.connectionSha256 == registration.descriptor.documentSha256 && attempt.eventSequence == counts[2],"Workspace attempt changed its root, revision or retained history.")
            if (attempt.eventSequence == 0L) state(attempt.eventSha256 == initial(attempt.connectionSha256,"events") && attempt.candidateSha256 == initial(attempt.connectionSha256,CANDIDATE) && attempt.readSha256 == initial(attempt.connectionSha256,READ) && counts[0] == 0L && counts[1] == 0L && counts[4] == 0L && counts[5] == 0L && attempt.historySha256 == initial(attempt.connectionSha256,HISTORY) && attempt.replaySha256 == initial(attempt.connectionSha256,REPLAY),"Empty workspace history lost its anchored state.")
            else {
                val event = event(database,attempt.eventSequence)
                state(event.global == attempt.eventSha256 && event.role == attempt.head(event.value.kind),"Workspace history lost its exact tail.")
            }
        }
        val result = Snapshot(database,registration,attempt,counts[0].toInt(),counts[1].toInt(),counts[4].toInt(),cancellation)
        if (full && attempt != null) result.validateAll()
        return result
    }
    private inner class Snapshot(val database: SQLiteDatabase,val registration: ProcessorLibraryRegistration,val attempt: ProcessorWorkspaceAttempt?,val candidateCount: Int,val readCount: Int,val historyCount: Int,val cancellation: CoroutineContext) {
        private val candidateCache = mutableMapOf<String,ProcessorWorkspaceCandidate?>()
        private val readCache = mutableMapOf<String,ProcessorWorkspaceRead?>()
        fun candidate(id: String): ProcessorWorkspaceCandidate? {
            if(candidateCache.containsKey(id)) return candidateCache[id]
            val value = row(CANDIDATE,id)?.let { decodeCandidate(id,it) };candidateCache[id] = value;return value
        }
        private fun decodeCandidate(id: String,bytes: ByteArray): ProcessorWorkspaceCandidate = decode { codec.candidate(bytes) }.also {
            state(it.fileId == id && id != registration.descriptor.workspace.folderId,"Saved candidate changed identity.");candidateCache[id] = it
        }
        fun read(id: String): ProcessorWorkspaceRead? {
            if(readCache.containsKey(id)) return readCache[id]
            val value = row(READ,id)?.let { decodeRead(id,it) };readCache[id] = value;return value
        }
        private fun decodeRead(id: String,bytes: ByteArray): ProcessorWorkspaceRead = decode { codec.read(bytes) }.also { value ->
            val captured = candidate(id)
            state(value.candidate.fileId == id && captured != null && sameContent(value.candidate,captured) && value.candidate.version <= captured.version,"Saved read lost its candidate.");readCache[id] = value
        }
        fun allIds(kind: String): List<String> = database.rawQuery("SELECT file_id FROM ${table(kind)} ORDER BY file_id LIMIT 4097",null).use { rows ->
            val result = arrayListOf<String>()
            while (rows.moveToNext()) { cancellation.ensureActive();state(result.size < MAX_DOCUMENTS,"Workspace inventory exceeds its bound.");result.add(rows.getString(0)) };result
        }
        fun history(id: String): ProcessorWorkspaceHistorySummary? = row(HISTORY,id)?.let { bytes -> decode { codec.history(bytes) }.also { state(it.key == id,"Saved history changed its identity.") } }
        fun replay(): ProcessorWorkspaceReplayState? = row(REPLAY,REPLAY_ID)?.let { bytes -> decode { codec.replay(bytes) }.also {
            state(it.owner == checkNotNull(attempt).id && it.cursor.accountId == registration.descriptor.workspace.accountId,"Saved replay changed its owner or principal.")
            val current = checkNotNull(attempt)
            state(current.firstUnresolved != null && it.start.token == current.firstUnresolved && it.start.sharedDriveId == current.unresolvedDriveId,"Saved replay lost its earliest retained interval.")
        } }
        private fun row(kind: String,id: String): ByteArray? {
            cancellation.ensureActive()
            val stored = blob(database,table(kind),"file_id=?",arrayOf(id),if(kind == REPLAY) 65536 else 2048)
            val latest = database.rawQuery("SELECT sequence FROM workspace_event WHERE kind=? AND file_id=? ORDER BY sequence DESC LIMIT 1",arrayOf(kind,id)).use { row -> if(row.moveToFirst()) row.getLong(0) else null }
            if (stored == null) { state(latest == null,"Saved workspace row disappeared from its retained history.");return null }
            state(latest != null && latest <= checkNotNull(attempt).eventSequence,"Saved workspace row lost its immutable history.")
            val event = event(database,checkNotNull(latest))
            state(event.value.kind == kind && event.value.fileId == id && event.value.documentSha256 == stored.sha,"Saved workspace row disagrees with its history.")
            return stored.bytes
        }
        fun validateAll() {
            val current = checkNotNull(attempt);val pin = registration.descriptor.documentSha256
            var global = initial(pin,"events")
            val heads = KINDS.associateWith { initial(pin,it) }.toMutableMap()
            val expected = KINDS.associateWith { sortedMapOf<String,String>() }
            for (sequence in 1..current.eventSequence) {
                cancellation.ensureActive()
                val item = event(database,sequence);val value = item.value
                state(value.previous == global && value.previousRole == heads.getValue(value.kind),"Workspace immutable history changed or was reordered.")
                global = item.global;heads[value.kind] = item.role;expected.getValue(value.kind)[value.fileId] = value.documentSha256
            }
            state(global == current.eventSha256 && KINDS.all { heads[it] == current.head(it) },"Workspace retained history lost its exact heads.")
            for (kind in KINDS) {
                val rows = expected.getValue(kind)
                state(allIds(kind) == rows.keys.toList(),"Workspace materialized identities changed or disappeared.")
                for ((id,sha) in rows) {
                    val bytes = row(kind,id);state(bytes != null && hash(bytes) == sha,"Workspace row differs from its retained history.")
                    when(kind) { CANDIDATE -> decodeCandidate(id,checkNotNull(bytes));READ -> decodeRead(id,checkNotNull(bytes));HISTORY -> history(id);REPLAY -> replay() }
                }
            }

        }
    }
    private fun append(database: SQLiteDatabase,attempt: ProcessorWorkspaceAttempt,kind: String,id: String,document: ByteArray): ProcessorWorkspaceAttempt {
        if (attempt.eventSequence >= MAX_EVENTS) throw ProcessorWorkspaceLimit()
        val value = WorkspaceObservationEvent(attempt.eventSequence + 1,kind,id,hash(document),attempt.eventSha256,
            attempt.head(kind))
        val body = codec.event(value);val global = eventHash(body);val role = roleHash(body)
        database.insertOrThrow("workspace_event",null,contentValuesOf("sequence" to value.sequence,"kind" to kind,"file_id" to id,"document" to body,"sha256" to hash(body)))
        val values = contentValuesOf("document" to document,"sha256" to value.documentSha256)
        if (database.update(table(kind),values,"file_id=?",arrayOf(id)) == 0) { values.put("file_id",id);database.insertOrThrow(table(kind),null,values) }
        return attempt.copy(candidateSha256=if(kind == CANDIDATE) role else attempt.candidateSha256,readSha256=if(kind == READ) role else attempt.readSha256,historySha256=if(kind == HISTORY) role else attempt.historySha256,replaySha256=if(kind == REPLAY) role else attempt.replaySha256,eventSequence=value.sequence,eventSha256=global)
    }
    private fun event(database: SQLiteDatabase,sequence: Long): ObservedEvent {
        val stored = blob(database,"workspace_event","sequence=?",arrayOf(sequence.toString()),2048)
        state(stored != null,"Workspace immutable event disappeared.")
        val value = decode { codec.event(checkNotNull(stored).bytes) }
        val columns = database.rawQuery("SELECT kind,file_id FROM workspace_event WHERE sequence=?",arrayOf(sequence.toString())).use { row ->
            state(row.moveToFirst(),"Workspace event changed during its transaction.");row.getString(0) to row.getString(1)
        }
        state(value.sequence == sequence && value.kind == columns.first && value.fileId == columns.second,"Workspace immutable event changed identity.")
        return ObservedEvent(value,eventHash(checkNotNull(stored).bytes),roleHash(stored.bytes))
    }
    private fun save(database: SQLiteDatabase,attempt: ProcessorWorkspaceAttempt) {
        val bytes = codec.attempt(attempt);store.requireWriteBudget(bytes.size.toLong() * 3 + 65536)
        val values = contentValuesOf("document" to bytes,"sha256" to hash(bytes))
        if (database.update("workspace_attempt",values,"singleton=1",null) == 0) { values.put("singleton",1);database.insertOrThrow("workspace_attempt",null,values) }
    }
    private fun putMeta(database: SQLiteDatabase,key: String,value: String) {
        val values = contentValuesOf("value" to value)
        if (database.update("client_meta",values,"key=?",arrayOf(key)) == 0) { values.put("key",key);database.insertOrThrow("client_meta",null,values) }
    }
    private fun blob(database: SQLiteDatabase,table: String,where: String,args: Array<String>?,maximum: Int): StoredBlob? {
        check(table in setOf("workspace_attempt","workspace_candidate","workspace_read","workspace_event","workspace_history","workspace_replay"))
        return database.rawQuery("SELECT length(document),CASE WHEN length(document) BETWEEN 1 AND $maximum THEN document END,CASE WHEN length(sha256)=64 THEN sha256 END FROM $table WHERE $where",args).use { row ->
            if(!row.moveToFirst()) null else {
                state(!row.isNull(1) && !row.isNull(2),"Saved workspace evidence exceeds its bound.")
                val bytes = row.getBlob(1);val sha = row.getString(2)
                state(bytes.size.toLong() == row.getLong(0) && sha.matches(DIGEST) && hash(bytes) == sha,"Saved workspace evidence changed its digest.")
                StoredBlob(bytes,sha)
            }
        }
    }
    private fun <T> decode(action: ()->T): T = try { action() }
        catch(error: ProcessorProtocolException) { throw ProcessorLibraryStateCorrupt("Saved workspace observation cannot be decoded.",error) }
        catch(error: IOException) { throw ProcessorLibraryStateCorrupt("Saved workspace observation is invalid.",error) }
    private class StoredBlob(val bytes: ByteArray,val sha: String)
    private class ObservedEvent(val value: WorkspaceObservationEvent,val global: String,val role: String)
    internal companion object {
        const val HISTORY = "history";const val REPLAY = "replay";const val REPLAY_ID = "cursor"
        val KINDS = listOf("candidate","read",HISTORY,REPLAY)
        const val CANDIDATE = "candidate";const val READ = "read";const val MAX_DOCUMENTS = 4096;const val MAX_EVENTS = 16384
        val DIGEST = Regex("[0-9a-f]{64}")
        fun sameContent(left: ProcessorWorkspaceCandidate,right: ProcessorWorkspaceCandidate) = (left.bytes == right.bytes || left.bytes == null) && left.mimeType == right.mimeType
        fun table(kind: String): String = when(kind) { CANDIDATE -> "workspace_candidate";READ -> "workspace_read";HISTORY -> "workspace_history";REPLAY -> "workspace_replay";else -> error("Unexpected workspace evidence role.") }
        fun initial(pin: String,kind: String) = hash(("mpa-native-workspace-base-v1:" + kind + "\u0000" + pin).toByteArray(Charsets.UTF_8))
        fun eventHash(bytes: ByteArray) = hash("mpa-native-workspace-event-v1\u0000".toByteArray(Charsets.UTF_8) + bytes)
        fun roleHash(bytes: ByteArray) = hash("mpa-native-workspace-role-v1\u0000".toByteArray(Charsets.UTF_8) + bytes)
        fun hash(bytes: ByteArray): String {
            val digest = MessageDigest.getInstance("SHA-256").digest(bytes);val chars = CharArray(digest.size * 2);val digits = "0123456789abcdef"
            digest.forEachIndexed { index,byte -> val value = byte.toInt() and 255;chars[index * 2] = digits[value ushr 4];chars[index * 2 + 1] = digits[value and 15] };return String(chars)
        }
        fun state(valid: Boolean,message: String) { if(!valid) throw ProcessorLibraryStateCorrupt(message) }
    }
}

private data class WorkspaceObservationEvent(val sequence: Long,val kind: String,val fileId: String,val documentSha256: String,val previous: String,val previousRole: String)

private class WorkspaceObservationCodec {
    private val canonical = CanonicalProtocolJson(65536)
    fun candidate(value: ProcessorWorkspaceCandidate): ByteArray = encode(JsonObject().apply {
        addProperty("fileId",value.fileId);addProperty("version",value.version.toString());addProperty("bytes",value.bytes?.toString());addProperty("mime",value.mimeType)
    })
    fun read(value: ProcessorWorkspaceRead): ByteArray = encode(JsonObject().apply {
        add("candidate",JsonParser.parseString(String(candidate(value.candidate),Charsets.UTF_8)));addProperty("sha256",value.sha256)
    })
    fun history(value: ProcessorWorkspaceHistorySummary): ByteArray = encode(JsonObject().apply {
        addProperty("kind",value.kind);addProperty("id",value.id);addProperty("recovery",value.recoveryRequired);addProperty("occurrences",value.occurrences.toString())
        addProperty("first",value.firstSha256);addProperty("last",value.lastSha256);addProperty("chain",value.chainSha256)
    })
    fun history(bytes: ByteArray): ProcessorWorkspaceHistorySummary {
        val root = parse(bytes,setOf("kind","id","recovery","occurrences","first","last","chain"))
        val kind = text(root,"kind");val id = text(root,"id");DriveWorkspaceMetadata.opaque(id)
        val first = text(root,"first");val last = text(root,"last");val chain = text(root,"chain")
        if(kind !in setOf("file","drive") || listOf(first,last,chain).any { !it.matches(Regex("[0-9a-f]{64}")) }) throw IOException("Saved Drive history changed its kind or digest.")
        return ProcessorWorkspaceHistorySummary(kind,id,boolean(root,"recovery"),number(root,"occurrences"),first,last,chain)
    }
    fun replay(value: ProcessorWorkspaceReplayState): ByteArray = encode(JsonObject().apply {
        addProperty("owner",value.owner.toString());add("start",cursor(value.start));add("cursor",cursor(value.cursor));addProperty("total",value.totalPages.toString())
        addProperty("batch",value.batchPages.toString());addProperty("active",value.active);addProperty("end",value.atEnd);addProperty("changed",value.inventoryChanged)
    })
    fun replay(bytes: ByteArray): ProcessorWorkspaceReplayState {
        val root = parse(bytes,setOf("owner","start","cursor","total","batch","active","end","changed"))
        val owner = text(root,"owner")
        if(!owner.matches(Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"))) throw IOException("Saved replay owner is invalid.")
        val id = UUID.fromString(owner)
        if(id.toString() != owner || id == UUID(0,0)) throw IOException("Saved replay owner is invalid.")
        val start = cursor(root.asJsonObject["start"]);val current = cursor(root.asJsonObject["cursor"])
        val total = number(root,"total",true);val batch = number(root,"batch",true);val active = boolean(root,"active");val end = boolean(root,"end");val changed = boolean(root,"changed")
        if(!start.sameScope(current) || batch !in 0..128L || total < batch || active && (end || changed)) throw IOException("Saved replay lost its exact bounded phase.")
        return ProcessorWorkspaceReplayState(id,start,current,total,batch.toInt(),active,end,changed)
    }
    private fun cursor(value: DriveWorkspaceCursor) = JsonObject().apply { addProperty("account",value.accountId);addProperty("drive",value.sharedDriveId);addProperty("token",value.token) }
    private fun cursor(root: JsonElement): DriveWorkspaceCursor {
        if(!root.isJsonObject || root.asJsonObject.keySet() != setOf("account","drive","token")) throw IOException("Saved Drive cursor fields changed.")
        return DriveWorkspaceCursor(text(root,"account"),if(root.asJsonObject["drive"].isJsonNull) null else text(root,"drive"),text(root,"token"))
    }
    private fun boolean(root: JsonElement,name: String): Boolean {
        val value = root.asJsonObject[name]
        if(!value.isJsonPrimitive || !value.asJsonPrimitive.isBoolean) throw IOException("Saved observation requires an explicit boolean.")
        return value.asBoolean
    }
    fun attempt(value: ProcessorWorkspaceAttempt): ByteArray = encode(JsonObject().apply {
        addProperty("id",value.id.toString());addProperty("revision",value.revision.toString());addProperty("connection",value.connectionSha256)
        if (value.inventoryStart == null) add("start",com.google.gson.JsonNull.INSTANCE) else addProperty("start",value.inventoryStart)
        if (value.firstUnresolved == null) add("unresolved",com.google.gson.JsonNull.INSTANCE) else addProperty("unresolved",value.firstUnresolved)
        addProperty("candidates",value.candidateSha256);addProperty("reads",value.readSha256);addProperty("listed",value.inventoryListed);addProperty("eventSequence",value.eventSequence.toString());addProperty("eventSha256",value.eventSha256)
        addProperty("startDrive",value.inventoryDriveId);addProperty("unresolvedDrive",value.unresolvedDriveId);addProperty("history",value.historySha256);addProperty("replay",value.replaySha256)
    })
    fun candidate(bytes: ByteArray): ProcessorWorkspaceCandidate = candidate(parse(bytes,setOf("fileId","version","bytes","mime")))
    private fun candidate(root: JsonElement) = ProcessorWorkspaceCandidate(text(root,"fileId"),number(root,"version"),if(root.asJsonObject["bytes"].isJsonNull) null else number(root,"bytes",true),text(root,"mime"))
    fun read(bytes: ByteArray): ProcessorWorkspaceRead {
        val root = parse(bytes,setOf("candidate","sha256")); val content = root.asJsonObject["candidate"]
        if (!content.isJsonObject || content.asJsonObject.keySet() != setOf("fileId","version","bytes","mime")) throw IOException("Invalid saved read candidate.")
        return ProcessorWorkspaceRead(candidate(content),text(root,"sha256"))
    }
    fun attempt(bytes: ByteArray): ProcessorWorkspaceAttempt {
        val legacy = setOf("id","revision","connection","start","unresolved","candidates","reads","listed","eventSequence","eventSha256")
        val root = parseEither(bytes,legacy,legacy + setOf("startDrive","unresolvedDrive","history","replay"))
        val id = text(root,"id")
        if (!id.matches(Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")) || id == "00000000-0000-0000-0000-000000000000") throw IOException("Invalid saved scan identity.")
        val listed = root.asJsonObject["listed"]
        if (!listed.isJsonPrimitive || !listed.asJsonPrimitive.isBoolean) throw IOException("Invalid saved inventory phase.")
        fun cursor(name: String): String? = if (root.asJsonObject[name].isJsonNull) null else text(root,name).also(DriveWorkspacePage::pageToken)
        val start = cursor("start");val unresolved = cursor("unresolved")
        if ((start != null || listed.asBoolean) && unresolved == null || listed.asBoolean && start == null) throw IOException("Inventory phase lost its unresolved cursor.")
        val connection = text(root,"connection"); val candidates = text(root,"candidates"); val reads = text(root,"reads");val eventSha = text(root,"eventSha256")
        if (listOf(connection,candidates,reads,eventSha).any { !it.matches(Regex("[0-9a-f]{64}")) }) throw IOException("Invalid saved observation digest.")
        val old = root.asJsonObject.keySet() == legacy
        fun drive(name: String): String? = if(old || root.asJsonObject[name].isJsonNull) null else text(root,name).also(DriveWorkspaceMetadata::opaque)
        val startDrive = drive("startDrive");val unresolvedDrive = drive("unresolvedDrive")
        if(start == null && startDrive != null || unresolved == null && unresolvedDrive != null || start != null && startDrive != unresolvedDrive) throw IOException("Saved cursors changed corpus.")
        val history = if(old) ProcessorWorkspaceObservations.initial(connection,"history") else text(root,"history")
        val replay = if(old) ProcessorWorkspaceObservations.initial(connection,"replay") else text(root,"replay")
        if(listOf(history,replay).any { !it.matches(Regex("[0-9a-f]{64}")) }) throw IOException("Saved replay lost its role heads.")
        return ProcessorWorkspaceAttempt(UUID.fromString(id),number(root,"revision"),connection,start,unresolved,candidates,reads,listed.asBoolean,number(root,"eventSequence",true),eventSha,startDrive,unresolvedDrive,history,replay)
    }
    fun event(value: WorkspaceObservationEvent): ByteArray = encode(JsonObject().apply {
        addProperty("sequence",value.sequence.toString());addProperty("kind",value.kind);addProperty("fileId",value.fileId)
        addProperty("documentSha256",value.documentSha256);addProperty("previous",value.previous);addProperty("previousRole",value.previousRole)
    })
    fun event(bytes: ByteArray): WorkspaceObservationEvent {
        val root = parse(bytes,setOf("sequence","kind","fileId","documentSha256","previous","previousRole"))
        val kind = text(root,"kind");val id = text(root,"fileId");DriveWorkspaceMetadata.opaque(id)
        val digest = text(root,"documentSha256");val previous = text(root,"previous");val previousRole = text(root,"previousRole")
        if(kind !in ProcessorWorkspaceObservations.KINDS || listOf(digest,previous,previousRole).any { !it.matches(Regex("[0-9a-f]{64}")) }) throw IOException("Invalid immutable observation event.")
        return WorkspaceObservationEvent(number(root,"sequence"),kind,id,digest,previous,previousRole)
    }
    private fun parseEither(bytes: ByteArray,first: Set<String>,second: Set<String>): JsonElement {
        val encoded = canonical.encode(bytes)
        if(!encoded.contentEquals(bytes)) throw IOException("Saved observation JSON changed its canonical form.")
        val root = JsonParser.parseString(String(bytes,Charsets.UTF_8))
        if(!root.isJsonObject || root.asJsonObject.keySet() != first && root.asJsonObject.keySet() != second) throw IOException("Saved observation fields changed.")
        return root
    }
    private fun encode(value: JsonElement) = canonical.encode(value.toString().toByteArray(Charsets.UTF_8))
    private fun parse(bytes: ByteArray,fields: Set<String>): JsonElement {
        val encoded = canonical.encode(bytes)
        if (!encoded.contentEquals(bytes)) throw IOException("Saved observation JSON changed its canonical form.")
        val root = JsonParser.parseString(String(bytes,Charsets.UTF_8))
        if (!root.isJsonObject || root.asJsonObject.keySet() != fields) throw IOException("Saved observation fields changed.")
        return root
    }
    private fun text(root: JsonElement,name: String): String {
        val value = root.asJsonObject[name]
        if (!value.isJsonPrimitive || !value.asJsonPrimitive.isString) throw IOException("Saved observation requires a text field.")
        return value.asString
    }
    private fun number(root: JsonElement,name: String,zero: Boolean = false): Long {
        val text = text(root,name); val number = text.toLongOrNull()
        if (number == null || number < (if(zero) 0 else 1) || number.toString() != text) throw IOException("Saved observation counter changed.")
        return number
    }
}

package com.myphotosanywhere.mpa.processor.client

import android.database.sqlite.SQLiteDatabase
import androidx.core.content.contentValuesOf
import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParser
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
    val inventoryStart: String?,val firstUnresolved: String?,val candidateSha256: String,val readSha256: String,val inventoryListed: Boolean,val eventSequence: Long,val eventSha256: String)

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
            previous?.eventSequence ?: 0,previous?.eventSha256 ?: initial(pin,"events"))
        store.requireWriteBudget();putMeta(database,"workspace_attempt_revision",attempt.revision.toString());save(database,attempt);attempt
    }
    suspend fun start(attempt: ProcessorWorkspaceAttempt,cursor: String): ProcessorWorkspaceAttempt {
        DriveWorkspacePage.pageToken(cursor)
        return change(attempt) { database,_ ->
            if (attempt.inventoryStart != null) throw ProcessorWorkspaceConflict()
            attempt.copy(inventoryStart=cursor,firstUnresolved=attempt.firstUnresolved ?: cursor).also { save(database,it) }
        }
    }
    suspend fun rememberCandidates(attempt: ProcessorWorkspaceAttempt,candidates: List<ProcessorWorkspaceCandidate>): ProcessorWorkspaceAttempt {
        if (candidates.size > 100) throw ProcessorWorkspaceLimit()
        val owned = candidates.toList()
        if (owned.size > 100) throw ProcessorWorkspaceLimit()
        if (owned.map { it.fileId }.distinct().size != owned.size) throw ProcessorWorkspaceConflict()
        return change(attempt) { database,state ->
            if (attempt.inventoryStart == null) throw ProcessorWorkspaceConflict()
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
            next
        }
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
        val counts = database.rawQuery("SELECT (SELECT count(*) FROM workspace_candidate),(SELECT count(*) FROM workspace_read),(SELECT count(*) FROM workspace_event),(SELECT coalesce(max(sequence),0) FROM workspace_event)",null).use { row ->
            state(row.moveToFirst(),"Workspace counters are missing.")
            val values = LongArray(4) { row.getLong(it) }
            state(values[0] in 0..MAX_DOCUMENTS.toLong() && values[1] in 0..MAX_DOCUMENTS.toLong() && values[2] in 0..MAX_EVENTS.toLong() && values[2] == values[3],"Workspace history is missing or exceeds its bound.")
            values
        }
        if (attempt == null) state(marker == null && counts.all { it == 0L },"Workspace evidence lost its pending owner.")
        else {
            state(marker == attempt.revision.toString() && attempt.connectionSha256 == registration.descriptor.documentSha256 && attempt.eventSequence == counts[2],"Workspace attempt changed its root, revision or retained history.")
            if (attempt.eventSequence == 0L) state(attempt.eventSha256 == initial(attempt.connectionSha256,"events") && attempt.candidateSha256 == initial(attempt.connectionSha256,CANDIDATE) && attempt.readSha256 == initial(attempt.connectionSha256,READ) && counts[0] == 0L && counts[1] == 0L,"Empty workspace history lost its anchored state.")
            else {
                val event = event(database,attempt.eventSequence)
                state(event.global == attempt.eventSha256 && event.role == if (event.value.kind == CANDIDATE) attempt.candidateSha256 else attempt.readSha256,"Workspace history lost its exact tail.")
            }
        }
        val result = Snapshot(database,registration,attempt,counts[0].toInt(),counts[1].toInt(),cancellation)
        if (full && attempt != null) result.validateAll()
        return result
    }
    private inner class Snapshot(val database: SQLiteDatabase,val registration: ProcessorLibraryRegistration,val attempt: ProcessorWorkspaceAttempt?,val candidateCount: Int,val readCount: Int,val cancellation: CoroutineContext) {
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
        private fun row(kind: String,id: String): ByteArray? {
            cancellation.ensureActive()
            val stored = blob(database,table(kind),"file_id=?",arrayOf(id),2048)
            val latest = database.rawQuery("SELECT sequence FROM workspace_event WHERE kind=? AND file_id=? ORDER BY sequence DESC LIMIT 1",arrayOf(kind,id)).use { row -> if(row.moveToFirst()) row.getLong(0) else null }
            if (stored == null) { state(latest == null,"Saved workspace row disappeared from its retained history.");return null }
            state(latest != null && latest <= checkNotNull(attempt).eventSequence,"Saved workspace row lost its immutable history.")
            val event = event(database,checkNotNull(latest))
            state(event.value.kind == kind && event.value.fileId == id && event.value.documentSha256 == stored.sha,"Saved workspace row disagrees with its history.")
            return stored.bytes
        }
        fun validateAll() {
            val current = checkNotNull(attempt);val pin = registration.descriptor.documentSha256
            var global = initial(pin,"events");var candidates = initial(pin,CANDIDATE);var reads = initial(pin,READ)
            val expectedCandidates = sortedMapOf<String,String>();val expectedReads = sortedMapOf<String,String>()
            for (sequence in 1..current.eventSequence) {
                cancellation.ensureActive()
                val item = event(database,sequence);val value = item.value
                state(value.previous == global && value.previousRole == if(value.kind == CANDIDATE) candidates else reads,"Workspace immutable history changed or was reordered.")
                global = item.global
                if(value.kind == CANDIDATE) { candidates = item.role;expectedCandidates[value.fileId] = value.documentSha256 }
                else { reads = item.role;expectedReads[value.fileId] = value.documentSha256 }
            }
            state(global == current.eventSha256 && candidates == current.candidateSha256 && reads == current.readSha256,"Workspace retained history lost its exact heads.")
            state(allIds(CANDIDATE) == expectedCandidates.keys.toList() && allIds(READ) == expectedReads.keys.toList(),"Workspace materialized identities changed or disappeared.")
            for ((id,sha) in expectedCandidates) { val bytes = row(CANDIDATE,id);state(bytes != null && hash(bytes) == sha,"Workspace candidate differs from its history.");decodeCandidate(id,checkNotNull(bytes)) }
            for ((id,sha) in expectedReads) { val bytes = row(READ,id);state(bytes != null && hash(bytes) == sha,"Workspace read differs from its history.");decodeRead(id,checkNotNull(bytes)) }
        }
    }
    private fun append(database: SQLiteDatabase,attempt: ProcessorWorkspaceAttempt,kind: String,id: String,document: ByteArray): ProcessorWorkspaceAttempt {
        if (attempt.eventSequence >= MAX_EVENTS) throw ProcessorWorkspaceLimit()
        val value = WorkspaceObservationEvent(attempt.eventSequence + 1,kind,id,hash(document),attempt.eventSha256,
            if(kind == CANDIDATE) attempt.candidateSha256 else attempt.readSha256)
        val body = codec.event(value);val global = eventHash(body);val role = roleHash(body)
        database.insertOrThrow("workspace_event",null,contentValuesOf("sequence" to value.sequence,"kind" to kind,"file_id" to id,"document" to body,"sha256" to hash(body)))
        val values = contentValuesOf("document" to document,"sha256" to value.documentSha256)
        if (database.update(table(kind),values,"file_id=?",arrayOf(id)) == 0) { values.put("file_id",id);database.insertOrThrow(table(kind),null,values) }
        return attempt.copy(candidateSha256=if(kind == CANDIDATE) role else attempt.candidateSha256,readSha256=if(kind == READ) role else attempt.readSha256,eventSequence=value.sequence,eventSha256=global)
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
        check(table in setOf("workspace_attempt","workspace_candidate","workspace_read","workspace_event"))
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
    private companion object {
        const val CANDIDATE = "candidate";const val READ = "read";const val MAX_DOCUMENTS = 4096;const val MAX_EVENTS = 16384
        val DIGEST = Regex("[0-9a-f]{64}")
        fun sameContent(left: ProcessorWorkspaceCandidate,right: ProcessorWorkspaceCandidate) = (left.bytes == right.bytes || left.bytes == null) && left.mimeType == right.mimeType
        fun table(kind: String): String = when(kind) { CANDIDATE -> "workspace_candidate";READ -> "workspace_read";else -> error("Unexpected workspace evidence role.") }
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
    fun attempt(value: ProcessorWorkspaceAttempt): ByteArray = encode(JsonObject().apply {
        addProperty("id",value.id.toString());addProperty("revision",value.revision.toString());addProperty("connection",value.connectionSha256)
        if (value.inventoryStart == null) add("start",com.google.gson.JsonNull.INSTANCE) else addProperty("start",value.inventoryStart)
        if (value.firstUnresolved == null) add("unresolved",com.google.gson.JsonNull.INSTANCE) else addProperty("unresolved",value.firstUnresolved)
        addProperty("candidates",value.candidateSha256);addProperty("reads",value.readSha256);addProperty("listed",value.inventoryListed);addProperty("eventSequence",value.eventSequence.toString());addProperty("eventSha256",value.eventSha256)
    })
    fun candidate(bytes: ByteArray): ProcessorWorkspaceCandidate = candidate(parse(bytes,setOf("fileId","version","bytes","mime")))
    private fun candidate(root: JsonElement) = ProcessorWorkspaceCandidate(text(root,"fileId"),number(root,"version"),if(root.asJsonObject["bytes"].isJsonNull) null else number(root,"bytes",true),text(root,"mime"))
    fun read(bytes: ByteArray): ProcessorWorkspaceRead {
        val root = parse(bytes,setOf("candidate","sha256")); val content = root.asJsonObject["candidate"]
        if (!content.isJsonObject || content.asJsonObject.keySet() != setOf("fileId","version","bytes","mime")) throw IOException("Invalid saved read candidate.")
        return ProcessorWorkspaceRead(candidate(content),text(root,"sha256"))
    }
    fun attempt(bytes: ByteArray): ProcessorWorkspaceAttempt {
        val root = parse(bytes,setOf("id","revision","connection","start","unresolved","candidates","reads","listed","eventSequence","eventSha256"))
        val id = text(root,"id")
        if (!id.matches(Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")) || id == "00000000-0000-0000-0000-000000000000") throw IOException("Invalid saved scan identity.")
        val listed = root.asJsonObject["listed"]
        if (!listed.isJsonPrimitive || !listed.asJsonPrimitive.isBoolean) throw IOException("Invalid saved inventory phase.")
        fun cursor(name: String): String? = if (root.asJsonObject[name].isJsonNull) null else text(root,name).also(DriveWorkspacePage::pageToken)
        val start = cursor("start");val unresolved = cursor("unresolved")
        if ((start != null || listed.asBoolean) && unresolved == null || listed.asBoolean && start == null) throw IOException("Inventory phase lost its unresolved cursor.")
        val connection = text(root,"connection"); val candidates = text(root,"candidates"); val reads = text(root,"reads");val eventSha = text(root,"eventSha256")
        if (listOf(connection,candidates,reads,eventSha).any { !it.matches(Regex("[0-9a-f]{64}")) }) throw IOException("Invalid saved observation digest.")
        return ProcessorWorkspaceAttempt(UUID.fromString(id),number(root,"revision"),connection,start,unresolved,candidates,reads,listed.asBoolean,number(root,"eventSequence",true),eventSha)
    }
    fun event(value: WorkspaceObservationEvent): ByteArray = encode(JsonObject().apply {
        addProperty("sequence",value.sequence.toString());addProperty("kind",value.kind);addProperty("fileId",value.fileId)
        addProperty("documentSha256",value.documentSha256);addProperty("previous",value.previous);addProperty("previousRole",value.previousRole)
    })
    fun event(bytes: ByteArray): WorkspaceObservationEvent {
        val root = parse(bytes,setOf("sequence","kind","fileId","documentSha256","previous","previousRole"))
        val kind = text(root,"kind");val id = text(root,"fileId");DriveWorkspaceMetadata.opaque(id)
        val digest = text(root,"documentSha256");val previous = text(root,"previous");val previousRole = text(root,"previousRole")
        if(kind !in setOf("candidate","read") || listOf(digest,previous,previousRole).any { !it.matches(Regex("[0-9a-f]{64}")) }) throw IOException("Invalid immutable observation event.")
        return WorkspaceObservationEvent(number(root,"sequence"),kind,id,digest,previous,previousRole)
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

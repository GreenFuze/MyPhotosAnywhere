package com.myphotosanywhere.mpa.processor.client

import android.database.sqlite.SQLiteDatabase
import androidx.core.content.contentValuesOf
import com.google.gson.JsonNull
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.drive.DriveWorkspaceMetadata
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
import com.myphotosanywhere.mpa.processor.protocol.LibraryCurrentControl
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolCompatibilityException
import kotlin.coroutines.CoroutineContext
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import java.security.MessageDigest
import java.util.UUID

internal data class ProcessorControlAttempt(val id: UUID,val revision: Long,val connectionSha256: String,val email: String,
    val phase: String,val sequence: Long?,val version: Long?,val maxVersion: Long,val driveId: String?,val declaredBytes: Long? = null,val declaredSha256: String? = null,val contentVersion: Long = 0,val contentSha256: String? = null)

/** Durable OBSERVATIONS only. Even a fenced attempt is not activation authority.
 * Every begin retires the previous nonce before I/O. Signed head history and the
 * actual-file version high-water survive failures/restarts and cannot be reset by a
 * new inventory. All mutations use the existing serialized state transaction. */
internal class ProcessorCurrentControlObservations(private val store: ProcessorLibraryStateStore) {
    suspend fun begin(connectionSha256: String): ProcessorControlAttempt = transaction { database,registration,cancellation ->
        val pin = registration.descriptor
        if (pin.currentFileId == null || pin.documentSha256 != connectionSha256 || registration.account == null) throw ProcessorWorkspaceConflict()
        val old = load(database,registration,cancellation).attempt
        val attempt = ProcessorControlAttempt(UUID.randomUUID(),Math.addExact(old?.revision ?: 0,1),connectionSha256,
            registration.account.email,"open",null,null,old?.maxVersion ?: 0,old?.driveId,contentVersion=old?.contentVersion ?: 0,contentSha256=old?.contentSha256)
        store.requireWriteBudget(65536);save(database,attempt);attempt
    }

    suspend fun assertCaptured(attempt: ProcessorControlAttempt) = transaction { database,registration,cancellation ->
        val state = load(database,registration,cancellation)
        if (attempt.phase != "captured" || state.attempt != attempt || registration.account?.email != attempt.email) throw ProcessorWorkspaceConflict()
    }

    suspend fun rememberCapture(attempt: ProcessorControlAttempt,metadata: DriveWorkspaceMetadata): ProcessorControlAttempt = transaction { database,registration,cancellation ->
        val state = load(database,registration,cancellation)
        if (attempt.phase != "open" || state.attempt != attempt || registration.account?.email != attempt.email) throw ProcessorWorkspaceConflict()
        val pin = registration.descriptor
        if (metadata.id != pin.currentFileId || metadata.mimeType != "application/json" || metadata.trashed || metadata.downloadable != true ||
            pin.workspace.folderId !in metadata.parents || metadata.bytes == null || metadata.bytes !in 1..262144L || metadata.version < attempt.maxVersion ||
            attempt.maxVersion != 0L && metadata.sharedDriveId != attempt.driveId) throw ProcessorWorkspaceConflict()
        store.requireWriteBudget(65536)
        attempt.copy(phase="reading",version=metadata.version,maxVersion=metadata.version,driveId=metadata.sharedDriveId,
            declaredBytes=metadata.bytes,declaredSha256=metadata.sha256).also { save(database,it) }
    }

    suspend fun rejectFence(attempt: ProcessorControlAttempt,metadata: DriveWorkspaceMetadata) = transaction { database,registration,cancellation ->
        val state = load(database,registration,cancellation)
        if (attempt.phase != "captured" || state.attempt != attempt || registration.account?.email != attempt.email || metadata.id != registration.descriptor.currentFileId) throw ProcessorWorkspaceConflict()
        store.requireWriteBudget(65536)
        save(database,attempt.copy(phase="blocked",maxVersion=maxOf(attempt.maxVersion,metadata.version)))
    }

    suspend fun rememberBytes(attempt: ProcessorControlAttempt,envelope: ByteArray): ProcessorControlAttempt {
        if (envelope.size !in 1..262144) throw ProcessorProtocolException("Current-control read exceeds its wire bound.")
        val owned = envelope.copyOf();val digest = hash(owned)
        return transaction { database,registration,cancellation ->
            val state = load(database,registration,cancellation)
            if (attempt.phase != "reading" || state.attempt != attempt || registration.account?.email != attempt.email ||
                owned.size.toLong() != attempt.declaredBytes || attempt.declaredSha256 != null && digest != attempt.declaredSha256 ||
                attempt.contentVersion == attempt.version && attempt.contentSha256 != digest) throw ProcessorWorkspaceConflict()
            store.requireWriteBudget(65536)
            attempt.copy(phase="read",contentVersion=checkNotNull(attempt.version),contentSha256=digest).also { save(database,it) }
        }
    }

    suspend fun observe(attempt: ProcessorControlAttempt,metadata: DriveWorkspaceMetadata,envelope: ByteArray): Pair<ProcessorControlAttempt,LibraryCurrentControl> {
        if (envelope.size !in 1..262144) throw ProcessorProtocolException("Current-control observation exceeds its wire bound.")
        val owned = envelope.copyOf()
        return transaction { database,registration,cancellation ->
            val state = load(database,registration,cancellation)
            if (attempt.phase != "read" || state.attempt != attempt || registration.account?.email != attempt.email || hash(owned) != attempt.contentSha256) throw ProcessorWorkspaceConflict()
            val pin = registration.descriptor
            if (metadata.id != pin.currentFileId || metadata.mimeType != "application/json" || metadata.trashed || metadata.downloadable != true ||
                pin.workspace.folderId !in metadata.parents || metadata.bytes != owned.size.toLong() || metadata.bytes != attempt.declaredBytes || metadata.sha256 != attempt.declaredSha256 || metadata.version != attempt.version || metadata.version < attempt.maxVersion ||
                attempt.maxVersion != 0L && metadata.sharedDriveId != attempt.driveId) throw ProcessorWorkspaceConflict()
            val head = LibraryCurrentControl.read(owned,pin)
            if (metadata.sha256 != null && metadata.sha256 != head.envelopeSha256) throw ProcessorWorkspaceConflict()
            state.head?.let(head::assertSuccessorOf)
            if (database.rawQuery("SELECT 1 FROM controller_document WHERE sequence=?",arrayOf(head.sequence.toString())).use { it.moveToFirst() })
                throw ProcessorControllerConflict("A current-control signature reused a retained controller-policy sequence.")
            store.requireWriteBudget(owned.size.toLong() * 3 + 65536)
            if (state.head == null || state.head.sequence < head.sequence) {
                if (state.count == MAX_HEADS) throw ProcessorWorkspaceLimit()
                database.insertOrThrow("current_control_head",null,contentValuesOf("sequence" to head.sequence,"envelope" to owned,"sha256" to head.envelopeSha256))
            }
            val captured = attempt.copy(phase="captured",sequence=head.sequence,version=metadata.version,maxVersion=metadata.version,driveId=metadata.sharedDriveId)
            save(database,captured);captured to head
        }
    }

    suspend fun fence(attempt: ProcessorControlAttempt): ProcessorControlAttempt = transaction { database,registration,cancellation ->
        val state = load(database,registration,cancellation)
        if (attempt.phase != "captured" || state.attempt != attempt || registration.account?.email != attempt.email) throw ProcessorWorkspaceConflict()
        store.requireWriteBudget(65536);attempt.copy(phase="fenced").also { save(database,it) }
    }

    private suspend fun <T> transaction(action: (SQLiteDatabase,ProcessorLibraryRegistration,CoroutineContext)->T): T {
        val cancellation = currentCoroutineContext()
        return store.controllerTransaction { database,registration -> action(database,registration,cancellation) }
    }
    private data class State(val attempt: ProcessorControlAttempt?,val head: LibraryCurrentControl?,val count: Int)
    private fun load(database: SQLiteDatabase,registration: ProcessorLibraryRegistration,cancellation: CoroutineContext): State {
        val pin = registration.descriptor
        val marker = database.rawQuery("SELECT length(value),value FROM client_meta WHERE key=?",arrayOf(MARKER)).use { row ->
            if (!row.moveToFirst()) null else { corrupt(row.getLong(0) == 64L,"Current-control integrity marker exceeds its bound.");row.getString(1) }
        }
        var previous: LibraryCurrentControl? = null
        var count = 0
        val history = MessageDigest.getInstance("SHA-256")
        database.rawQuery("SELECT sequence,length(envelope),sha256 FROM current_control_head ORDER BY sequence LIMIT 4097",null).use { rows ->
            while(rows.moveToNext()) {
                cancellation.ensureActive()
                corrupt(++count <= MAX_HEADS && rows.getLong(1) in 1..262144L,"Current-control history exceeds its retained bound.")
                val sequence = rows.getLong(0);val digest = rows.getString(2)
                val bytes = database.rawQuery("SELECT envelope FROM current_control_head WHERE sequence=?",arrayOf(sequence.toString())).use { body ->
                    corrupt(body.moveToFirst(),"Current-control history disappeared during its owned read.");body.getBlob(0)
                }
                corrupt(bytes.size.toLong() == rows.getLong(1) && hash(bytes) == digest,"A retained current-control envelope changed its immutable bytes.")
                val head = try { LibraryCurrentControl.read(bytes,pin).also { next -> previous?.let(next::assertSuccessorOf) } }
                    catch(error: ProcessorProtocolException) { throw ProcessorLibraryStateCorrupt("Retained current-control authority cannot be verified.",error) }
                    catch(error: ProtocolCompatibilityException) { throw ProcessorLibraryStateCorrupt("Retained current-control protocol requirements changed.",error) }
                cancellation.ensureActive()
                corrupt(head.sequence == sequence,"Current-control history changed its root sequence.")
                previous = head;history.update("$sequence:$digest\n".toByteArray(Charsets.US_ASCII))
            }
        }
        corrupt(!database.rawQuery("SELECT 1 FROM current_control_head h JOIN controller_document d ON h.sequence=d.sequence LIMIT 1",null).use { it.moveToFirst() },"Root signing history reused a sequence across document roles.")
        val attempt = database.rawQuery("SELECT length(document),sha256 FROM current_control_attempt WHERE singleton=1",null).use { row ->
            if (!row.moveToFirst()) null else {
                corrupt(row.getLong(0) in 1..4096L,"Saved current-control attempt exceeds its bound.")
                val bytes = database.rawQuery("SELECT document FROM current_control_attempt WHERE singleton=1",null).use { body ->
                    corrupt(body.moveToFirst(),"Saved current-control attempt disappeared.");body.getBlob(0)
                }
                corrupt(bytes.size.toLong() == row.getLong(0) && hash(bytes) == row.getString(1),"Saved current-control attempt changed its digest.")
                decode(bytes).also {
                    corrupt(it.connectionSha256 == pin.documentSha256,"Saved current-control attempt changed its connection or credential owner.")
                    corrupt(it.sequence == null || it.sequence == previous?.sequence,"Current-control attempt lost its highest observed head.")
                    corrupt(it.sequence == null || it.contentSha256 == previous?.envelopeSha256,"Current-control attempt lost the exact selected read receipt.")
                    corrupt(marker == stateHash(pin.documentSha256,history.digest(),hash(bytes)),"Current-control state lost its complete integrity record.")
                }
            }
        }
        corrupt(attempt != null || count == 0 && marker == null,"Current-control history lost its retained attempt; never recreate a nonce over it.")
        return State(attempt,previous,count)
    }

    private fun save(database: SQLiteDatabase,attempt: ProcessorControlAttempt) {
        val bytes = encode(attempt)
        val values = contentValuesOf("document" to bytes,"sha256" to hash(bytes))
        if (database.update("current_control_attempt",values,"singleton=1",null) == 0) {
            values.put("singleton",1);database.insertOrThrow("current_control_attempt",null,values)
        }
        val history = MessageDigest.getInstance("SHA-256")
        database.rawQuery("SELECT sequence,sha256 FROM current_control_head ORDER BY sequence",null).use { rows ->
            while(rows.moveToNext()) history.update("${rows.getLong(0)}:${rows.getString(1)}\n".toByteArray(Charsets.US_ASCII))
        }
        val marker = contentValuesOf("value" to stateHash(attempt.connectionSha256,history.digest(),hash(bytes)))
        if (database.update("client_meta",marker,"key=?",arrayOf(MARKER)) == 0) {
            marker.put("key",MARKER);database.insertOrThrow("client_meta",null,marker)
        }
    }

    private fun encode(value: ProcessorControlAttempt): ByteArray = CanonicalProtocolJson(4096).encode(JsonObject().apply {
        addProperty("id",value.id.toString());addProperty("revision",value.revision.toString());addProperty("pin",value.connectionSha256);addProperty("email",value.email)
        addProperty("phase",value.phase);add("sequence",value.sequence?.let { com.google.gson.JsonPrimitive(it.toString()) } ?: JsonNull.INSTANCE)
        add("version",value.version?.let { com.google.gson.JsonPrimitive(it.toString()) } ?: JsonNull.INSTANCE)
        addProperty("maxVersion",value.maxVersion.toString());addProperty("drive",value.driveId)
        add("bytes",value.declaredBytes?.let { com.google.gson.JsonPrimitive(it.toString()) } ?: JsonNull.INSTANCE);addProperty("sha256",value.declaredSha256)
        addProperty("contentVersion",value.contentVersion.toString());addProperty("contentSha256",value.contentSha256)
    }.toString().toByteArray(Charsets.UTF_8))
    private fun decode(bytes: ByteArray): ProcessorControlAttempt {
        try {
            val root = JsonParser.parseString(bytes.toString(Charsets.UTF_8)).asJsonObject
            corrupt(root.keySet() == setOf("id","revision","pin","email","phase","sequence","version","maxVersion","drive","bytes","sha256","contentVersion","contentSha256"),"Saved current-control attempt changed its fields.")
            fun text(name: String): String { val value = root[name];corrupt(value.isJsonPrimitive && value.asJsonPrimitive.isString,"Saved current-control field is not text.");return value.asString }
            fun number(name: String,zero: Boolean = false): Long { val text = text(name);val value = text.toLongOrNull();corrupt(value != null && value >= (if(zero) 0 else 1) && value.toString() == text,"Saved current-control counter is invalid.");return checkNotNull(value) }
            val id = UUID.fromString(text("id"));corrupt(id != UUID(0,0) && id.toString() == text("id"),"Saved current-control nonce is invalid.")
            val phase = text("phase");val sequence = if(root["sequence"].isJsonNull) null else number("sequence");val version = if(root["version"].isJsonNull) null else number("version")
            val max = number("maxVersion",true);val drive = if(root["drive"].isJsonNull) null else text("drive").also(DriveWorkspaceMetadata::opaque)
            val length = if(root["bytes"].isJsonNull) null else number("bytes");val declared = if(root["sha256"].isJsonNull) null else text("sha256")
            corrupt(length == null || length in 1..262144L,"Saved current-control byte count exceeds its bound.")
            corrupt(declared == null || declared.matches(Regex("[0-9a-f]{64}")),"Saved current-control checksum is invalid.")
            val contentVersion = number("contentVersion",true);val contentSha = if(root["contentSha256"].isJsonNull) null else text("contentSha256")
            corrupt(contentVersion <= max && (contentVersion == 0L) == (contentSha == null) && (contentSha == null || contentSha.matches(Regex("[0-9a-f]{64}"))),"Saved current-control byte receipt lost its exact version/digest.")
            val validPhase = when(phase) {
                "open" -> sequence == null && version == null && length == null && declared == null
                "reading" -> sequence == null && version == max && max > 0 && length != null
                "read" -> sequence == null && version == max && contentVersion == max && contentSha != null && max > 0 && length != null
                "captured","fenced" -> sequence != null && version == max && contentVersion == max && contentSha != null && max > 0 && length != null
                "blocked" -> sequence != null && version != null && version <= max && max > 0 && length != null
                else -> false
            }
            corrupt(validPhase && (max != 0L || drive == null),"Saved current-control phase lost its exact version fence.")
            return ProcessorControlAttempt(id,number("revision"),text("pin"),text("email"),phase,sequence,version,max,drive,length,declared,contentVersion,contentSha).also {
                corrupt(encode(it).contentEquals(bytes),"Saved current-control attempt lost its canonical encoding.")
            }
        } catch(error: ProcessorLibraryStateCorrupt) { throw error }
          catch(error: RuntimeException) { throw ProcessorLibraryStateCorrupt("Saved current-control attempt cannot be decoded.",error) }
    }
    private fun stateHash(pin: String,history: ByteArray,attempt: String) = hash(pin.toByteArray(Charsets.US_ASCII) + history + attempt.toByteArray(Charsets.US_ASCII))
    private fun hash(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it.toInt() and 255) }
    private fun corrupt(valid: Boolean,message: String) { if(!valid) throw ProcessorLibraryStateCorrupt(message) }
    companion object { private const val MAX_HEADS = 4096;private const val MARKER = "current_control_state_sha256" }
}

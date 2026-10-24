package com.myphotosanywhere.mpa.processor.client

import android.database.sqlite.SQLiteDatabase
import androidx.core.content.contentValuesOf
import com.myphotosanywhere.mpa.processor.protocol.CatalogPublicationReader
import com.myphotosanywhere.mpa.processor.protocol.CatalogPublisherAuthorization
import com.myphotosanywhere.mpa.processor.protocol.CatalogSourceConfiguration
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolCompatibilityException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentReader
import com.myphotosanywhere.mpa.processor.protocol.SignedProtocolDocuments
import com.myphotosanywhere.mpa.processor.protocol.TrustedProtocolActor
import com.myphotosanywhere.mpa.processor.protocol.VerifiedProtocolDocument
import java.io.IOException
import java.security.MessageDigest
import java.util.Base64

internal class ProcessorControllerConflict(message: String) : IOException(message)
internal class ProcessorControllerHistoryFull : IOException("The accepted controller history needs maintenance before more policies can be received. Current library state was preserved.")

/** Accepted local history, not a claim that a cloud inventory is complete/current. A
 * consumer must fence incomplete observations before using this policy for activation. */
internal data class ProcessorControllerState(
    val connection: LibraryConnectionDescriptor,
    val publisher: CatalogPublisherAuthorization?,
    val sourceSequence: Long?,
    val sources: CatalogSourceConfiguration?,
)

/** Borrows the serialized private state owner. No network, original-file writes, device
 * enrollment or catalog activation occurs here. Every decision and its immutable signed
 * evidence commit in the same SQLite transaction as the independently approved pin. */
internal class ProcessorLibraryControllerJournal(private val store: ProcessorLibraryStateStore) {
    suspend fun current(): ProcessorControllerState = store.controllerTransaction { database, registration ->
        Session(database,registration.descriptor).current()
    }

    suspend fun accept(envelope: ByteArray): ProcessorControllerState {
        if (envelope.isEmpty() || envelope.size > ProtocolDocumentReader.MAX_DOCUMENT_BYTES)
            throw ProcessorProtocolException("Controller envelope exceeds its wire bound.")
        val owned = envelope.copyOf()
        return store.controllerTransaction { database, registration ->
            if (registration.account == null) throw IOException("Review and approve the Google account before receiving shared policies.")
            val session = Session(database,registration.descriptor)
            val previous = session.current()
            val incoming = session.decode(owned)
            val existing = session.stored(incoming.header.sequence)
            if (existing != null) {
                if (!existing.bytes.contentEquals(owned))
                    throw ProcessorControllerConflict("The controller reused an accepted sequence with different signed evidence. Current state was preserved.")
                previous
            } else {
                if (session.count >= MAX_DOCUMENTS) throw ProcessorControllerHistoryFull()
                if (incoming.sources != null) session.checkSourceNeighbors(incoming)
                store.requireWriteBudget(owned.size.toLong() * 3 + 65536)
                session.save(incoming)
                session.current()
            }
        }
    }

    private class Session(val database: SQLiteDatabase, val pin: LibraryConnectionDescriptor) {
        private val actor = TrustedProtocolActor(pin.libraryId,pin.controllerId,pin.controllerEpoch,
            Base64.getDecoder().decode(pin.controllerPublicKeySpki),KINDS)
        private val signed = SignedProtocolDocuments(setOf(CatalogPublisherAuthorization.CAPABILITY,CatalogSourceConfiguration.CAPABILITY))
        private val reader = CatalogPublicationReader(pin.workspace)
        var count: Int = 0
            private set

        fun decode(bytes: ByteArray): Policy {
            val header = signed.verifyAndRead(bytes,actor)
            return when (header.kind) {
                PUBLISHER -> Policy(bytes,header,reader.readPublisherAuthorization(bytes,actor),null)
                SOURCES -> Policy(bytes,header,null,reader.readSourceConfiguration(header,pin.libraryId))
                else -> throw ProcessorProtocolException("Unapproved controller policy kind.")
            }
        }

        fun current(): ProcessorControllerState {
            count = database.rawQuery("SELECT count(*) FROM controller_document",null).use { row ->
                state(row.moveToFirst(),"Controller history count is missing.")
                val value = row.getLong(0)
                state(value in 0L..MAX_DOCUMENTS.toLong(),"Controller history exceeds its bound.")
                value.toInt()
            }
            val anchor = database.rawQuery("SELECT connection_sha256 FROM controller_anchor WHERE singleton=1",null).use { row ->
                if (row.moveToFirst()) row.getString(0) else null
            }
            val marker = database.rawQuery("SELECT value FROM client_meta WHERE key='controller_anchor_sha256'",null).use { row ->
                if (row.moveToFirst()) row.getString(0) else null
            }
            if (count == 0) {
                val selections = database.rawQuery("SELECT 1 FROM controller_selection LIMIT 1",null).use { it.moveToFirst() }
                state(anchor == null && marker == null && !selections,"Accepted controller evidence is missing.")
                return ProcessorControllerState(pin,null,null,null)
            }
            state(anchor == pin.documentSha256 && marker == anchor,"Accepted controller history lost its approved connection anchor.")
            val publisher = selected(PUBLISHER)
            val sources = selected(SOURCES)
            return ProcessorControllerState(pin,publisher?.publisher,sources?.header?.sequence,sources?.sources)
        }

        private fun selected(kind: String): Policy? {
            val highest = database.rawQuery("SELECT max(sequence) FROM controller_document WHERE kind=?",arrayOf(kind)).use { row ->
                state(row.moveToFirst(),"Controller history index is missing.")
                if (row.isNull(0)) null else row.getLong(0)
            }
            val selection = database.rawQuery("SELECT sequence FROM controller_selection WHERE kind=?",arrayOf(kind)).use { row ->
                if (row.moveToFirst()) row.getLong(0) else null
            }
            state(highest == selection,"Selected controller history is missing or was rewound.")
            if (selection == null) return null
            return checkNotNull(stored(selection)).also { state(it.header.kind == kind,"Selected controller history changed its role.") }
        }

        fun stored(sequence: Long): Policy? {
            val metadata = database.rawQuery("SELECT kind,length(envelope),sha256 FROM controller_document WHERE sequence=?",arrayOf(sequence.toString())).use { row ->
                if (!row.moveToFirst()) return null
                state(row.getLong(1) in 1L..ProtocolDocumentReader.MAX_DOCUMENT_BYTES.toLong(),"Saved controller envelope exceeds its bound.")
                Triple(row.getString(0),row.getLong(1),row.getString(2))
            }
            val bytes = database.rawQuery("SELECT envelope FROM controller_document WHERE sequence=?",arrayOf(sequence.toString())).use { row ->
                state(row.moveToFirst(),"Saved controller envelope disappeared during its transaction.")
                row.getBlob(0)
            }
            state(bytes.size.toLong() == metadata.second && hash(bytes) == metadata.third,"Saved controller envelope changed its immutable digest.")
            val result = try { decode(bytes) }
                catch (error: ProcessorProtocolException) { throw ProcessorLibraryStateCorrupt("Saved controller evidence cannot be verified.",error) }
                catch (error: ProtocolCompatibilityException) { throw ProcessorLibraryStateCorrupt("Saved controller evidence changed its protocol requirements.",error) }
            state(result.header.kind == metadata.first && result.header.sequence == sequence,"Saved controller evidence changed its identity.")
            return result
        }

        fun checkSourceNeighbors(incoming: Policy) {
            for ((operator,order) in listOf("<" to "DESC",">" to "ASC")) {
                val neighbor = database.rawQuery("SELECT sequence FROM controller_document WHERE kind=? AND sequence $operator ? ORDER BY sequence $order LIMIT 1",
                    arrayOf(SOURCES,incoming.header.sequence.toString())).use { row -> if (row.moveToFirst()) row.getLong(0) else null }
                if (neighbor != null) {
                    val other = checkNotNull(stored(neighbor))
                    val lower = if (neighbor < incoming.header.sequence) other else incoming
                    val upper = if (neighbor < incoming.header.sequence) incoming else other
                    val before = checkNotNull(lower.sources).revision
                    val after = checkNotNull(upper.sources).revision
                    if (before > after || before == after && lower.header.canonicalPayload != upper.header.canonicalPayload)
                        throw ProcessorControllerConflict("Source revisions conflict with accepted signed history. Current source visibility was preserved.")
                }
            }
        }

        fun save(policy: Policy) {
            if (count == 0) {
                database.insertOrThrow("controller_anchor",null,contentValuesOf("singleton" to 1,"connection_sha256" to pin.documentSha256))
                database.insertOrThrow("client_meta",null,contentValuesOf("key" to "controller_anchor_sha256","value" to pin.documentSha256))
            }
            database.insertOrThrow("controller_document",null,contentValuesOf("sequence" to policy.header.sequence,"kind" to policy.header.kind,
                "envelope" to policy.bytes,"sha256" to hash(policy.bytes)))
            val selected = database.rawQuery("SELECT sequence FROM controller_selection WHERE kind=?",arrayOf(policy.header.kind)).use { row ->
                if (row.moveToFirst()) row.getLong(0) else null
            }
            if (selected == null) database.insertOrThrow("controller_selection",null,contentValuesOf("kind" to policy.header.kind,"sequence" to policy.header.sequence))
            else if (selected < policy.header.sequence) database.update("controller_selection",contentValuesOf("sequence" to policy.header.sequence),"kind=?",arrayOf(policy.header.kind))
        }
    }
    private class Policy(val bytes: ByteArray,val header: VerifiedProtocolDocument,val publisher: CatalogPublisherAuthorization?,val sources: CatalogSourceConfiguration?)
    private companion object {
        const val PUBLISHER = "library-publisher-authorization"
        const val SOURCES = "library-source-configuration"
        val KINDS = setOf(PUBLISHER,SOURCES)
        const val MAX_DOCUMENTS = 4096
        fun hash(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it.toInt() and 255) }
        fun state(valid: Boolean,message: String) { if (!valid) throw ProcessorLibraryStateCorrupt(message) }
    }
}

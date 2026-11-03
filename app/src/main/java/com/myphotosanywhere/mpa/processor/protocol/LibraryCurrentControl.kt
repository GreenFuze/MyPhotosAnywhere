package com.myphotosanywhere.mpa.processor.protocol

import com.google.gson.JsonElement
import com.google.gson.JsonParser
import java.security.MessageDigest
import java.util.Base64

data class LibraryPolicyReference(val reference: CatalogObjectReference, val sequence: Long)

/** One root-signed snapshot for an independently pinned mutable Drive file. Decoding
 * does NOT prove freshness: actual exact-file transport, a final version fence and a
 * durable high-water/atomic activation owner remain mandatory. No original-write grant. */
class LibraryCurrentControl private constructor(val connectionSha256: String, val sequence: Long, val envelopeSha256: String,
    val publishers: LibraryPolicyReference, val sources: LibraryPolicyReference, val catalog: CatalogObjectReference?) {
    fun assertSuccessorOf(previous: LibraryCurrentControl) {
        if (connectionSha256 != previous.connectionSha256 || sequence < previous.sequence ||
            sequence == previous.sequence && envelopeSha256 != previous.envelopeSha256 ||
            sequence > previous.sequence && (publishers != previous.publishers && publishers.sequence <= previous.sequence ||
                sources != previous.sources && sources.sequence <= previous.sequence))
            throw ProcessorProtocolException("Current-control history was rewound, forked, retargeted or selected an older policy.")
    }

    fun readPublishers(envelope: ByteArray, approved: LibraryConnectionDescriptor): CatalogPublisherAuthorization {
        val owned = ownEnvelope(envelope)
        assertEnvelope(owned, publishers, approved)
        val result = CatalogPublicationReader(approved.workspace).readPublisherAuthorization(owned, root(approved,"library-publisher-authorization"))
        if (result.sequence != publishers.sequence || result.publisher == null && catalog != null)
            throw ProcessorProtocolException("Current-control publisher sequence or revoked catalog selection is invalid.")
        return result
    }

    fun readSources(envelope: ByteArray, approved: LibraryConnectionDescriptor): CatalogSourceConfiguration {
        val owned = ownEnvelope(envelope)
        assertEnvelope(owned,sources,approved)
        val verified = SignedProtocolDocuments(setOf(CatalogSourceConfiguration.CAPABILITY)).verifyAndRead(owned,root(approved,"library-source-configuration"))
        if (verified.sequence != sources.sequence) throw ProcessorProtocolException("Current-control source sequence changed.")
        return CatalogPublicationReader(approved.workspace).readSourceConfiguration(verified,approved.libraryId)
    }

    private fun assertEnvelope(envelope: ByteArray, selected: LibraryPolicyReference, approved: LibraryConnectionDescriptor) {
        if (approved.documentSha256 != connectionSha256 || envelope.size.toLong() != selected.reference.bytes || hash(envelope) != selected.reference.sha256)
            throw ProcessorProtocolException("Selected policy differs from the pinned control's exact immutable bytes.")
    }

    companion object {
        const val KIND = "library-current-control"
        const val CAPABILITY = "library.current.v1"

        fun read(envelope: ByteArray, approved: LibraryConnectionDescriptor): LibraryCurrentControl {
            val currentFile = approved.currentFileId ?: throw ProcessorProtocolException("Independently approve a current-control file identity first; legacy pins cannot infer one.")
            val owned = ownEnvelope(envelope)
            val verified = SignedProtocolDocuments(setOf(CAPABILITY)).verifyAndRead(owned,root(approved,KIND))
            if (verified.kind != KIND || CAPABILITY !in verified.requiredCapabilities) throw ProcessorProtocolException("A current-control snapshot requires its explicit capability.")
            val body = JsonParser.parseString(verified.canonicalPayload)
            fields(body,"workspace","currentFileId","publishers","sources","catalog")
            val workspace = property(body,"workspace");fields(workspace,"accountId","folderId")
            if (text(workspace,"accountId") != approved.workspace.accountId || text(workspace,"folderId") != approved.workspace.folderId || text(body,"currentFileId") != currentFile)
                throw ProcessorProtocolException("Current control cannot retarget its independently approved workspace or file.")
            val ids = hashSetOf(currentFile)
            fun reference(value: JsonElement): CatalogObjectReference {
                fields(value,"accountId","fileId","workspaceId","bytes","sha256")
                val account = text(value,"accountId");val file = text(value,"fileId");val folder = text(value,"workspaceId")
                CatalogPublicationReader.checkOpaque(file)
                val length = counter(value,"bytes");val sha = text(value,"sha256")
                if (account != approved.workspace.accountId || folder != approved.workspace.folderId || file == "root" || file == folder ||
                    !ids.add(file) || length > ProtocolDocumentReader.MAX_DOCUMENT_BYTES || !sha.matches(Regex("[0-9a-f]{64}")))
                    throw ProcessorProtocolException("Current control requires distinct bounded immutable objects in its pinned workspace.")
                return CatalogObjectReference(account,file,folder,length,sha)
            }
            fun policy(value: JsonElement): LibraryPolicyReference {
                fields(value,"object","sequence");val sequence = counter(value,"sequence")
                if (sequence >= verified.sequence) throw ProcessorProtocolException("A selected policy must precede its current-control signature in the root stream.")
                return LibraryPolicyReference(reference(property(value,"object")),sequence)
            }
            val publishers = policy(property(body,"publishers"));val sources = policy(property(body,"sources"))
            if (publishers.sequence == sources.sequence) throw ProcessorProtocolException("Distinct controller policies cannot reuse a root sequence.")
            val catalog = property(body,"catalog").let { if (it.isJsonNull) null else reference(it) }
            return LibraryCurrentControl(approved.documentSha256,verified.sequence,hash(owned),publishers,sources,catalog)
        }

        private fun ownEnvelope(envelope: ByteArray): ByteArray {
            if (envelope.size !in 1..ProtocolDocumentReader.MAX_DOCUMENT_BYTES) throw ProcessorProtocolException("Current-control envelope exceeds its wire bound.")
            return envelope.copyOf()
        }
        private fun root(approved: LibraryConnectionDescriptor, kind: String) = TrustedProtocolActor(approved.libraryId,approved.controllerId,
            approved.controllerEpoch,Base64.getDecoder().decode(approved.controllerPublicKeySpki),setOf(kind))
        private fun hash(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it.toInt() and 255) }
        private fun fields(value: JsonElement, vararg names: String) {
            if (!value.isJsonObject || value.asJsonObject.size() != names.size || names.any { !value.asJsonObject.has(it) })
                throw ProcessorProtocolException("Unexpected current-control fields.")
        }
        private fun property(value: JsonElement,name: String): JsonElement = value.asJsonObject[name] ?: throw ProcessorProtocolException("Missing current-control field.")
        private fun text(value: JsonElement,name: String): String {
            val item = property(value,name)
            if (!item.isJsonPrimitive || !item.asJsonPrimitive.isString) throw ProcessorProtocolException("Missing current-control text field.")
            return item.asString
        }
        private fun counter(value: JsonElement,name: String): Long {
            val text = text(value,name);val result = text.toLongOrNull()
            if (text.length !in 1..19 || text.startsWith('0') || !text.all { it in '0'..'9' } || result == null || result < 1)
                throw ProcessorProtocolException("A canonical positive decimal counter is required.")
            return result
        }
    }
}

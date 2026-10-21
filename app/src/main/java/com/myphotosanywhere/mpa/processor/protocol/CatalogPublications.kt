package com.myphotosanywhere.mpa.processor.protocol

import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import java.util.UUID

/** Selected locally/enrolled; a received reference cannot select its own workspace. */
data class CatalogWorkspace(val accountId: String, val folderId: String) {
    init { CatalogPublicationReader.checkOpaque(accountId); CatalogPublicationReader.checkOpaque(folderId) }
}
data class CatalogObjectReference(val accountId: String, val fileId: String, val workspaceId: String, val bytes: Long, val sha256: String)
data class CatalogChunkReference(val encoded: CatalogObjectReference, val expandedBytes: Long, val expandedSha256: String)
data class CatalogCommit(val generationId: UUID, val parent: CatalogObjectReference?, val rootIndex: CatalogObjectReference,
    val sourceCoverage: CatalogObjectReference, val settingsRevision: Long, val minimumHistoryGeneration: Long)
data class CatalogIndexChild(val edge: String, val reference: CatalogObjectReference, val chunk: CatalogChunkReference?)
data class CatalogIndexNode(val prefix: String, val children: List<CatalogIndexChild>)

/** Schemas only: verify signature/approved actor first. Drive membership, reference hashes,
 * ancestry and complete-tree validation remain mandatory before activating any generation. */
class CatalogPublicationReader(private val workspace: CatalogWorkspace) {
    fun readCommit(verified: VerifiedProtocolDocument): CatalogCommit {
        if (verified.kind != "catalog-commit" || CAPABILITY !in verified.requiredCapabilities)
            throw ProcessorProtocolException("A verified catalog commit with its required capability is required.")
        val root = canonical(verified.canonicalPayload.toByteArray(Charsets.UTF_8))
        val generation = uuid(root, "generationId")
        val parentValue = property(root, "parent")
        val parent = if (parentValue.isJsonNull) null else reference(parentValue, MAX_INDEX_BYTES)
        if ((verified.sequence == 1L) != (parent == null)) throw ProcessorProtocolException("Only the first stream commit may omit its predecessor.")
        val index = reference(property(root, "rootIndex"), MAX_INDEX_BYTES)
        val coverage = reference(property(root, "sourceCoverage"), MAX_INDEX_BYTES)
        val settings = decimal(root, "settingsRevision", allowZero = true)
        val minimum = decimal(root, "minimumHistoryGeneration")
        if (minimum > verified.sequence) throw ProcessorProtocolException("Required history exceeds the current stream sequence.")
        val ids = hashSetOf(index.fileId, coverage.fileId)
        if (ids.size != 2 || parent != null && !ids.add(parent.fileId)) throw ProcessorProtocolException("Distinct catalog roles cannot alias one Drive object.")
        return CatalogCommit(generation, parent, index, coverage, settings, minimum)
    }

    fun readIndex(canonicalBytes: ByteArray, expectedPrefix: String): CatalogIndexNode {
        if (expectedPrefix.length > 6 || expectedPrefix.length % 2 != 0 || !expectedPrefix.all(::isHex))
            throw ProcessorProtocolException("Invalid catalog index prefix.")
        val root = canonical(canonicalBytes)
        val majorValue = property(root, "major")
        if (!majorValue.isJsonPrimitive || !majorValue.asJsonPrimitive.isNumber)
            throw ProcessorProtocolException("Catalog major must be an integer.")
        val major = majorValue.asString.toIntOrNull() ?: throw ProcessorProtocolException("Catalog major must be an integer.")
        if (text(root, "format") != "mpa-catalog-index" || major != 1)
            throw ProtocolCompatibilityException("This catalog index requires a compatible MPA update.")
        if (text(root, "prefix") != expectedPrefix) throw ProcessorProtocolException("Catalog identity partition does not match its parent.")
        val children = property(root, "children")
        if (!children.isJsonArray || children.asJsonArray.size() > 256 || children.asJsonArray.size() == 0 && expectedPrefix.isNotEmpty())
            throw ProcessorProtocolException("Invalid bounded catalog index children.")
        val entries = ArrayList<CatalogIndexChild>(); val ids = HashSet<String>(); var previous: String? = null
        children.asJsonArray.forEach { child ->
            val edge = text(child, "edge")
            if (edge.length != 2 || !edge.all(::isHex) || previous?.let { it >= edge } == true)
                throw ProcessorProtocolException("Catalog edges must be unique sorted hexadecimal bytes.")
            previous = edge
            val kind = text(child, "kind")
            if (kind !in setOf("index", "chunk") || kind == "index" && expectedPrefix.length >= 6)
                throw ProcessorProtocolException("Catalog index exceeds its four partition levels.")
            val ref = reference(property(child, "object"), if (kind == "chunk") MAX_COMPRESSED_BYTES else MAX_INDEX_BYTES)
            if (!ids.add(ref.fileId)) throw ProcessorProtocolException("Catalog children cannot share one Drive object.")
            val expanded = property(child, "expanded")
            val chunk = if (kind == "chunk") {
                val bytes = decimal(expanded, "bytes"); val hash = digest(expanded, "sha256")
                if (bytes > MAX_EXPANDED_BYTES) throw ProcessorProtocolException("Expanded catalog chunk exceeds its bound.")
                CatalogChunkReference(ref, bytes, hash)
            } else {
                if (!expanded.isJsonNull) throw ProcessorProtocolException("Only chunks declare expanded bytes.")
                null
            }
            entries.add(CatalogIndexChild(edge, ref, chunk))
        }
        return CatalogIndexNode(expectedPrefix, entries.toList())
    }

    private fun reference(value: JsonElement, maximum: Int): CatalogObjectReference {
        val account = text(value, "accountId"); val file = text(value, "fileId"); val folder = text(value, "workspaceId")
        checkOpaque(account); checkOpaque(file); checkOpaque(folder)
        if (account != workspace.accountId || folder != workspace.folderId || file == folder)
            throw ProcessorProtocolException("Catalog reference is outside the selected metadata workspace.")
        val bytes = decimal(value, "bytes")
        if (bytes > maximum) throw ProcessorProtocolException("Catalog object exceeds its role's byte bound.")
        return CatalogObjectReference(account, file, folder, bytes, digest(value, "sha256"))
    }
    private fun canonical(bytes: ByteArray): JsonObject {
        val encoded = CanonicalProtocolJson(MAX_INDEX_BYTES).encode(bytes)
        if (!bytes.contentEquals(encoded)) throw ProcessorProtocolException("Catalog documents must already use canonical JSON bytes.")
        val element = JsonParser.parseString(encoded.toString(Charsets.UTF_8))
        if (!element.isJsonObject) throw ProcessorProtocolException("Catalog document must be an object.")
        return element.asJsonObject
    }
    private fun property(root: JsonElement, name: String): JsonElement =
        if (root.isJsonObject) root.asJsonObject[name] ?: throw ProcessorProtocolException("Missing catalog field: $name")
        else throw ProcessorProtocolException("Catalog field owner must be an object.")
    private fun text(root: JsonElement, name: String): String {
        val value = property(root, name)
        if (!value.isJsonPrimitive || !value.asJsonPrimitive.isString) throw ProcessorProtocolException("Catalog field must be a string: $name")
        return value.asString
    }
    private fun decimal(root: JsonElement, name: String, allowZero: Boolean = false): Long {
        val value = text(root, name)
        if (value.length !in 1..19 || value.length > 1 && value[0] == '0' || !value.all { it in '0'..'9' })
            throw ProcessorProtocolException("Invalid canonical catalog counter: $name")
        val number = value.toLongOrNull() ?: throw ProcessorProtocolException("Catalog counter overflow: $name")
        if (number < (if (allowZero) 0 else 1)) throw ProcessorProtocolException("Catalog counter is outside its range: $name")
        return number
    }
    private fun digest(root: JsonElement, name: String): String = text(root, name).also {
        if (it.length != 64 || !it.all(::isHex)) throw ProcessorProtocolException("Catalog digest must be lowercase SHA-256.")
    }
    private fun uuid(root: JsonElement, name: String): UUID {
        val value = text(root, name)
        if (!UUID_PATTERN.matches(value)) throw ProcessorProtocolException("Invalid catalog generation identity.")
        return UUID.fromString(value).also { if (it == UUID(0, 0)) throw ProcessorProtocolException("Zero generation identity is invalid.") }
    }
    companion object {
        const val CAPABILITY = "catalog.generations.v1"
        const val MAX_INDEX_BYTES = 256 * 1024
        const val MAX_COMPRESSED_BYTES = 4 * 1024 * 1024
        const val MAX_EXPANDED_BYTES = 16 * 1024 * 1024
        private val UUID_PATTERN = Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
        internal fun checkOpaque(value: String) {
            if (value.length !in 1..256 || !value.all { it in 'a'..'z' || it in 'A'..'Z' || it in '0'..'9' || it == '-' || it == '_' })
                throw ProcessorProtocolException("Catalog references require opaque Drive identities, never paths or URLs.")
        }
        private fun isHex(value: Char) = value in '0'..'9' || value in 'a'..'f'
    }
}

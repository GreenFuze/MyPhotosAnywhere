package com.myphotosanywhere.mpa.processor.protocol

import com.google.gson.JsonElement
import com.google.gson.JsonParser
import java.util.UUID

data class DriveCatalogSource(val id: UUID, val revision: Long, val depth: Int)
data class DriveCatalogAsset(val id: UUID, val accountId: String, val fileId: String, val driveVersion: Long, val name: String,
    val mimeType: String, val width: Int?, val height: Int?, val capturedUtc: Long?, val rawCapture: String?, val modifiedUtc: Long?,
    val sources: List<DriveCatalogSource>) {
    val effectiveUtc: Long? get() = capturedUtc ?: modifiedUtc
}
data class DriveCatalogChunk(val prefix: String, val assets: List<DriveCatalogAsset>)

/** Observed metadata, never a verified original. Authority, source coverage and complete
 * generation staging must be validated separately before any live catalog is activated. */
class DriveCatalogFacts(private val library: UUID) {
    private val identity = DriveAssetIdentity(library)
    fun read(canonicalBytes: ByteArray, expectedPrefix: String): DriveCatalogChunk {
        if (expectedPrefix.length !in 2..8 || expectedPrefix.length % 2 != 0 || !expectedPrefix.all { it in '0'..'9' || it in 'a'..'f' })
            throw ProcessorProtocolException("Drive facts require a complete two-digit identity partition path.")
        val canonical = CanonicalProtocolJson().encode(canonicalBytes)
        if (!canonicalBytes.contentEquals(canonical)) throw ProcessorProtocolException("Drive facts must already be canonical JSON.")
        val root = JsonParser.parseString(canonical.toString(Charsets.UTF_8))
        if (text(root, "format") != "mpa-drive-facts" || integer(root, "major") != 1)
            throw ProtocolCompatibilityException("These Drive facts require a compatible MPA update.")
        if (uuid(root, "libraryId") != library || text(root, "prefix") != expectedPrefix)
            throw ProcessorProtocolException("Drive facts do not match their captured library/identity partition.")
        val rows = property(root, "assets")
        if (!rows.isJsonArray || rows.asJsonArray.size() !in 1..MAX_ASSETS)
            throw ProcessorProtocolException("Drive facts require a bounded nonempty identity partition.")
        val assets = ArrayList<DriveCatalogAsset>(); var previous: String? = null
        rows.asJsonArray.forEach { row ->
            val id = uuid(row, "id"); val sources = property(row, "sources")
            if (!sources.isJsonArray || sources.asJsonArray.size() !in 1..MAX_SOURCES)
                throw ProcessorProtocolException("Drive facts require bounded captured source memberships.")
            val memberships = ArrayList<DriveCatalogSource>(); var priorSource: String? = null
            sources.asJsonArray.forEach { source ->
                val sourceId = uuid(source, "id"); val key = sourceId.toString()
                if (priorSource?.let { it >= key } == true) throw ProcessorProtocolException("Source memberships must be unique and ordinally sorted.")
                priorSource = key
                memberships.add(DriveCatalogSource(sourceId, positive(source, "revision"), integer(source, "depth")))
            }
            val asset = DriveCatalogAsset(id, text(row, "accountId"), text(row, "fileId"), positive(row, "driveVersion"), text(row, "name"), text(row, "mimeType"),
                optionalInteger(row, "width"), optionalInteger(row, "height"), timestamp(row, "capturedUtc"), optionalText(row, "rawCapture"), timestamp(row, "modifiedUtc"), memberships.toList())
            CatalogPublicationReader.checkOpaque(asset.accountId); CatalogPublicationReader.checkOpaque(asset.fileId)
            if (identity.derive(asset.accountId, asset.fileId) != id || !id.toString().replace("-", "").startsWith(expectedPrefix) ||
                asset.name.isBlank() || asset.name.length > 4096 || asset.name.any(Char::isISOControl) ||
                asset.mimeType.length > 256 || asset.mimeType.any(Char::isISOControl) || !(asset.mimeType.startsWith("image/") || asset.mimeType.startsWith("video/")) ||
                asset.width?.let { it !in 1..1_000_000 } == true || asset.height?.let { it !in 1..1_000_000 } == true ||
                asset.rawCapture?.let { it.length > 128 } == true || memberships.any { it.depth !in 0..256 })
                throw ProcessorProtocolException("Invalid observed Drive facts or captured source membership.")
            val key = id.toString()
            if (previous?.let { it >= key } == true) throw ProcessorProtocolException("Partition asset identities must be unique and ordinally sorted.")
            previous = key; assets.add(asset)
        }
        return DriveCatalogChunk(expectedPrefix, assets.toList())
    }
    private fun property(root: JsonElement, name: String): JsonElement =
        if (root.isJsonObject) root.asJsonObject[name] ?: throw ProcessorProtocolException("Missing Drive facts field: $name")
        else throw ProcessorProtocolException("Drive facts field owner must be an object.")
    private fun text(root: JsonElement, name: String): String {
        val value = property(root, name)
        if (!value.isJsonPrimitive || !value.asJsonPrimitive.isString) throw ProcessorProtocolException("Drive facts field must be a string: $name")
        return value.asString
    }
    private fun optionalText(root: JsonElement, name: String) = if (property(root, name).isJsonNull) null else text(root, name)
    private fun integer(root: JsonElement, name: String): Int {
        val value = property(root, name)
        if (!value.isJsonPrimitive || !value.asJsonPrimitive.isNumber) throw ProcessorProtocolException("Drive facts field must be a nonnegative int32: $name")
        return value.asString.toIntOrNull()?.takeIf { it >= 0 } ?: throw ProcessorProtocolException("Drive facts field must be a nonnegative int32: $name")
    }
    private fun optionalInteger(root: JsonElement, name: String) = if (property(root, name).isJsonNull) null else integer(root, name)
    private fun positive(root: JsonElement, name: String): Long {
        val text = text(root, name); val number = text.toLongOrNull()
        if (number == null || number < 1 || number.toString() != text) throw ProcessorProtocolException("Drive facts counter must be a positive canonical int64 string: $name")
        return number
    }
    private fun timestamp(root: JsonElement, name: String): Long? {
        if (property(root, name).isJsonNull) return null
        val text = text(root, name); val number = text.toLongOrNull()
        if (number == null || number !in -62135596800000L..253402300799999L || number.toString() != text)
            throw ProcessorProtocolException("Drive facts timestamp must be a bounded canonical millisecond string: $name")
        return number
    }
    private fun uuid(root: JsonElement, name: String): UUID {
        val text = text(root, name)
        if (!UUID_PATTERN.matches(text)) throw ProcessorProtocolException("Invalid canonical Drive facts identity: $name")
        return UUID.fromString(text).also { if (it == UUID(0, 0)) throw ProcessorProtocolException("Zero Drive facts identity is invalid.") }
    }
    companion object {
        const val CAPABILITY = "catalog.drive-facts.v1"
        const val MAX_ASSETS = 1000
        const val MAX_SOURCES = 256
        private val UUID_PATTERN = Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
    }
}

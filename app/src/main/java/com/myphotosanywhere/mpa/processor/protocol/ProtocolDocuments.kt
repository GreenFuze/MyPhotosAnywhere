package com.myphotosanywhere.mpa.processor.protocol

import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import java.util.UUID

class ProtocolCompatibilityException(message: String) : IllegalStateException(message)

/** Construct from approved pairing state, never from a received document's self-asserted identity. */
class TrustedProtocolActor(
    val libraryId: UUID,
    val actorId: UUID,
    val epoch: Long,
    publicKeyInfo: ByteArray,
    allowedKinds: Set<String>,
) {
    internal val verifier = ProtocolDocumentVerifier(publicKeyInfo, ProtocolDocumentReader.MAX_DOCUMENT_BYTES)
    private val kinds = allowedKinds.toSet()

    init {
        require(libraryId != UUID(0, 0) && actorId != UUID(0, 0) && epoch > 0)
        require(kinds.size in 1..32 && kinds.all(ProtocolDocumentReader::isIdentifier))
    }

    internal fun canPublish(kind: String) = kind in kinds
}

data class VerifiedProtocolDocument(
    val libraryId: UUID,
    val actorId: UUID,
    val epoch: Long,
    val sequence: Long,
    val kind: String,
    val minor: Int,
    val requiredCapabilities: List<String>,
    val canonicalPayload: String,
)

class ProtocolDocumentReader(supportedCapabilities: Set<String>) {
    private val capabilities = supportedCapabilities.toSet()
    private val codec = CanonicalProtocolJson(MAX_DOCUMENT_BYTES)

    init { require(capabilities.size <= 64 && capabilities.all(::isIdentifier)) }

    fun verifyAndRead(body: ByteArray, signature: DocumentSignature, authority: TrustedProtocolActor): VerifiedProtocolDocument {
        if (!authority.verifier.verify(body, signature)) throw ProcessorProtocolException("Document signature does not match the approved actor.")
        val element = JsonParser.parseString(codec.encode(body).toString(Charsets.UTF_8))
        if (!element.isJsonObject) throw ProcessorProtocolException("Protocol document must be an object.")
        val root = element.asJsonObject
        if (string(root, "format") != "mpa") throw ProcessorProtocolException("Invalid protocol document format.")
        val library = uuid(root, "libraryId")
        val actor = uuid(root, "actorId")
        val epoch = decimal(root, "epoch")
        val sequence = decimal(root, "sequence")
        val kind = string(root, "kind")
        if (library != authority.libraryId || actor != authority.actorId || epoch != authority.epoch || !authority.canPublish(kind))
            throw ProcessorProtocolException("Document is outside the approved actor/library/epoch/kind scope.")
        val major = integer(root, "major")
        val minor = integer(root, "minor")
        if (major != 1) throw ProtocolCompatibilityException("This MPA library protocol requires a compatible app/Processor update.")
        val required = property(root, "requiredCapabilities")
        if (!required.isJsonArray || required.asJsonArray.size() > 64) throw ProcessorProtocolException("Invalid required capability list.")
        val names = ArrayList<String>()
        required.asJsonArray.forEach { item ->
            if (!item.isJsonPrimitive || !item.asJsonPrimitive.isString) throw ProcessorProtocolException("Capability must be a string.")
            val name = item.asString
            if (!isIdentifier(name) || names.lastOrNull()?.let { it >= name } == true)
                throw ProcessorProtocolException("Capabilities must be unique and ordinally sorted.")
            names.add(name)
        }
        if (names.any { it !in capabilities }) throw ProtocolCompatibilityException("Unsupported required capability. Keep the last verified library view.")
        val payload = property(root, "payload")
        if (!payload.isJsonObject) throw ProcessorProtocolException("Document payload must be an object.")
        return VerifiedProtocolDocument(library, actor, epoch, sequence, kind, minor, names.toList(),
            codec.encode(payload.toString().toByteArray(Charsets.UTF_8)).toString(Charsets.UTF_8))
    }

    private fun property(root: JsonObject, name: String): JsonElement = root[name]
        ?: throw ProcessorProtocolException("Missing required protocol field: $name")

    private fun string(root: JsonObject, name: String): String {
        val value = property(root, name)
        if (!value.isJsonPrimitive || !value.asJsonPrimitive.isString) throw ProcessorProtocolException("Expected string: $name")
        return value.asString
    }

    private fun uuid(root: JsonObject, name: String): UUID {
        val value = string(root, name)
        if (!value.matches(Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")))
            throw ProcessorProtocolException("Expected canonical UUID: $name")
        val id = UUID.fromString(value)
        if (id == UUID(0, 0)) throw ProcessorProtocolException("Zero UUID is not permitted: $name")
        return id
    }

    private fun decimal(root: JsonObject, name: String): Long {
        val value = string(root, name)
        if (!value.matches(Regex("[1-9][0-9]{0,18}"))) throw ProcessorProtocolException("Expected positive canonical int64 string: $name")
        return value.toLongOrNull() ?: throw ProcessorProtocolException("Counter overflow: $name")
    }

    private fun integer(root: JsonObject, name: String): Int {
        val value = property(root, name)
        if (!value.isJsonPrimitive || !value.asJsonPrimitive.isNumber) throw ProcessorProtocolException("Expected nonnegative int32: $name")
        return value.asString.toIntOrNull()?.takeIf { it >= 0 } ?: throw ProcessorProtocolException("Expected nonnegative int32: $name")
    }

    companion object {
        const val MAX_DOCUMENT_BYTES = 256 * 1024
        internal fun isIdentifier(value: String) = value.matches(Regex("[a-z0-9.-]{1,64}"))
    }
}

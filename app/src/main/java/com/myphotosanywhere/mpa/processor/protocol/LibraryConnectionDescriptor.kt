package com.myphotosanywhere.mpa.processor.protocol

import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import java.security.MessageDigest
import java.util.Base64
import java.util.UUID

/** Public connection information, NOT enrollment or authority. Local setup must obtain
 * independent human approval, bind the actual Google account and persist its pin first.
 * No endpoint, private key, Google credential or original-write grant is carried here. */
class LibraryConnectionDescriptor(
    val libraryId: UUID,
    val controllerId: UUID,
    val controllerEpoch: Long,
    val workspace: CatalogWorkspace,
    controllerPublicKey: ByteArray,
) {
    val controllerPublicKeySpki: String
    val controllerKeyId: String
    val documentSha256: String
    private val document: ByteArray

    init {
        if (libraryId == UUID(0, 0) || controllerId == UUID(0, 0) || controllerEpoch < 1 || workspace.folderId == "root")
            throw ProcessorProtocolException("A concrete library, controller epoch and metadata workspace are required.")
        if (controllerPublicKey.isEmpty() || controllerPublicKey.size > 512) throw ProcessorProtocolException("Invalid controller public-key bound.")
        val owned = controllerPublicKey.copyOf()
        val verifier = ProtocolDocumentVerifier(owned)
        controllerPublicKeySpki = Base64.getEncoder().encodeToString(owned)
        controllerKeyId = verifier.keyId
        val root = JsonObject().apply {
            addProperty("format", "mpa-library-connection"); addProperty("major", 1); addProperty("libraryId", libraryId.toString())
            add("controller", JsonObject().apply {
                addProperty("actorId", controllerId.toString()); addProperty("epoch", controllerEpoch.toString())
                addProperty("publicKeySpki", controllerPublicKeySpki); addProperty("keyId", controllerKeyId)
            })
            add("workspace", JsonObject().apply { addProperty("accountId", workspace.accountId); addProperty("folderId", workspace.folderId) })
        }
        document = CanonicalProtocolJson(MAX_DOCUMENT_BYTES).encode(root.toString().toByteArray(Charsets.UTF_8))
        documentSha256 = MessageDigest.getInstance("SHA-256").digest(document).joinToString("") { "%02x".format(it.toInt() and 255) }
    }

    fun encode(): String = PREFIX + Base64.getUrlEncoder().withoutPadding().encodeToString(document)

    companion object {
        const val PREFIX = "mpa-library-v1:"
        const val MAX_DOCUMENT_BYTES = 2048
        const val MAX_CODE_CHARACTERS = 4096
        private val UUID_PATTERN = Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")

        fun decode(code: String): LibraryConnectionDescriptor {
            if (code.length > MAX_CODE_CHARACTERS || !code.startsWith(PREFIX))
                throw ProcessorProtocolException("Invalid or unsupported MPA library connection code.")
            val encoded = code.substring(PREFIX.length)
            if (encoded.isEmpty() || encoded.length % 4 == 1 || !encoded.all { it in 'a'..'z' || it in 'A'..'Z' || it in '0'..'9' || it == '-' || it == '_' })
                throw ProcessorProtocolException("Library connection code requires canonical unpadded base64url.")
            val bytes = try { Base64.getUrlDecoder().decode(encoded) }
                catch (error: IllegalArgumentException) { throw ProcessorProtocolException("Invalid library connection encoding.", error) }
            if (bytes.isEmpty() || bytes.size > MAX_DOCUMENT_BYTES) throw ProcessorProtocolException("Library connection exceeds its byte bound.")
            val canonical = CanonicalProtocolJson(MAX_DOCUMENT_BYTES).encode(bytes)
            if (!bytes.contentEquals(canonical)) throw ProcessorProtocolException("Library connection JSON must already be canonical.")
            val root = JsonParser.parseString(canonical.toString(Charsets.UTF_8))
            objectSize(root, 5)
            val major = property(root, "major")
            if (text(root, "format") != "mpa-library-connection" || !major.isJsonPrimitive || !major.asJsonPrimitive.isNumber || major.asString != "1")
                throw ProcessorProtocolException("Unsupported library connection format.")
            val controller = property(root, "controller"); objectSize(controller, 4)
            val workspace = property(root, "workspace"); objectSize(workspace, 2)
            val epochText = text(controller, "epoch")
            val epoch = epochText.toLongOrNull()
            if (epochText.length !in 1..19 || epochText.startsWith('0') || !epochText.all { it in '0'..'9' } || epoch == null || epoch < 1)
                throw ProcessorProtocolException("Invalid canonical controller epoch.")
            val spki = text(controller, "publicKeySpki")
            if (spki.length !in 1..684) throw ProcessorProtocolException("Invalid controller public-key bound.")
            val key = try { Base64.getDecoder().decode(spki) }
                catch (error: IllegalArgumentException) { throw ProcessorProtocolException("Invalid controller public-key encoding.", error) }
            if (Base64.getEncoder().encodeToString(key) != spki) throw ProcessorProtocolException("Non-canonical controller public key.")
            val result = LibraryConnectionDescriptor(uuid(root, "libraryId"), uuid(controller, "actorId"), epoch,
                CatalogWorkspace(text(workspace, "accountId"), text(workspace, "folderId")), key)
            if (text(controller, "keyId") != result.controllerKeyId || result.encode() != code)
                throw ProcessorProtocolException("Connection key identity, fields or wire encoding changed.")
            return result
        }

        private fun objectSize(root: JsonElement, count: Int) {
            if (!root.isJsonObject || root.asJsonObject.size() != count) throw ProcessorProtocolException("Unexpected library connection fields.")
        }
        private fun property(root: JsonElement, name: String): JsonElement =
            root.asJsonObject[name] ?: throw ProcessorProtocolException("Missing library connection field: $name")
        private fun text(root: JsonElement, name: String): String {
            val value = property(root, name)
            if (!value.isJsonPrimitive || !value.asJsonPrimitive.isString) throw ProcessorProtocolException("Connection field must be text: $name")
            return value.asString
        }
        private fun uuid(root: JsonElement, name: String): UUID {
            val value = text(root, name)
            if (!UUID_PATTERN.matches(value)) throw ProcessorProtocolException("Invalid canonical library connection identity.")
            return UUID.fromString(value).also { if (it == UUID(0, 0)) throw ProcessorProtocolException("Zero connection identity is invalid.") }
        }
    }
}

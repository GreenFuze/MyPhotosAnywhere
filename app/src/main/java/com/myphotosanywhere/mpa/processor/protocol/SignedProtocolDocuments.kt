package com.myphotosanywhere.mpa.processor.protocol

import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParser

/** Bounded immutable Drive file; a received key or discovery hint never grants authority. */
class SignedProtocolDocuments(supportedCapabilities: Set<String>) {
    private val codec = CanonicalProtocolJson(ProtocolDocumentReader.MAX_DOCUMENT_BYTES)
    private val reader = ProtocolDocumentReader(supportedCapabilities)

    fun encode(canonicalDocument: ByteArray, signature: DocumentSignature): ByteArray {
        validateSignature(signature)
        val body = codec.encode(canonicalDocument)
        if (!canonicalDocument.contentEquals(body)) throw ProcessorProtocolException("Signed document input must already be canonical JSON.")
        val document = JsonParser.parseString(body.toString(Charsets.UTF_8))
        if (!document.isJsonObject) throw ProcessorProtocolException("Signed document must be an object.")
        // The body is canonical and the only interpolated values are validated ASCII
        // signature fields. Do not expand Unicode through another JSON serializer.
        val prefix = "{\"document\":".toByteArray(Charsets.UTF_8)
        val tail = (",\"format\":\"mpa-signed-document\",\"major\":1,\"signature\":{\"algorithm\":\"ES256\",\"keyId\":\"" +
            signature.keyId + "\",\"value\":\"" + signature.value + "\"}}").toByteArray(Charsets.UTF_8)
        if (body.size > ProtocolDocumentReader.MAX_DOCUMENT_BYTES - prefix.size - tail.size)
            throw ProcessorProtocolException("Signed envelope exceeds its total wire byte bound.")
        val envelope = ByteArray(prefix.size + body.size + tail.size).also { envelope ->
            prefix.copyInto(envelope); body.copyInto(envelope, prefix.size); tail.copyInto(envelope, prefix.size + body.size)
        }
        return codec.encode(envelope) // Includes total nesting/structure validation.
    }

    fun verifyAndRead(canonicalEnvelope: ByteArray, authority: TrustedProtocolActor): VerifiedProtocolDocument {
        val bytes = codec.encode(canonicalEnvelope)
        if (!canonicalEnvelope.contentEquals(bytes)) throw ProcessorProtocolException("Signed envelope must already be canonical JSON.")
        val root = fields(JsonParser.parseString(bytes.toString(Charsets.UTF_8)), setOf("format", "major", "document", "signature"))
        if (text(root, "format") != "mpa-signed-document") throw ProcessorProtocolException("Unsupported signed document format.")
        val major = root["major"]
        if (!major.isJsonPrimitive || !major.asJsonPrimitive.isNumber) throw ProcessorProtocolException("Signed document major must be an integer.")
        val version = major.asString.toIntOrNull() ?: throw ProcessorProtocolException("Signed document major must be an integer.")
        if (version != 1) throw ProtocolCompatibilityException("This signed document requires a compatible MPA update.")
        val body = root["document"]
        if (!body.isJsonObject) throw ProcessorProtocolException("Signed document must be an object.")
        val signature = fields(root["signature"], setOf("algorithm", "keyId", "value"))
        val captured = DocumentSignature(text(signature, "algorithm"), text(signature, "keyId"), text(signature, "value"))
        validateSignature(captured)
        return reader.verifyAndRead(codec.encodeVerifiedSubtree(body), captured, authority)
    }

    private fun validateSignature(signature: DocumentSignature) {
        if (signature.algorithm != "ES256" || !signature.keyId.matches(Regex("[0-9a-f]{64}")))
            throw ProcessorProtocolException("Unsupported signature/key encoding.")
        SignatureEncoding.decode(signature.value)
    }
    private fun fields(value: JsonElement, names: Set<String>): JsonObject {
        if (!value.isJsonObject || value.asJsonObject.keySet() != names) throw ProcessorProtocolException("Unexpected signed document fields.")
        return value.asJsonObject
    }
    private fun text(value: JsonObject, name: String): String {
        val field = value[name]
        if (field == null || !field.isJsonPrimitive || !field.asJsonPrimitive.isString) throw ProcessorProtocolException("Missing signed document text field.")
        return field.asString
    }
}

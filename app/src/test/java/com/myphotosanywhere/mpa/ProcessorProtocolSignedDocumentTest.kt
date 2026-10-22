package com.myphotosanywhere.mpa

import com.google.gson.JsonArray
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
import com.myphotosanywhere.mpa.processor.protocol.DocumentSignature
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolCompatibilityException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentReader
import com.myphotosanywhere.mpa.processor.protocol.SignedProtocolDocuments
import com.myphotosanywhere.mpa.processor.protocol.TrustedProtocolActor
import java.util.Base64
import java.util.UUID
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class ProcessorProtocolSignedDocumentTest {
    private val library = UUID.fromString("94a81164-e980-4ec1-a566-cec36475b839")
    private val actor = UUID.fromString("6c803c09-3b11-49cb-a3dc-1d42c3dc6f30")
    private fun fixture() = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/signed-document.json"))
        .bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
    private fun authority(root: JsonObject, libraryId: UUID = library, actorId: UUID = actor, epoch: Long = 1, kinds: Set<String> = setOf("user-operation")) =
        TrustedProtocolActor(libraryId, actorId, epoch, Base64.getDecoder().decode(root["publicKeySpki"].asString), kinds)
    private fun signature(root: JsonObject): DocumentSignature {
        val signature = root["signature"].asJsonObject
        return DocumentSignature(signature["algorithm"].asString, signature["keyId"].asString, signature["value"].asString)
    }

    @Test fun bothConsumersEncodeIdenticalIndependentSignedFileAndVerifyAnEnrolledActor() {
        val root = fixture(); val codec = SignedProtocolDocuments(emptySet())
        val expected = root["envelope"].asString.toByteArray(Charsets.UTF_8)
        assertArrayEquals(expected, codec.encode(root["body"].asString.toByteArray(Charsets.UTF_8), signature(root)))
        val verified = codec.verifyAndRead(expected, authority(root))
        assertEquals(library, verified.libraryId); assertEquals(actor, verified.actorId); assertEquals(1L, verified.sequence)
        assertEquals("שלום 📷", JsonParser.parseString(verified.canonicalPayload).asJsonObject["caption"].asString)
    }

    @Test fun bothConsumersRejectEveryMalformedUntrustedAndAlteredEnvelope() {
        val root = fixture(); val codec = SignedProtocolDocuments(emptySet()); val authority = authority(root)
        val cases = root["invalid"].asJsonArray; assertEquals(15, cases.size())
        cases.forEach { row ->
            val item = row.asJsonObject; val bytes = item["json"].asString.toByteArray(Charsets.UTF_8)
            if (item["compatibility"].asBoolean) assertThrows(item["name"].asString, ProtocolCompatibilityException::class.java) { codec.verifyAndRead(bytes, authority) }
            else assertThrows(item["name"].asString, ProcessorProtocolException::class.java) { codec.verifyAndRead(bytes, authority) }
        }
    }

    @Test fun validSignatureCannotAuthorizeAnotherLibraryActorEpochOrDocumentKind() {
        val root = fixture(); val codec = SignedProtocolDocuments(emptySet()); val bytes = root["envelope"].asString.toByteArray(Charsets.UTF_8)
        val denied = listOf(authority(root, libraryId = UUID.randomUUID()), authority(root, actorId = UUID.randomUUID()), authority(root, epoch = 2), authority(root, kinds = setOf("catalog-commit")))
        denied.forEach { assertThrows(ProcessorProtocolException::class.java) { codec.verifyAndRead(bytes, it) } }
    }

    @Test fun totalFileBoundIncludesSignatureAndPackagingDoesNotAuthorizeABody() {
        val root = fixture(); val codec = SignedProtocolDocuments(emptySet()); val authority = authority(root); val signature = signature(root)
        val body = JsonParser.parseString(root["body"].asString).asJsonObject
        val basic = CanonicalProtocolJson().encode(body.toString().toByteArray(Charsets.UTF_8))
        val partLength = (ProtocolDocumentReader.MAX_DOCUMENT_BYTES - basic.size - 160) / 8
        body.add("padding", JsonArray().apply { repeat(8) { add("x".repeat(partLength)) } })
        val nearLimit = CanonicalProtocolJson().encode(body.toString().toByteArray(Charsets.UTF_8))
        assertTrue(nearLimit.size < ProtocolDocumentReader.MAX_DOCUMENT_BYTES)
        assertThrows(ProcessorProtocolException::class.java) { codec.encode(nearLimit, signature) }
        assertThrows(ProcessorProtocolException::class.java) { codec.verifyAndRead(ByteArray(ProtocolDocumentReader.MAX_DOCUMENT_BYTES + 1), authority) }
        assertThrows(ProcessorProtocolException::class.java) { codec.encode(" {\"unapproved\":true}".toByteArray(Charsets.UTF_8), signature) }
        val packed = codec.encode("{\"unapproved\":true}".toByteArray(Charsets.UTF_8), signature)
        assertThrows(ProcessorProtocolException::class.java) { codec.verifyAndRead(packed, authority) }
    }
}

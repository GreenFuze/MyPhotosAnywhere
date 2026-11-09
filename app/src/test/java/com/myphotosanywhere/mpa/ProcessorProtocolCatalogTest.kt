package com.myphotosanywhere.mpa

import com.google.gson.JsonElement
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
import com.myphotosanywhere.mpa.processor.protocol.CatalogPublicationReader
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolCompatibilityException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentReader
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentSigner
import com.myphotosanywhere.mpa.processor.protocol.TrustedProtocolActor
import com.myphotosanywhere.mpa.processor.protocol.VerifiedProtocolDocument
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.UUID

class ProcessorProtocolCatalogTest {
    private val reader = CatalogPublicationReader(CatalogWorkspace("test-account", "test-workspace"))
    private val codec = CanonicalProtocolJson()
    private fun fixture() = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/catalog-publication.json")) {
        "Shared publication fixture missing."
    }.bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
    private fun canonical(value: JsonElement) = codec.encode(value.toString().toByteArray(Charsets.UTF_8))
    private fun verified(value: JsonElement, sequence: Long = 3) = VerifiedProtocolDocument(UUID.randomUUID(), UUID.randomUUID(), 1, sequence,
        "catalog-commit", 0, listOf(CatalogPublicationReader.CAPABILITY), canonical(value).toString(Charsets.UTF_8))

    @Test fun sharedCommitAndIndexRetainBoundsAndPartitionRoles() {
        val fixture = fixture(); val commit = reader.readCommit(verified(fixture["commit"]))
        assertEquals("22222222-2222-4222-8222-222222222222", commit.generationId.toString())
        assertEquals(0L, commit.settingsRevision); assertEquals(1L, commit.minimumHistoryGeneration)
        assertEquals("previous-commit", commit.parent?.fileId); assertEquals("root-index", commit.rootIndex.fileId)
        val index = reader.readIndex(canonical(fixture["index"]), "")
        assertEquals(2, index.children.size); assertEquals("0a", index.children[0].edge); assertNull(index.children[0].chunk)
        assertEquals(CatalogPublicationReader.MAX_COMPRESSED_BYTES.toLong(), index.children[1].reference.bytes)
        assertEquals(CatalogPublicationReader.MAX_EXPANDED_BYTES.toLong(), index.children[1].chunk?.expandedBytes)
        assertEquals(0, reader.readIndex("""{"children":[],"format":"mpa-catalog-index","major":1,"prefix":""}""".toByteArray(), "").children.size)
        val deepest = canonical(fixture["index"]).toString(Charsets.UTF_8).replace("\"prefix\":\"\"", "\"prefix\":\"000000\"")
            .replace("\"kind\":\"index\"", "\"kind\":\"chunk\"").replace("\"expanded\":null", "\"expanded\":{\"bytes\":\"1\",\"sha256\":\"${"a".repeat(64)}\"}")
        assertEquals(2, reader.readIndex(deepest.toByteArray(Charsets.UTF_8), "000000").children.size)
    }

    @Test fun bothConsumersRejectEverySharedMalformedCommitAndIndex() {
        val fixture = fixture(); val commits = fixture.getAsJsonArray("invalidCommits"); val indexes = fixture.getAsJsonArray("invalidIndexes")
        assertEquals(18, commits.size()); assertEquals(21, indexes.size())
        commits.forEach { item ->
            val row = item.asJsonObject
            assertThrows(row["name"].asString, ProcessorProtocolException::class.java) {
                reader.readCommit(verified(row["payload"], row["sequence"].asString.toLong()))
            }
        }
        indexes.forEach { item ->
            val row = item.asJsonObject
            assertThrows(row["name"].asString, ProcessorProtocolException::class.java) { reader.readIndex(canonical(row["node"]), row["prefix"].asString) }
        }
    }

    @Test fun approvedSignaturePrecedesTypedCommitAndCanonicalIndex() {
        val fixture = fixture()
        val key = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        val signer = ProtocolDocumentSigner(key.private, key.public); val library = UUID.randomUUID(); val actor = UUID.randomUUID()
        val authority = TrustedProtocolActor(library, actor, 1, key.public.encoded, setOf("catalog-commit"))
        val body = """{"format":"mpa","major":1,"minor":0,"libraryId":"$library","actorId":"$actor","epoch":"1","sequence":"3","kind":"catalog-commit","requiredCapabilities":["${CatalogPublicationReader.CAPABILITY}"],"payload":${fixture["commit"]}}""".toByteArray(Charsets.UTF_8)
        val verified = ProtocolDocumentReader(setOf(CatalogPublicationReader.CAPABILITY)).verifyAndRead(body, signer.sign(body), authority)
        assertEquals("root-index", reader.readCommit(verified).rootIndex.fileId)
        assertThrows(ProcessorProtocolException::class.java) { reader.readCommit(verified.copy(kind = "human-operation")) }
        assertThrows(ProcessorProtocolException::class.java) { reader.readCommit(verified.copy(requiredCapabilities = emptyList())) }
        val index = canonical(fixture["index"]).toString(Charsets.UTF_8)
        assertThrows(ProcessorProtocolException::class.java) { reader.readIndex(" $index".toByteArray(Charsets.UTF_8), "") }
        assertThrows(ProtocolCompatibilityException::class.java) { reader.readIndex(index.replace("\"major\":1", "\"major\":2").toByteArray(Charsets.UTF_8), "") }
        assertThrows(ProcessorProtocolException::class.java) { reader.readIndex(ByteArray(CatalogPublicationReader.MAX_INDEX_BYTES + 1), "") }
        val genesis = verified.copy(sequence = 1, canonicalPayload = verified.canonicalPayload.replace("\"parent\":${fixture["commit"].asJsonObject["parent"]}", "\"parent\":null"))
        assertNull(reader.readCommit(genesis).parent)
    }
}

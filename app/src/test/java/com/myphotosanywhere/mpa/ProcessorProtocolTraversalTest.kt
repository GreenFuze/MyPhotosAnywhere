package com.myphotosanywhere.mpa

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
import com.myphotosanywhere.mpa.processor.protocol.CatalogFactsTraversal
import com.myphotosanywhere.mpa.processor.protocol.CatalogObjectReference
import com.myphotosanywhere.mpa.processor.protocol.CatalogObjectSource
import com.myphotosanywhere.mpa.processor.protocol.CatalogPublicationReader
import com.myphotosanywhere.mpa.processor.protocol.CatalogTraversalLimitException
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.DriveCatalogFacts
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolCompatibilityException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentReader
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentSigner
import com.myphotosanywhere.mpa.processor.protocol.TrustedProtocolActor
import com.myphotosanywhere.mpa.processor.protocol.VerifiedProtocolDocument
import java.io.IOException
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Test

class ProcessorProtocolTraversalTest {
    private val library = UUID.fromString("94a81164-e980-4ec1-a566-cec36475b839")
    private val workspace = CatalogWorkspace("workspace-account", "workspace-folder")
    private fun fixture() = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/catalog-traversal.json")) {
        "Shared catalog traversal fixture missing."
    }.bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
    private fun verified(graph: JsonObject) = VerifiedProtocolDocument(UUID.fromString(graph["libraryId"].asString), UUID.randomUUID(), 1,
        graph["sequence"].asString.toLong(), "catalog-commit", 0, graph.getAsJsonArray("requiredCapabilities").map { it.asString },
        CanonicalProtocolJson().encode(graph["commit"].toString().toByteArray(Charsets.UTF_8)).toString(Charsets.UTF_8))

    @Test fun bothConsumersWalkIndependentTreeInIdentityOrderAndValidateEmptyRoot() = runBlocking {
        val fixture = fixture(); val graph = fixture.getAsJsonObject("valid"); val source = Source(graph)
        val leaves = CatalogFactsTraversal(library, workspace, source).readLeaves(verified(graph)).toList()
        assertEquals(fixture.getAsJsonArray("expectedIds").map { it.asString }, leaves.flatMap { it.facts.assets }.map { it.id.toString() })
        assertEquals(listOf("index-root", "index-56", "leaf-5603", "leaf-5687"), source.reads)
        val empty = fixture.getAsJsonObject("empty"); val emptySource = Source(empty)
        assertEquals(0, CatalogFactsTraversal(library, workspace, emptySource, 2).readLeaves(verified(empty)).toList().size)
        assertEquals(listOf("index-root"), emptySource.reads)
    }
    @Test fun bothConsumersRejectEveryMalformedLateFailedAliasedOrOverBudgetTree() {
        val cases = fixture().getAsJsonArray("invalid"); assertEquals(21, cases.size())
        cases.forEach { value ->
            val row = value.asJsonObject; val graph = row.getAsJsonObject("graph"); val source = Source(graph); var emitted = 0
            val expected = when (row["error"].asString) {
                "format" -> ProcessorProtocolException::class.java
                "compatibility" -> ProtocolCompatibilityException::class.java
                "limit" -> CatalogTraversalLimitException::class.java
                "io" -> IOException::class.java
                else -> error("Unknown shared traversal failure category.")
            }
            assertThrows(row["name"].asString, expected) {
                runBlocking { CatalogFactsTraversal(library, workspace, source, graph["objectBudget"].asInt).readLeaves(verified(graph)).collect { emitted++ } }
            }
            assertEquals(row["name"].asString, row["emittedBeforeFailure"].asInt, emitted)
        }
    }
    @Test fun stoppingCollectionDoesNotPrefetchAndLateCancellationCannotEmitLeaf() {
        val graph = fixture().getAsJsonObject("valid"); val source = Source(graph)
        val first = runBlocking { CatalogFactsTraversal(library, workspace, source).readLeaves(verified(graph)).take(1).toList() }
        assertEquals("5603", first.single().prefix); assertEquals(3, source.reads.size)
        val canceled = Job(); val late = Source(graph)
        late.afterRead = { if (it.fileId == "leaf-5603") canceled.cancel() }
        assertThrows(CancellationException::class.java) { runBlocking { withContext(canceled) { CatalogFactsTraversal(library, workspace, late).readLeaves(verified(graph)).toList() } } }
        assertEquals(3, late.reads.size)
        assertThrows(IllegalArgumentException::class.java) { CatalogFactsTraversal(library, workspace, source, 0) }
    }
    @Test fun approvedSignatureAndCapabilitiesPrecedeEveryTreeRead() = runBlocking {
        val graph = fixture().getAsJsonObject("valid"); val source = Source(graph)
        val key = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        val signer = ProtocolDocumentSigner(key.private, key.public); val actor = UUID.randomUUID()
        val authority = TrustedProtocolActor(library, actor, 1, key.public.encoded, setOf("catalog-commit"))
        val body = """{"format":"mpa","major":1,"minor":0,"libraryId":"$library","actorId":"$actor","epoch":"1","sequence":"1","kind":"catalog-commit","requiredCapabilities":["${DriveCatalogFacts.CAPABILITY}","${CatalogPublicationReader.CAPABILITY}"],"payload":${graph["commit"]}}""".toByteArray(Charsets.UTF_8)
        val signature = signer.sign(body); val protocol = ProtocolDocumentReader(setOf(DriveCatalogFacts.CAPABILITY, CatalogPublicationReader.CAPABILITY))
        val changed = body.toString(Charsets.UTF_8).replace("\"settingsRevision\":\"1\"", "\"settingsRevision\":\"2\"").toByteArray(Charsets.UTF_8)
        assertFalse(body.contentEquals(changed))
        assertThrows(ProcessorProtocolException::class.java) { protocol.verifyAndRead(changed, signature, authority) }; assertEquals(0, source.reads.size)
        assertEquals(2, CatalogFactsTraversal(library, workspace, source).readLeaves(protocol.verifyAndRead(body, signature, authority)).toList().size)
    }
    private class Source(graph: JsonObject) : CatalogObjectSource {
        private val objects = graph.getAsJsonObject("objects").entrySet().associate { it.key to Base64.getDecoder().decode(it.value.asString) }
        val reads = ArrayList<String>()
        var afterRead: ((CatalogObjectReference) -> Unit)? = null
        override suspend fun read(reference: CatalogObjectReference): ByteArray {
            currentCoroutineContext().ensureActive(); reads.add(reference.fileId); afterRead?.invoke(reference)
            return objects[reference.fileId] ?: throw IOException("Synthetic catalog object is unavailable.")
        }
    }
}

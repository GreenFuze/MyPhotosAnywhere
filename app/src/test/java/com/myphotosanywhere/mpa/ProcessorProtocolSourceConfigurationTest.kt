package com.myphotosanywhere.mpa

import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
import com.myphotosanywhere.mpa.processor.protocol.CatalogFactsTraversal
import com.myphotosanywhere.mpa.processor.protocol.CatalogObjectReference
import com.myphotosanywhere.mpa.processor.protocol.CatalogObjectSource
import com.myphotosanywhere.mpa.processor.protocol.CatalogPublicationReader
import com.myphotosanywhere.mpa.processor.protocol.CatalogSourceConfiguration
import com.myphotosanywhere.mpa.processor.protocol.CatalogSourceCoverageSet
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentReader
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentSigner
import com.myphotosanywhere.mpa.processor.protocol.TrustedProtocolActor
import com.myphotosanywhere.mpa.processor.protocol.VerifiedProtocolDocument
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import java.util.UUID
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class ProcessorProtocolSourceConfigurationTest {
    private val library = UUID.fromString("94a81164-e980-4ec1-a566-cec36475b839")
    private val workspace = CatalogWorkspace("workspace-account", "workspace-folder")
    private fun fixture(name: String = "catalog-source-configuration") = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/$name.json"))
        .bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
    private fun bytes(value: JsonElement) = CanonicalProtocolJson().encode(value.toString().toByteArray(Charsets.UTF_8))
    private fun verified(graph: JsonObject, payload: String = "payload") = VerifiedProtocolDocument(UUID.fromString(graph["libraryId"].asString), UUID.randomUUID(), 1,
        graph["sequence"]?.asString?.toLong() ?: 1, graph["kind"]?.asString ?: "catalog-commit", 0,
        graph.getAsJsonArray("requiredCapabilities").map { it.asString }, bytes(graph[payload]).toString(Charsets.UTF_8))
    private fun read(graph: JsonObject) = CatalogPublicationReader(workspace).readSourceConfiguration(verified(graph), library)
    private fun coverage(graph: JsonObject): CatalogSourceCoverageSet {
        val reader = CatalogPublicationReader(workspace); val root = reader.readCoverageRoot(bytes(graph["root"]), library)
        return CatalogSourceCoverageSet(root, graph.getAsJsonArray("pages").mapIndexed { index, page -> reader.readCoveragePage(bytes(page), root, root.pages[index]) })
    }

    @Test fun approvedPoliciesAdmitPartialAndDisconnectedCoverageWithoutGrantingNewSources() {
        val configuration = read(fixture().getAsJsonObject("valid"))
        configuration.validateCoverage(coverage(fixture("catalog-coverage").getAsJsonObject("valid")))
        assertEquals(7L, configuration.revision); assertEquals(3, configuration.sources.size); assertEquals(3, configuration.accounts.size)
        assertThrows(UnsupportedOperationException::class.java) { (configuration.sources as MutableList<*>).clear() }
        assertThrows(UnsupportedOperationException::class.java) { (configuration.accounts as MutableList<*>).clear() }
    }

    @Test fun emptyAndMaximumSourceAndAccountBoundsRemainUsable() {
        val configFixture = fixture(); val coverageFixture = fixture("catalog-coverage")
        read(configFixture.getAsJsonObject("empty")).validateCoverage(coverage(coverageFixture.getAsJsonObject("empty")))
        val maximum = read(configFixture.getAsJsonObject("maximum")); assertEquals(256, maximum.sources.size)
        maximum.validateCoverage(coverage(coverageFixture.getAsJsonObject("maximum")))
        assertEquals(128, read(configFixture.getAsJsonObject("maximumAccounts")).accounts.size)
    }

    @Test fun bothConsumersRejectAllMalformedUnscopedAndUnassociatedConfigurations() {
        val rows = fixture().getAsJsonArray("invalid"); assertEquals(30, rows.size())
        rows.forEach { row ->
            val value = row.asJsonObject
            assertThrows(value["name"].asString, ProcessorProtocolException::class.java) { read(value.getAsJsonObject("graph")) }
        }
    }

    @Test fun coverageCannotRetargetOrEnablePoliciesFromAnotherConfiguration() {
        val coverage = coverage(fixture("catalog-coverage").getAsJsonObject("valid")); val rows = fixture().getAsJsonArray("coverageConflicts"); assertEquals(9, rows.size())
        rows.forEach { row ->
            val value = row.asJsonObject; val configuration = read(value.getAsJsonObject("graph"))
            assertThrows(value["name"].asString, ProcessorProtocolException::class.java) { configuration.validateCoverage(coverage) }
        }
    }

    @Test fun controllerSignatureMustMatchThePreviouslyEnrolledActor() {
        val graph = fixture().getAsJsonObject("valid"); val actor = UUID.randomUUID()
        val generator = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }
        val key = generator.generateKeyPair(); val other = generator.generateKeyPair(); val signer = ProtocolDocumentSigner(key.private, key.public)
        val body = """{"format":"mpa","major":1,"minor":0,"libraryId":"$library","actorId":"$actor","epoch":"1","sequence":"1","kind":"library-source-configuration","requiredCapabilities":${graph["requiredCapabilities"]},"payload":${graph["payload"]}}""".toByteArray(Charsets.UTF_8)
        val reader = ProtocolDocumentReader(setOf(CatalogSourceConfiguration.CAPABILITY)); val signature = signer.sign(body)
        val wrong = TrustedProtocolActor(library, actor, 1, other.public.encoded, setOf("library-source-configuration"))
        assertThrows(ProcessorProtocolException::class.java) { reader.verifyAndRead(body, signature, wrong) }
        val enrolled = TrustedProtocolActor(library, actor, 1, key.public.encoded, setOf("library-source-configuration"))
        assertEquals(3, CatalogPublicationReader(workspace).readSourceConfiguration(reader.verifyAndRead(body, signature, enrolled), library).sources.size)
    }

    @Test fun coveredTraversalUsesApprovedConfigurationBeforeAnyLeafIsEmitted() = runBlocking {
        val configuration = read(fixture().getAsJsonObject("coveredTraversalConfiguration")); val graph = fixture("catalog-covered-traversal").getAsJsonObject("valid")
        var admitted = 0
        val leaves = CatalogFactsTraversal(library, workspace, Source(graph)).readCoveredLeaves(verified(graph, "commit")) {
            configuration.validateCoverage(it); admitted++
        }.toList()
        assertEquals(1, admitted); assertEquals(2, leaves.size)
    }
    private class Source(graph: JsonObject) : CatalogObjectSource {
        private val objects = graph.getAsJsonObject("objects").entrySet().associate { it.key to Base64.getDecoder().decode(it.value.asString) }
        override suspend fun read(reference: CatalogObjectReference): ByteArray {
            currentCoroutineContext().ensureActive(); return checkNotNull(objects[reference.fileId])
        }
    }
}

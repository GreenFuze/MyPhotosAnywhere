package com.myphotosanywhere.mpa

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
import com.myphotosanywhere.mpa.processor.protocol.CatalogFactsTraversal
import com.myphotosanywhere.mpa.processor.protocol.CatalogObjectReference
import com.myphotosanywhere.mpa.processor.protocol.CatalogObjectSource
import com.myphotosanywhere.mpa.processor.protocol.CatalogTraversalLimitException
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolCompatibilityException
import com.myphotosanywhere.mpa.processor.protocol.VerifiedProtocolDocument
import java.io.IOException
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
import org.junit.Assert.assertTrue
import org.junit.Test

class ProcessorProtocolCoveredTraversalTest {
    private val library = UUID.fromString("94a81164-e980-4ec1-a566-cec36475b839")
    private val workspace = CatalogWorkspace("workspace-account", "workspace-folder")
    private fun fixture() = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/catalog-covered-traversal.json"))
        .bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
    private fun verified(graph: JsonObject) = VerifiedProtocolDocument(UUID.fromString(graph["libraryId"].asString), UUID.randomUUID(), 1,
        graph["sequence"].asString.toLong(), "catalog-commit", 0, graph.getAsJsonArray("requiredCapabilities").map { it.asString },
        CanonicalProtocolJson().encode(graph["commit"].toString().toByteArray(Charsets.UTF_8)).toString(Charsets.UTF_8))
    private fun reader(graph: JsonObject, source: Source) = CatalogFactsTraversal(library, workspace, source, graph["objectBudget"].asInt)

    @Test fun completeCoveragePrecedesConfigurationAdmissionAndEveryPhotoRead() = runBlocking {
        val fixture = fixture(); val graph = fixture.getAsJsonObject("valid"); val source = Source(graph); var approved = false
        val leaves = reader(graph, source).readCoveredLeaves(verified(graph)) { coverage ->
            currentCoroutineContext().ensureActive(); assertEquals(3, source.reads.size)
            assertEquals(3, coverage.root.sourceCount); assertEquals(7L, coverage.root.configurationRevision)
            assertEquals(31L, coverage.root.discoveryRevision); assertEquals(3, coverage.sources.size)
            assertEquals("account-A", coverage.sources[0].accountId); assertEquals("partial", coverage.sources[0].state)
            assertEquals("disconnected", coverage.sources[1].state); assertEquals("offline", coverage.sources[2].state)
            assertThrows(UnsupportedOperationException::class.java) { (coverage.sources as MutableList).clear() }
            approved = true
        }.toList()
        assertTrue(approved)
        assertEquals(fixture.getAsJsonArray("expectedIds").map { it.asString }, leaves.flatMap { it.facts.assets }.map { it.id.toString() })
        assertEquals(listOf("coverage-only", "coverage-page-0", "coverage-page-1", "index-root", "index-56", "leaf-5603", "leaf-5687"), source.reads)
    }

    @Test fun emptyCatalogStillValidatesCoverageAndConfiguration() = runBlocking {
        val graph = fixture().getAsJsonObject("empty"); val source = Source(graph); var approved = 0
        val leaves = reader(graph, source).readCoveredLeaves(verified(graph)) { coverage ->
            assertEquals(0, coverage.root.sourceCount); assertEquals(1, source.reads.size); approved++
        }.toList()
        assertEquals(1, approved); assertTrue(leaves.isEmpty()); assertEquals(listOf("coverage-only", "index-root"), source.reads)
    }

    @Test fun bothConsumersRejectEveryFailedCoverageAliasedRoleOrConflictingMembership() {
        val rows = fixture().getAsJsonArray("invalid"); assertEquals(22, rows.size())
        rows.forEach { value ->
            val row = value.asJsonObject; val graph = row.getAsJsonObject("graph"); val source = Source(graph); var emitted = 0
            val expected = when (row["error"].asString) {
                "format" -> ProcessorProtocolException::class.java
                "compatibility" -> ProtocolCompatibilityException::class.java
                "limit" -> CatalogTraversalLimitException::class.java
                "io" -> IOException::class.java
                else -> error("Unknown fixture failure category")
            }
            assertThrows(row["name"].asString, expected) {
                runBlocking { reader(graph, source).readCoveredLeaves(verified(graph)) {}.collect { emitted++ } }
            }
            assertEquals(row["name"].asString, row["emittedBeforeFailure"].asInt, emitted)
        }
    }

    @Test fun rejectedConfigurationAndCancellationNeverReadFactsOrBecomeSuccessfulEmptyResults() {
        val graph = fixture().getAsJsonObject("valid"); val source = Source(graph)
        assertThrows(IOException::class.java) {
            runBlocking { reader(graph, source).readCoveredLeaves(verified(graph)) { throw IOException("Unapproved source configuration") }.collect { error("Rejected configuration cannot emit facts") } }
        }
        assertEquals(3, source.reads.size)
        val canceledSource = Source(graph); val job = Job(); canceledSource.afterRead = { job.cancel() }
        assertThrows(CancellationException::class.java) {
            runBlocking { withContext(job) { reader(graph, canceledSource).readCoveredLeaves(verified(graph)) {}.toList() } }
        }
        assertEquals(1, canceledSource.reads.size)
    }

    @Test fun abandonedStagingReadDoesNotReadTheNextPhotoChunk() = runBlocking {
        val graph = fixture().getAsJsonObject("valid"); val source = Source(graph)
        val leaves = reader(graph, source).readCoveredLeaves(verified(graph)) {}.take(1).toList()
        assertEquals("5603", leaves.single().prefix); assertEquals(6, source.reads.size); assertFalse("leaf-5687" in source.reads)
    }
    private class Source(graph: JsonObject) : CatalogObjectSource {
        private val objects = graph.getAsJsonObject("objects").entrySet().associate { it.key to Base64.getDecoder().decode(it.value.asString) }
        val reads = mutableListOf<String>()
        var afterRead: ((CatalogObjectReference) -> Unit)? = null
        override suspend fun read(reference: CatalogObjectReference): ByteArray {
            currentCoroutineContext().ensureActive(); reads += reference.fileId; afterRead?.invoke(reference)
            return objects[reference.fileId] ?: throw IOException("Synthetic catalog object is unavailable")
        }
    }
}

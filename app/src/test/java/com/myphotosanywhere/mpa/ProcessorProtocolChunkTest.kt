package com.myphotosanywhere.mpa

import com.google.gson.JsonElement
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.CatalogChunkDecoder
import com.myphotosanywhere.mpa.processor.protocol.CatalogChunkReference
import com.myphotosanywhere.mpa.processor.protocol.CatalogObjectReference
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import java.util.Base64
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class ProcessorProtocolChunkTest {
    private fun fixture() = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/catalog-chunks.json")) {
        "Shared compressed catalog fixture missing."
    }.bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
    private fun reference(item: JsonElement): CatalogChunkReference {
        val value = item.asJsonObject.getAsJsonObject("expected"); val encoded = value.getAsJsonObject("encoded")
        return CatalogChunkReference(CatalogObjectReference(encoded["accountId"].asString, encoded["fileId"].asString,
            encoded["workspaceId"].asString, encoded["bytes"].asString.toLong(), encoded["sha256"].asString),
            value["expandedBytes"].asString.toLong(), value["expandedSha256"].asString)
    }
    @Test fun bothConsumersDecodeIndependentPythonGzip() = runBlocking {
        val value = fixture().getAsJsonObject("valid"); val encoded = Base64.getDecoder().decode(value["compressed"].asString)
        val before = encoded.copyOf(); val decoded = CatalogChunkDecoder().decode(encoded, reference(value))
        assertEquals(value["canonical"].asString, decoded.toString(Charsets.UTF_8)); assertArrayEquals(before, encoded)
    }
    @Test fun bothConsumersRejectEveryCorruptNoncanonicalAndOversizedSharedCase() {
        val cases = fixture().getAsJsonArray("invalid"); assertEquals(16, cases.size())
        cases.forEach { value ->
            val row = value.asJsonObject; val bytes = Base64.getDecoder().decode(row["compressed"].asString)
            assertThrows(row["name"].asString, ProcessorProtocolException::class.java) {
                runBlocking { CatalogChunkDecoder().decode(bytes, reference(value)) }
            }
        }
    }
    @Test fun cancellationAndSmallerConsumerBudgetsRemainBlocking() {
        val value = fixture().getAsJsonObject("valid"); val reference = reference(value)
        val encoded = Base64.getDecoder().decode(value["compressed"].asString)
        val canceled = Job().apply { cancel() }
        assertThrows(CancellationException::class.java) { runBlocking { withContext(canceled) { CatalogChunkDecoder().decode(encoded, reference) } } }
        assertThrows(ProcessorProtocolException::class.java) { runBlocking { CatalogChunkDecoder(compressedLimit = encoded.size - 1).decode(encoded, reference) } }
        assertThrows(ProcessorProtocolException::class.java) { runBlocking { CatalogChunkDecoder(expandedLimit = reference.expandedBytes.toInt() - 1).decode(encoded, reference) } }
    }
}

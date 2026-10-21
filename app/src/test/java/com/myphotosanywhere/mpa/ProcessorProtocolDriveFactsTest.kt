package com.myphotosanywhere.mpa

import com.google.gson.JsonElement
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
import com.myphotosanywhere.mpa.processor.protocol.DriveCatalogFacts
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolCompatibilityException
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test
import java.util.UUID

class ProcessorProtocolDriveFactsTest {
    private val reader = DriveCatalogFacts(UUID.fromString("94a81164-e980-4ec1-a566-cec36475b839"))
    private fun fixture() = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/drive-catalog-facts.json")) {
        "Shared observed Drive facts fixture missing."
    }.bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
    private fun canonical(value: JsonElement) = CanonicalProtocolJson().encode(value.toString().toByteArray(Charsets.UTF_8))

    @Test fun bothConsumersPreserveAccountIdentityUnknownDatesAndLiteralMetadata() {
        val fixture = fixture(); val chunk = reader.read(canonical(fixture["valid"]), fixture["prefix"].asString)
        assertEquals(2, chunk.assets.size)
        val photo = chunk.assets.single { it.accountId == "account-A" }; val video = chunk.assets.single { it.accountId == "account-B" }
        assertNotEquals(photo.id, video.id); assertEquals(photo.name, video.name); assertEquals("שלום.jpg", photo.name)
        assertEquals(Long.MAX_VALUE, photo.driveVersion); assertEquals(-1L, photo.effectiveUtc); assertEquals(1L, photo.modifiedUtc)
        assertEquals(2, photo.sources.size); assertEquals(3, photo.sources[1].depth)
        assertNull(video.capturedUtc); assertNull(video.effectiveUtc); assertNull(video.width)
        assertEquals("2020:10:11 07:30:00", video.rawCapture)
    }

    @Test fun bothConsumersRejectAllSharedMalformedObservedFacts() {
        val fixture = fixture(); val cases = fixture.getAsJsonArray("invalid"); assertEquals(38, cases.size())
        cases.forEach { item ->
            val row = item.asJsonObject
            assertThrows(row["name"].asString, ProcessorProtocolException::class.java) { reader.read(canonical(row["body"]), row["prefix"].asString) }
        }
    }

    @Test fun noncanonicalAndUnknownVersionNeverBecomeAnEmptyCatalog() {
        val fixture = fixture(); val bytes = canonical(fixture["valid"]).toString(Charsets.UTF_8); val prefix = fixture["prefix"].asString
        assertThrows(ProcessorProtocolException::class.java) { reader.read(" $bytes".toByteArray(Charsets.UTF_8), prefix) }
        assertThrows(ProtocolCompatibilityException::class.java) { reader.read(bytes.replace("\"major\":1", "\"major\":2").toByteArray(Charsets.UTF_8), prefix) }
    }
}

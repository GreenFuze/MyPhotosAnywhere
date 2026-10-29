package com.myphotosanywhere.mpa

import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
import com.myphotosanywhere.mpa.processor.protocol.DriveAssetIdentity
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import java.util.UUID

class ProcessorProtocolContractTest {
    private val codec = CanonicalProtocolJson()

    @Test fun sharedCanonicalFixturesMatchExactBytes() {
        val fixtures = fixture("codec.json").asJsonObject
        fixtures.getAsJsonArray("valid").forEach { entry ->
            val test = entry.asJsonObject
            val expected = test["canonical"].asString.toByteArray(Charsets.UTF_8)
            assertArrayEquals(test["name"].asString, expected, codec.encode(test["input"].asString.toByteArray(Charsets.UTF_8)))
            assertArrayEquals(expected, codec.encode(expected))
        }
        fixtures.getAsJsonArray("invalid").forEach { entry ->
            assertThrows(ProcessorProtocolException::class.java) { codec.encode(entry.asJsonObject["input"].asString.toByteArray(Charsets.UTF_8)) }
        }
    }

    @Test fun parserRejectsMalformedUtf8AndResourceLimitViolations() {
        assertThrows(ProcessorProtocolException::class.java) { codec.encode(byteArrayOf(0x22, 0xc3.toByte(), 0x28, 0x22)) }
        assertThrows(ProcessorProtocolException::class.java) { CanonicalProtocolJson(4).encode("[0,0]".toByteArray()) }
        assertThrows(ProcessorProtocolException::class.java) { codec.encode(("[" + List(1001) { "0" }.joinToString(",") + "]").toByteArray()) }
        assertThrows(ProcessorProtocolException::class.java) { codec.encode(("[".repeat(33) + "0" + "]".repeat(33)).toByteArray()) }
        assertThrows(ProcessorProtocolException::class.java) { codec.encode(("{\"" + "a".repeat(129) + "\":0}").toByteArray()) }
        assertThrows(ProcessorProtocolException::class.java) { codec.encode(("\"" + "a".repeat(65537) + "\"").toByteArray()) }
        val fields = (0..256).joinToString(",") { "\"k$it\":0" }
        assertThrows(ProcessorProtocolException::class.java) { codec.encode("{$fields}".toByteArray()) }
    }

    @Test fun unicodeKeysUseUtf16OrderWithoutNormalization() {
        assertEquals("{\"😀\":1,\"\ue000\":2}", codec.encode("{\"\ue000\":2,\"😀\":1}".toByteArray(Charsets.UTF_8)).toString(Charsets.UTF_8))
    }

    @Test fun sharedDriveIdentityFixturesMatchIndependentReference() {
        fixture("identities.json").asJsonArray.forEach { item ->
            val row = item.asJsonObject
            val identity = DriveAssetIdentity(UUID.fromString(row["libraryId"].asString))
            assertEquals(row["assetId"].asString, identity.derive(row["accountPermissionId"].asString, row["fileId"].asString).toString())
        }
    }

    @Test fun pathsEmailsAndMissingLibrariesCannotBecomeIdentity() {
        assertThrows(IllegalArgumentException::class.java) { DriveAssetIdentity(UUID(0, 0)) }
        val identity = DriveAssetIdentity(UUID.fromString("94a81164-e980-4ec1-a566-cec36475b839"))
        listOf("", "person@example.com", "folder/photo.jpg", "../path", "x".repeat(257)).forEach { value ->
            assertThrows(ProcessorProtocolException::class.java) { identity.derive(value, "file") }
        }
        assertNotEquals(identity.derive("account-A", "file"), identity.derive("account-B", "file"))
        assertNotEquals(identity.derive("account", "File"), identity.derive("account", "file"))
    }

    private fun fixture(name: String) = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/$name")) {
        "Shared protocol fixture missing."
    }.bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it) }
}

package com.myphotosanywhere.mpa

import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentVerifier
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class ProcessorLibraryConnectionDescriptorTest {
    private fun fixture() = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/library-connection.json"))
        .bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }

    @Test fun bothClientsReadTheIndependentPublicConnectionVectorsWithoutChangingIdentity() {
        val valid = fixture()["valid"].asJsonArray; assertEquals(3, valid.size())
        valid.forEach { value ->
            val row = value.asJsonObject; val code = row["code"].asString
            val actual = LibraryConnectionDescriptor.decode(code)
            assertEquals(code, actual.encode()); assertEquals(row["sha256"].asString, actual.documentSha256)
            assertEquals(UUID.fromString("94a81164-e980-4ec1-a566-cec36475b839"), actual.libraryId)
            assertEquals(UUID.fromString("6c803c09-3b11-49cb-a3dc-1d42c3dc6f30"), actual.controllerId)
            assertTrue(actual.controllerEpoch > 0); assertNotEquals("root", actual.workspace.folderId)
            assertEquals(ProtocolDocumentVerifier(Base64.getDecoder().decode(actual.controllerPublicKeySpki)).keyId, actual.controllerKeyId)
        }
        assertEquals(Long.MAX_VALUE, LibraryConnectionDescriptor.decode(valid[1].asJsonObject["code"].asString).controllerEpoch)
        assertEquals(256, LibraryConnectionDescriptor.decode(valid[2].asJsonObject["code"].asString).workspace.accountId.length)
    }

    @Test fun bothClientsRejectEveryMalformedOverbroadAndNoncanonicalConnectionVector() {
        val invalid = fixture()["invalid"].asJsonArray; assertEquals(52, invalid.size())
        invalid.forEach { value ->
            val row = value.asJsonObject
            assertThrows(row["name"].asString, ProcessorProtocolException::class.java) { LibraryConnectionDescriptor.decode(row["code"].asString) }
        }
    }

    @Test fun connectionOwnsOnlyValidatedPublicKeyBytesAndCannotImportOtherKeyKinds() {
        val original = LibraryConnectionDescriptor.decode(fixture()["valid"].asJsonArray[0].asJsonObject["code"].asString)
        val bytes = Base64.getDecoder().decode(original.controllerPublicKeySpki)
        val owned = LibraryConnectionDescriptor(original.libraryId, original.controllerId, original.controllerEpoch, original.workspace, bytes)
        bytes[0] = (bytes[0].toInt() xor 1).toByte()
        assertEquals(original.encode(), owned.encode()); assertEquals(original.documentSha256, owned.documentSha256)
        val p384 = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp384r1")) }.generateKeyPair()
        val rsa = KeyPairGenerator.getInstance("RSA").apply { initialize(2048) }.generateKeyPair()
        val p256 = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        val invalid = listOf(p384.public.encoded, rsa.public.encoded, p256.private.encoded, byteArrayOf(), ByteArray(513))
        invalid.forEach { key ->
            assertThrows(ProcessorProtocolException::class.java) { LibraryConnectionDescriptor(original.libraryId, original.controllerId, 1, original.workspace, key) }
        }
        assertThrows(ProcessorProtocolException::class.java) {
            LibraryConnectionDescriptor(UUID(0, 0), original.controllerId, 1, original.workspace, Base64.getDecoder().decode(original.controllerPublicKeySpki))
        }
        assertThrows(ProcessorProtocolException::class.java) {
            LibraryConnectionDescriptor(original.libraryId, original.controllerId, 1, CatalogWorkspace(original.workspace.accountId, "root"),
                Base64.getDecoder().decode(original.controllerPublicKeySpki))
        }
    }
}

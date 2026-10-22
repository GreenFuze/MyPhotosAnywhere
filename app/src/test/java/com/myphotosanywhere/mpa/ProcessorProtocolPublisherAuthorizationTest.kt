package com.myphotosanywhere.mpa

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.CatalogPublicationReader
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import com.myphotosanywhere.mpa.processor.protocol.SignedProtocolDocuments
import com.myphotosanywhere.mpa.processor.protocol.TrustedProtocolActor
import java.security.MessageDigest
import java.util.Base64
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class ProcessorProtocolPublisherAuthorizationTest {
    private val library = UUID.fromString("94a81164-e980-4ec1-a566-cec36475b839")
    private val controller = UUID.fromString("6c803c09-3b11-49cb-a3dc-1d42c3dc6f30")
    private val workspace = CatalogWorkspace("workspace-account", "workspace-folder")
    private fun fixture() = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/catalog-publisher-authorization.json"))
        .bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
    private fun bytes(root: JsonObject, name: String) = root[name].asString.toByteArray(Charsets.UTF_8)
    private fun authority(root: JsonObject, libraryId: UUID = library, actorId: UUID = controller, epoch: Long = 1,
        kinds: Set<String> = setOf("library-publisher-authorization", "user-operation")) =
        TrustedProtocolActor(libraryId, actorId, epoch, Base64.getDecoder().decode(root["controllerPublicKeySpki"].asString), kinds)

    @Test fun controllerApprovalGrantsOnlyTheSeparatePublishersCapturedCatalogStream() {
        val root = fixture(); val encoded = bytes(root, "active"); val reader = CatalogPublicationReader(workspace)
        val policy = reader.readPublisherAuthorization(encoded, authority(root))
        assertEquals(library, policy.libraryId); assertEquals(controller, policy.controllerId); assertEquals(1L, policy.controllerEpoch); assertEquals(9L, policy.sequence)
        assertEquals(workspace, policy.workspace); assertEquals(MessageDigest.getInstance("SHA-256").digest(encoded).joinToString("") { "%02x".format(it) }, policy.envelopeSha256)
        val publisher = checkNotNull(policy.publisher); assertEquals(UUID.fromString(root["publisherId"].asString), publisher.actorId); assertEquals(2L, publisher.epoch)
        val commit = SignedProtocolDocuments(setOf(CatalogPublicationReader.CAPABILITY)).verifyAndRead(bytes(root, "publisherCommit"), publisher)
        assertNull(reader.readCommit(commit).parent); assertEquals(1L, commit.sequence)
        assertThrows(ProcessorProtocolException::class.java) { SignedProtocolDocuments(emptySet()).verifyAndRead(bytes(root, "publisherOperation"), publisher) }
    }

    @Test fun explicitRevocationCarriesNoPublisherAndDoesNotChooseAnOlderPolicy() {
        val root = fixture(); val reader = CatalogPublicationReader(workspace)
        val active = reader.readPublisherAuthorization(bytes(root, "active"), authority(root))
        val revoked = reader.readPublisherAuthorization(bytes(root, "revoked"), authority(root))
        assertEquals(10L, revoked.sequence); assertNull(revoked.publisher); assertNull(revoked.publisherKeyId)
        assertEquals(controller, revoked.controllerId); assertNotEquals(active.envelopeSha256, revoked.envelopeSha256)
        assertNotNull(active.publisher)
    }

    @Test fun bothConsumersRejectEverySignedButInvalidOrOverbroadPublisherPolicy() {
        val root = fixture(); val cases = root["invalid"].asJsonArray; assertEquals(26, cases.size())
        val reader = CatalogPublicationReader(workspace); val authority = authority(root)
        cases.forEach { row -> val item = row.asJsonObject
            assertThrows(item["name"].asString, ProcessorProtocolException::class.java) { reader.readPublisherAuthorization(bytes(item, "envelope"), authority) }
        }
    }

    @Test fun receivedPoliciesCannotSelectControllerLibraryEpochOrWorkspaceAuthority() {
        val root = fixture(); val bytes = bytes(root, "active"); val reader = CatalogPublicationReader(workspace)
        val denied = listOf(authority(root, libraryId = UUID.randomUUID()), authority(root, actorId = UUID.randomUUID()), authority(root, epoch = 2), authority(root, kinds = setOf("catalog-commit")))
        denied.forEach { assertThrows(ProcessorProtocolException::class.java) { reader.readPublisherAuthorization(bytes, it) } }
        assertThrows(ProcessorProtocolException::class.java) { CatalogPublicationReader(CatalogWorkspace("foreign-account", workspace.folderId)).readPublisherAuthorization(bytes, authority(root)) }
        assertThrows(ProcessorProtocolException::class.java) { CatalogPublicationReader(CatalogWorkspace(workspace.accountId, "foreign-folder")).readPublisherAuthorization(bytes, authority(root)) }
        bytes[bytes.lastIndex - 1] = (bytes[bytes.lastIndex - 1].toInt() xor 1).toByte()
        assertThrows(ProcessorProtocolException::class.java) { reader.readPublisherAuthorization(bytes, authority(root)) }
    }
}

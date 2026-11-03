package com.myphotosanywhere.mpa

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import com.myphotosanywhere.mpa.processor.protocol.LibraryCurrentControl
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class ProcessorLibraryCurrentControlTest {
    private fun fixture() = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/library-current-control.json"))
        .bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
    private fun bytes(value: JsonObject,name: String) = value[name].asString.toByteArray(Charsets.UTF_8)
    private fun connection(value: JsonObject) = LibraryConnectionDescriptor.decode(value["code"].asString)
    private fun first(value: JsonObject) = LibraryCurrentControl.read(bytes(value["valid"].asJsonArray[0].asJsonObject,"envelope"),connection(value))

    @Test fun publicV2ConnectionPinsOneActualControlIdentityAndPreservesExactWire() {
        val root=fixture();val connection=connection(root)
        assertEquals("current-control",connection.currentFileId);assertEquals(root["code"].asString,connection.encode())
        assertEquals(root["connectionSha256"].asString,connection.documentSha256)
        val legacy=LibraryConnectionDescriptor.decode(root["legacyCode"].asString)
        assertNull(legacy.currentFileId);assertNotEquals(legacy.documentSha256,connection.documentSha256)
        val invalid=root["invalidCodes"].asJsonArray;assertEquals(8,invalid.size())
        invalid.forEach { row -> assertThrows(row.asJsonObject["name"].asString,ProcessorProtocolException::class.java) { LibraryConnectionDescriptor.decode(row.asJsonObject["code"].asString) } }
    }

    @Test fun bothConsumersReadIndependentSignedSnapshotsAndFullCounterRange() {
        val root=fixture();val connection=connection(root);val first=first(root);val valid=root["valid"].asJsonArray;assertEquals(5,valid.size())
        valid.forEach { row ->
            val current=LibraryCurrentControl.read(bytes(row.asJsonObject,"envelope"),connection);current.assertSuccessorOf(first)
            assertEquals(connection.documentSha256,current.connectionSha256);assertEquals("publishers",current.publishers.reference.fileId)
        }
        assertEquals(Long.MAX_VALUE,LibraryCurrentControl.read(bytes(valid[4].asJsonObject,"envelope"),connection).sequence)
        assertEquals(11L,first.sequence);assertEquals(9L,first.publishers.sequence);assertEquals(10L,first.sources.sequence)
    }

    @Test fun bothConsumersRejectSignedButMalformedAliasedRetargetedAndOverbroadSnapshots() {
        val root=fixture();val connection=connection(root);val invalid=root["invalid"].asJsonArray;assertEquals(39,invalid.size())
        invalid.forEach { row -> assertThrows(row.asJsonObject["name"].asString,ProcessorProtocolException::class.java) { LibraryCurrentControl.read(bytes(row.asJsonObject,"envelope"),connection) } }
    }

    @Test fun durableHeadComparisonRejectsRewindForkAndOlderUnpublishedPolicySelection() {
        val root=fixture();val connection=connection(root);val first=first(root);val invalid=root["invalidTransitions"].asJsonArray;assertEquals(4,invalid.size())
        invalid.forEach { row ->
            val candidate=LibraryCurrentControl.read(bytes(row.asJsonObject,"envelope"),connection)
            assertThrows(row.asJsonObject["name"].asString,ProcessorProtocolException::class.java) { candidate.assertSuccessorOf(first) }
        }
        val retarget=LibraryCurrentControl.read(bytes(root,"retargetHead"),LibraryConnectionDescriptor.decode(root["retargetCode"].asString))
        assertThrows(ProcessorProtocolException::class.java) { retarget.assertSuccessorOf(first) }
    }

    @Test fun legacyPinsAndWrongControlCopiesCannotClaimFreshAuthority() {
        val root=fixture();val bytes=bytes(root["valid"].asJsonArray[0].asJsonObject,"envelope")
        assertThrows(ProcessorProtocolException::class.java) { LibraryCurrentControl.read(bytes,LibraryConnectionDescriptor.decode(root["legacyCode"].asString)) }
        assertThrows(ProcessorProtocolException::class.java) { LibraryCurrentControl.read(bytes,LibraryConnectionDescriptor.decode(root["retargetCode"].asString)) }
        bytes[bytes.lastIndex-1]=(bytes[bytes.lastIndex-1].toInt() xor 1).toByte()
        assertThrows(ProcessorProtocolException::class.java) { LibraryCurrentControl.read(bytes,connection(root)) }
    }

    @Test fun selectedPoliciesRequireExactBytesSequenceAndIndependentRootSignature() {
        val root=fixture();val connection=connection(root);val current=first(root)
        val publisher=current.readPublishers(bytes(root,"publisher"),connection);val sources=current.readSources(bytes(root,"sources"),connection)
        assertEquals(9L,publisher.sequence);assertNotNull(publisher.publisher);assertEquals(7L,sources.revision);assertEquals(3,sources.sources.size)
        assertThrows(ProcessorProtocolException::class.java) { current.readSources(bytes(root,"publisher"),connection) }
        assertThrows(ProcessorProtocolException::class.java) { current.readPublishers(bytes(root,"sources"),connection) }
        val bytes=bytes(root,"publisher");bytes[bytes.lastIndex-1]=(bytes[bytes.lastIndex-1].toInt() xor 1).toByte()
        assertThrows(ProcessorProtocolException::class.java) { current.readPublishers(bytes,connection) }
        assertThrows(ProcessorProtocolException::class.java) { current.readSources(bytes(root,"sources"),LibraryConnectionDescriptor.decode(root["retargetCode"].asString)) }
        val wrong=LibraryCurrentControl.read(bytes(root,"wrongPolicySequence"),connection)
        assertThrows(ProcessorProtocolException::class.java) { wrong.readPublishers(bytes(root,"publisher"),connection) }
    }

    @Test fun explicitRevocationCannotRetainACatalogFromTheOldPublisher() {
        val root=fixture();val connection=connection(root);val revoked=LibraryCurrentControl.read(bytes(root,"revocation"),connection)
        revoked.assertSuccessorOf(first(root));assertNull(revoked.catalog);assertNull(revoked.readPublishers(bytes(root,"revokedPublisher"),connection).publisher)
        val invalid=LibraryCurrentControl.read(bytes(root,"invalidRevocation"),connection)
        assertThrows(ProcessorProtocolException::class.java) { invalid.readPublishers(bytes(root,"revokedPublisher"),connection) }
    }

    @Test fun exactReferencedBytesCannotSubstituteAnotherRootSigningKey() {
        val root=fixture();val connection=connection(root);val current=LibraryCurrentControl.read(bytes(root,"forgedPoliciesHead"),connection)
        assertThrows(ProcessorProtocolException::class.java) { current.readPublishers(bytes(root,"forgedPublisher"),connection) }
        assertThrows(ProcessorProtocolException::class.java) { current.readSources(bytes(root,"forgedSources"),connection) }
    }

    @Test fun acceptedSnapshotOwnsItsIdentityAndRejectsOutOfBoundsBorrowedEnvelopes() {
        val root=fixture();val connection=connection(root);val bytes=bytes(root["valid"].asJsonArray[0].asJsonObject,"envelope")
        val current=LibraryCurrentControl.read(bytes,connection);bytes.fill(0)
        assertEquals(first(root).envelopeSha256,current.envelopeSha256);current.assertSuccessorOf(first(root))
        listOf(0,com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentReader.MAX_DOCUMENT_BYTES+1).forEach { length ->
            val invalid=ByteArray(length)
            assertThrows(ProcessorProtocolException::class.java) { LibraryCurrentControl.read(invalid,connection) }
            assertThrows(ProcessorProtocolException::class.java) { current.readPublishers(invalid,connection) }
            assertThrows(ProcessorProtocolException::class.java) { current.readSources(invalid,connection) }
        }
    }
}

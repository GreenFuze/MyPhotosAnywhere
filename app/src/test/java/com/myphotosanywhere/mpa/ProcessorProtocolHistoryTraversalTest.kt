package com.myphotosanywhere.mpa

import com.google.gson.JsonArray
import com.google.gson.JsonObject
import com.myphotosanywhere.mpa.processor.protocol.*
import java.io.IOException
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.spec.ECGenParameterSpec
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.supervisorScope
import kotlinx.coroutines.withContext
import org.junit.Assert.*
import org.junit.Test

class ProcessorProtocolHistoryTraversalTest {
    private fun key() = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    private inner class Fixture {
        val keys = key();val library = UUID.randomUUID();val actor = UUID.randomUUID();val workspace = CatalogWorkspace("actual-account","metadata")
        val source = Source()
        fun reader(maximum: Int = CatalogCommitHistoryTraversal.MAX_COMMITS,authority: TrustedProtocolActor = TrustedProtocolActor(library,actor,1,keys.public.encoded,setOf("catalog-commit"))) = CatalogCommitHistoryTraversal(workspace,authority,source,maximum)
        fun add(sequence: Long,parent: CatalogObjectReference? = null,generation: UUID = UUID.randomUUID(),rootFile: String? = null,name: String = "commit-${UUID.randomUUID()}",epoch: Long = 1,signing: KeyPair = keys,
            capabilities: List<String> = listOf(DriveCatalogFacts.CAPABILITY,CatalogPublicationReader.CAPABILITY).sorted()): CatalogObjectReference {
            fun ref(id: String) = JsonObject().apply { addProperty("accountId",workspace.accountId);addProperty("fileId",id);addProperty("workspaceId",workspace.folderId);addProperty("bytes","2");addProperty("sha256",hash("{}".toByteArray())) }
            fun ref(value: CatalogObjectReference) = JsonObject().apply { addProperty("accountId",value.accountId);addProperty("fileId",value.fileId);addProperty("workspaceId",value.workspaceId);addProperty("bytes",value.bytes.toString());addProperty("sha256",value.sha256) }
            val payload = JsonObject().apply {
                addProperty("generationId",generation.toString());add("parent",parent?.let(::ref) ?: com.google.gson.JsonNull.INSTANCE)
                add("rootIndex",ref(rootFile ?: "index-$name"));add("sourceCoverage",ref("coverage-$name"));addProperty("settingsRevision","1");addProperty("minimumHistoryGeneration","1")
            }
            val body = JsonObject().apply {
                addProperty("format","mpa");addProperty("major",1);addProperty("minor",0);addProperty("libraryId",library.toString());addProperty("actorId",actor.toString());addProperty("epoch",epoch.toString());addProperty("sequence",sequence.toString());addProperty("kind","catalog-commit")
                add("requiredCapabilities",JsonArray().apply { capabilities.forEach { add(it) } });add("payload",payload)
            }
            val bytes = CanonicalProtocolJson().encode(body.toString().toByteArray(Charsets.UTF_8))
            val envelope = SignedProtocolDocuments(capabilities.toSet()).encode(bytes,ProtocolDocumentSigner(signing.private,signing.public).sign(bytes))
            check(source.objects.put(name,envelope) == null)
            return CatalogObjectReference(workspace.accountId,name,workspace.folderId,envelope.size.toLong(),hash(envelope))
        }
    }
    private class Source : CatalogObjectSource {
        val objects = mutableMapOf<String,ByteArray>();val reads = arrayListOf<String>()
        var hold = false;var retired = false;val entered = CompletableDeferred<Unit>();val release = CompletableDeferred<Unit>()
        override suspend fun read(reference: CatalogObjectReference): ByteArray {
            reads.add(reference.fileId);entered.complete(Unit)
            if(hold) withContext(NonCancellable) { release.await();retired = true }
            return objects[reference.fileId] ?: throw IOException("Synthetic unavailable predecessor")
        }
    }
    private fun hash(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it.toInt() and 255) }
    private suspend fun rejects(fixture: Fixture,head: CatalogObjectReference,emitted: Int = 0) {
        var count = 0
        try { fixture.reader().read(head).collect { count++ };fail("Invalid ancestry completed") } catch (_: ProcessorProtocolException) { }
        assertEquals(emitted,count)
    }

    @Test fun everyActualSignatureIsVerifiedWithoutPrefetchPastConsumerPersistence() = runBlocking {
        val fixture = Fixture();val first = fixture.add(1);val second = fixture.add(2,first);val head = fixture.add(3,second);var expected = 3L
        fixture.reader().read(head).collect { entry ->
            assertEquals(expected,entry.verified.sequence);assertEquals((4 - expected).toInt(),fixture.source.reads.size)
            assertArrayEquals(fixture.source.objects[entry.reference.fileId],entry.envelope);assertEquals(expected == 1L,entry.commit.parent == null);expected--
        }
        assertEquals(0L,expected);assertEquals(3,fixture.source.reads.size)
        fixture.source.reads.clear();assertEquals(1,fixture.reader().read(head).take(1).toList().size);assertEquals(listOf(head.fileId),fixture.source.reads)
    }
    @Test fun missingOrChangedPredecessorFailsAfterHeadWithoutClaimingCompletion() = runBlocking {
        val fixture = Fixture();val first = fixture.add(1);val head = fixture.add(2,first);val original = fixture.source.objects.remove(first.fileId);var emitted = 0
        try { fixture.reader().read(head).collect { emitted++ };fail("Missing ancestor completed") } catch (_: IOException) { }
        assertEquals(1,emitted);val damaged = checkNotNull(original).copyOf().apply { this[lastIndex-1] = (this[lastIndex-1].toInt() xor 1).toByte() }
        fixture.source.objects[first.fileId] = damaged;rejects(fixture,head,1);assertFalse(original.contentEquals(damaged))
    }
    @Test fun validSignaturesCannotHideSequenceGapRepeatedGenerationOrCrossCommitRoleAlias() = runBlocking {
        val fixture = Fixture();val generation = UUID.randomUUID();val first = fixture.add(1,generation=generation)
        rejects(fixture,fixture.add(3,first),1);rejects(fixture,fixture.add(2,first,generation),1)
        val prior = fixture.add(1,rootFile="head-alias")
        rejects(fixture,fixture.add(2,prior,name="head-alias"),1)
    }
    @Test fun receivedReferencesCannotSelectWorkspaceOrBypassWireBoundsBeforeRead() = runBlocking {
        val fixture = Fixture();val first = fixture.add(1)
        for(reference in listOf(first.copy(accountId="foreign"),first.copy(workspaceId="foreign"),first.copy(fileId="root"),first.copy(fileId=fixture.workspace.folderId),first.copy(bytes=262145),first.copy(bytes=0),first.copy(sha256="A".repeat(64)))) rejects(fixture,reference)
        assertTrue(fixture.source.reads.isEmpty())
    }
    @Test fun receivedSignaturesCannotChooseKeyEpochActorLibraryOrCapability() = runBlocking {
        val fixture = Fixture();rejects(fixture,fixture.add(1,epoch=2));rejects(fixture,fixture.add(1,signing=key()))
        val first = fixture.add(1)
        for(authority in listOf(TrustedProtocolActor(UUID.randomUUID(),fixture.actor,1,fixture.keys.public.encoded,setOf("catalog-commit")),
            TrustedProtocolActor(fixture.library,UUID.randomUUID(),1,fixture.keys.public.encoded,setOf("catalog-commit")),
            TrustedProtocolActor(fixture.library,fixture.actor,1,fixture.keys.public.encoded,setOf("user-operation")))) {
            try { fixture.reader(authority=authority).read(first).toList();fail("Foreign authority completed") } catch (_: ProcessorProtocolException) { }
        }
        rejects(fixture,fixture.add(1,capabilities=listOf(CatalogPublicationReader.CAPABILITY)))
    }
    @Test fun knownDepthAndDeclaredStreamSequenceCannotExceedConfiguredHistoryBound() = runBlocking<Unit> {
        val fixture = Fixture();val first = fixture.add(1);val second = fixture.add(2,first)
        try { fixture.reader(1).read(second).toList();fail("Depth bound was ignored") } catch (_: CatalogTraversalLimitException) { }
        assertEquals(1,fixture.source.reads.size)
        assertThrows(IllegalArgumentException::class.java) { fixture.reader(0) };assertThrows(IllegalArgumentException::class.java) { fixture.reader(4097) }
    }
    @Test fun canceledActualReadRetiresBeforeReturningAndCannotEmitLateResult() = runBlocking {
        val fixture = Fixture();val head = fixture.add(1);fixture.source.hold = true;var emitted = 0
        supervisorScope {
            val owner = async { fixture.reader().read(head).collect { emitted++ } };fixture.source.entered.await();owner.cancel()
            assertFalse(owner.isCompleted);fixture.source.release.complete(Unit)
            try { owner.await();fail("Canceled ancestry completed") } catch (_: CancellationException) { }
            assertTrue(fixture.source.retired);assertEquals(0,emitted);assertEquals(1,fixture.source.reads.size)
        }
    }
    @Test fun observedEnvelopeOwnershipIsIndependentOfTransportAndConsumerMutation() = runBlocking {
        val fixture = Fixture();val head = fixture.add(1);val original = checkNotNull(fixture.source.objects[head.fileId]).copyOf()
        val entry = fixture.reader().read(head).toList().single();val exposed = entry.envelope;exposed.fill(0)
        checkNotNull(fixture.source.objects[head.fileId]).fill(1)
        assertArrayEquals(original,entry.envelope);assertEquals(head,entry.reference);assertEquals(1L,entry.verified.sequence)
        assertThrows(UnsupportedOperationException::class.java) { (entry.verified.requiredCapabilities as MutableList<*>).clear() }
        assertTrue(DriveCatalogFacts.CAPABILITY in entry.verified.requiredCapabilities)
    }
}

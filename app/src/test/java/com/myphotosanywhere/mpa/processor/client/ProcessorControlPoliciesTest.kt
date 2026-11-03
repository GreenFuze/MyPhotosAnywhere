package com.myphotosanywhere.mpa.processor.client

import android.app.Application
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.processor.protocol.*
import java.io.File
import java.io.IOException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.supervisorScope
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ProcessorControlPoliciesTest {
    private val fixture=checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/library-current-control.json"))
        .bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
    private val pin=LibraryConnectionDescriptor.decode(fixture["code"].asString)
    private fun head()=fixture["valid"].asJsonArray[0].asJsonObject["envelope"].asString.toByteArray(Charsets.UTF_8)
    private fun store()=ProcessorLibraryStateStore(RuntimeEnvironment.getApplication(),StorageBudget(object:StorageBudget.Capacity {
        override fun available(directory:File)=1024L*1024*1024
        override fun allocatable(directory:File)=1024L*1024*1024
        override fun allocate(directory:File,bytes:Long) { error("Policy reads cannot evict originals.") }
    }),File(RuntimeEnvironment.getApplication().noBackupFilesDir,"selected-policies"))
    private suspend fun approve(store:ProcessorLibraryStateStore,email:String="selected@example.invalid") {
        val connection=ProcessorLibraryConnection(store,object:ProcessorLibraryConnectionReader {
            override suspend fun account()=ProcessorLibraryAccount(pin.workspace.accountId,email)
            override suspend fun workspace(id:String)=ProcessorLibraryWorkspace(id,"Metadata",1,true,false)
        });connection.approve(connection.review(pin.encode()),pin.documentSha256)
    }
    private inner class Source(root:ByteArray=head(),publisher:ByteArray=fixture["publisher"].asString.toByteArray(Charsets.UTF_8),
        sources:ByteArray=fixture["sources"].asString.toByteArray(Charsets.UTF_8)):ProcessorWorkspaceCapturedSource {
        override var connectionSha256=pin.documentSha256
        val selected=LibraryCurrentControl.read(root,pin)
        val bodies=mutableMapOf(checkNotNull(pin.currentFileId) to root,selected.publishers.reference.fileId to publisher,selected.sources.reference.fileId to sources)
        val captures=mutableListOf<String>();val reads=mutableListOf<String>()
        var transform:(String,DriveWorkspaceMetadata)->DriveWorkspaceMetadata={ _,metadata->metadata }
        var beforeBody:(suspend (String)->Unit)?=null
        override suspend fun capture(fileId:String):DriveWorkspaceMetadata {
            captures+=fileId;val body=bodies[fileId] ?: error("Unknown role/copy was requested.")
            return transform(fileId,DriveWorkspaceMetadata(fileId,"application/json",1,body.size.toLong(),listOf(pin.workspace.folderId),false,null,true))
        }
        override suspend fun readCaptured(expected:DriveWorkspaceMetadata):ByteArray { reads+=expected.id;beforeBody?.invoke(expected.id);return checkNotNull(bodies[expected.id]) }
        override suspend fun cursor():DriveWorkspaceCursor=error("Selected policies cannot scan account changes.")
        override suspend fun page(token:String?):DriveWorkspacePage=error("Selected policies cannot list/search copies.")
    }
    private fun reader(store:ProcessorLibraryStateStore,source:Source)=ProcessorCurrentControlReader(ProcessorCurrentControlObservations(store),source,pin)
    private fun DriveWorkspaceMetadata.changed(id:String=this.id,mimeType:String=this.mimeType,version:Long=this.version,bytes:Long?=this.bytes,
        parents:List<String> = this.parents,trashed:Boolean=this.trashed,sha256:String?=this.sha256,downloadable:Boolean?=this.downloadable,
        sharedDriveId:String?=this.sharedDriveId)=DriveWorkspaceMetadata(id,mimeType,version,bytes,parents,trashed,sha256,downloadable,sharedDriveId)
    private suspend fun rejected(action:suspend ()->Unit) { try { action();fail("Unverified policy capture became usable") } catch(_:IOException) { } catch(_:ProcessorProtocolException) { } }

    @Test fun exactSelectedPoliciesShareTheControlOwnerAndPreserveOwnedBytes()=runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);val captured=reader.capture();val policies=reader.readSelectedPolicies(captured)
            assertEquals(9L,policies.publishers.sequence);assertEquals(pin.libraryId,policies.sources.libraryId)
            assertEquals(listOf(pin.currentFileId,source.selected.publishers.reference.fileId,source.selected.sources.reference.fileId),source.reads)
            val original=policies.publisherEnvelope();val changed=policies.publisherEnvelope();changed[0]=0
            assertArrayEquals(original,policies.publisherEnvelope());assertArrayEquals(fixture["sources"].asString.toByteArray(Charsets.UTF_8),policies.sourceEnvelope())
            assertEquals("fenced",reader.fence(captured).phase);val count=source.captures.size
            rejected { reader.readSelectedPolicies(captured) };assertEquals(count,source.captures.size)
        } finally { reader.close();store.close() }
    }

    @Test fun policyMetadataCannotRetargetBodiesOrChangeTheirCorpusTypeOrDigest()=runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);val captured=reader.capture();val id=captured.head.publishers.reference.fileId
            val bad=listOf<(DriveWorkspaceMetadata)->DriveWorkspaceMetadata>(
                { it.changed(id="equal-copy") },{ it.changed(version=0) },{ it.changed(bytes=1) },{ it.changed(mimeType="image/jpeg") },
                { it.changed(parents=listOf("foreign")) },{ it.changed(sharedDriveId="foreign-corpus") },{ it.changed(downloadable=false) },
                { it.changed(trashed=true) },{ it.changed(sha256="0".repeat(64)) })
            for(change in bad) {
                source.transform={ file,metadata->if(file==id) change(metadata) else metadata }
                rejected { reader.readSelectedPolicies(captured) }
            }
            assertEquals(listOf(pin.currentFileId),source.reads)
        } finally { reader.close();store.close() }
    }

    @Test fun changedActualPolicyBytesCannotReachSemanticConsumers()=runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);val captured=reader.capture();val id=captured.head.publishers.reference.fileId
            val original=checkNotNull(source.bodies[id]);source.bodies[id]=original.copyOf().also { it[it.lastIndex]=(it.last()+1).toByte() }
            rejected { reader.readSelectedPolicies(captured) };assertFalse(source.reads.contains(captured.head.sources.reference.fileId))
            source.bodies[id]=original;assertEquals(9L,reader.readSelectedPolicies(captured).publishers.sequence)
        } finally { reader.close();store.close() }
    }

    @Test fun foreignPolicySignatureStopsBeforeAnotherRoleRequest()=runBlocking {
        val store=store();val source=Source(fixture["forgedPoliciesHead"].asString.toByteArray(Charsets.UTF_8),
            fixture["forgedPublisher"].asString.toByteArray(Charsets.UTF_8),fixture["forgedSources"].asString.toByteArray(Charsets.UTF_8));val reader=reader(store,source)
        try {
            approve(store);val captured=reader.capture();rejected { reader.readSelectedPolicies(captured) }
            assertFalse(source.captures.contains(captured.head.sources.reference.fileId))
        } finally { reader.close();store.close() }
    }

    @Test fun aRenewedSelectorDuringPolicyIoCannotReturnTheOldCapture()=runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);val captured=reader.capture()
            source.beforeBody={ id->if(id==captured.head.publishers.reference.fileId) approve(store,"renewed@example.invalid") }
            rejected { reader.readSelectedPolicies(captured) };assertFalse(source.reads.contains(captured.head.sources.reference.fileId))
        } finally { reader.close();store.close() }
    }

    @Test fun closeJoinsActualPolicyBodyAndRejectsItsLateResult()=runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);val captured=reader.capture();supervisorScope {
                val entered=CompletableDeferred<Unit>();val release=CompletableDeferred<Unit>()
                source.beforeBody={ id->if(id==captured.head.publishers.reference.fileId) withContext(NonCancellable) { entered.complete(Unit);release.await() } }
                val read=async { runCatching { reader.readSelectedPolicies(captured) } };withTimeout(5000) { entered.await() }
                val closing=async { reader.close() };kotlinx.coroutines.yield();assertFalse(closing.isCompleted)
                release.complete(Unit);assertTrue(withTimeout(5000) { read.await() }.exceptionOrNull() is IllegalStateException)
                withTimeout(5000) { closing.await() };assertFalse(source.reads.contains(captured.head.sources.reference.fileId))
            }
        } finally { reader.close();store.close() }
    }

    @Test fun cancellationJoinsActualPolicyBodyBeforeReturning()=runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);val captured=reader.capture();supervisorScope {
                val entered=CompletableDeferred<Unit>();val release=CompletableDeferred<Unit>()
                source.beforeBody={ id->if(id==captured.head.publishers.reference.fileId) withContext(NonCancellable) { entered.complete(Unit);release.await() } }
                val read=async { reader.readSelectedPolicies(captured) };withTimeout(5000) { entered.await() };read.cancel();assertFalse(read.isCompleted)
                release.complete(Unit);withTimeout(5000) { read.join() };assertTrue(read.isCancelled)
                assertFalse(source.reads.contains(captured.head.sources.reference.fileId))
            }
        } finally { reader.close();store.close() }
    }

    @Test fun anotherValidHeadCannotBeAttachedToTheCapturedNonce()=runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);val captured=reader.capture();val other=LibraryCurrentControl.read(fixture["valid"].asJsonArray[1].asJsonObject["envelope"].asString.toByteArray(Charsets.UTF_8),pin)
            val substituted=ProcessorControlCapture(captured.owner,captured.attempt,captured.metadata,other);val count=source.captures.size
            rejected { reader.assertCaptured(substituted) };rejected { reader.readSelectedPolicies(substituted) };rejected { reader.fence(substituted) }
            assertEquals(count,source.captures.size);reader.assertCaptured(captured)
        } finally { reader.close();store.close() }
    }
}

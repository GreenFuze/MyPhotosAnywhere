package com.myphotosanywhere.mpa.processor.protocol

import java.security.MessageDigest
import java.util.UUID
import java.util.Collections
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.withContext

/** An independently verified ancestry entry, never current-policy or activation authority.
 * Mutable transport bytes cannot rewrite the retained signed observation. */
class CatalogHistoryCommit internal constructor(val reference: CatalogObjectReference,envelope: ByteArray,
    verified: VerifiedProtocolDocument,val commit: CatalogCommit) {
    private val owned = envelope.copyOf()
    val verified: VerifiedProtocolDocument = verified.copy(requiredCapabilities=Collections.unmodifiableList(verified.requiredCapabilities.toList()))
    val envelope: ByteArray get() = owned.copyOf()
}

/** One captured publisher's entire signed predecessor stream, newest first. Every
 * immutable reference, signature, sequence and role is checked. Normal FULL collection
 * proves ancestry completion only; coverage/current authority/staged activation remain
 * separate. There is no channel or prefetch past the consumer's persistence callback.
 * Borrowed sources must join actual I/O before retiring their Google credentials. */
class CatalogCommitHistoryTraversal(private val workspace: CatalogWorkspace,private val publisher: TrustedProtocolActor,
    private val source: CatalogObjectSource,private val maximumCommits: Int = MAX_COMMITS) {
    private val schema = CatalogPublicationReader(workspace)
    private val signed = SignedProtocolDocuments(setOf(CatalogPublicationReader.CAPABILITY,DriveCatalogFacts.CAPABILITY))
    init { require(maximumCommits in 1..MAX_COMMITS) }

    fun read(head: CatalogObjectReference): Flow<CatalogHistoryCommit> = flow {
        val commits = hashSetOf<String>();val dependencies = hashSetOf<String>();val generations = hashSetOf<UUID>()
        var reference: CatalogObjectReference? = head;var expectedSequence: Long? = null
        while(reference != null) {
            currentCoroutineContext().ensureActive();val captured = reference
            validate(captured)
            if(captured.fileId in dependencies || !commits.add(captured.fileId)) throw ProcessorProtocolException("Catalog history contains a cycle or an aliased document role.")
            if(commits.size > maximumCommits) throw CatalogTraversalLimitException()
            val borrowed = source.read(captured);currentCoroutineContext().ensureActive()
            val entry = withContext(Dispatchers.Default) {
                if(borrowed.size.toLong() != captured.bytes) throw ProcessorProtocolException("Catalog predecessor length differs from its immutable reference.")
                val envelope = borrowed.copyOf()
                val expected = ByteArray(32) { index -> captured.sha256.substring(index * 2,index * 2 + 2).toInt(16).toByte() }
                if(!MessageDigest.isEqual(MessageDigest.getInstance("SHA-256").digest(envelope),expected)) throw ProcessorProtocolException("Catalog predecessor digest differs from its immutable reference.")
                val verified = signed.verifyAndRead(envelope,publisher);val commit = schema.readCommit(verified)
                if(DriveCatalogFacts.CAPABILITY !in verified.requiredCapabilities) throw ProcessorProtocolException("Catalog ancestry requires observed Drive facts capability.")
                currentCoroutineContext().ensureActive();CatalogHistoryCommit(captured,envelope,verified,commit)
            }
            val verified = entry.verified;val commit = entry.commit
            if(verified.sequence > maximumCommits) throw CatalogTraversalLimitException()
            if(expectedSequence != null && verified.sequence != expectedSequence || !generations.add(commit.generationId)) throw ProcessorProtocolException("Catalog ancestry changed its stream sequence or reused a generation identity.")
            if(commit.rootIndex.fileId in commits || commit.sourceCoverage.fileId in commits ||
                commit.parent?.let { it.fileId in commits || it.fileId in dependencies } == true) throw ProcessorProtocolException("Catalog ancestry aliases a commit with its dependencies or predecessor.")
            dependencies.add(commit.rootIndex.fileId);dependencies.add(commit.sourceCoverage.fileId)
            currentCoroutineContext().ensureActive();emit(entry);currentCoroutineContext().ensureActive()
            expectedSequence = verified.sequence - 1;reference = commit.parent
        }
    }
    private fun validate(reference: CatalogObjectReference) {
        CatalogPublicationReader.checkOpaque(reference.fileId)
        if(workspace.folderId == "root" || reference.accountId != workspace.accountId || reference.workspaceId != workspace.folderId || reference.fileId == "root" || reference.fileId == workspace.folderId ||
            reference.bytes !in 1..ProtocolDocumentReader.MAX_DOCUMENT_BYTES.toLong() || !reference.sha256.matches(Regex("[0-9a-f]{64}"))) throw ProcessorProtocolException("Catalog history reference is outside its captured workspace or byte bound.")
    }
    companion object { const val MAX_COMMITS = 4096 }
}

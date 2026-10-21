package com.myphotosanywhere.mpa.processor.protocol

import java.io.IOException
import java.security.MessageDigest
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.withContext

/** The borrowed Drive adapter must check actual account/workspace membership before
 * downloading and retain its connection owner until the read actually completes. */
fun interface CatalogObjectSource {
    suspend fun read(reference: CatalogObjectReference): ByteArray
}
data class CatalogFactsLeaf(val prefix: String, val reference: CatalogChunkReference, val facts: DriveCatalogChunk)
class CatalogTraversalLimitException : IOException("This catalog exceeds the configured traversal budget and cannot be accepted by this reader.")

/** Streams only into staging: a later failure may follow already emitted leaves. Full
 * normal collection proves this walk completed, not source coverage, ancestor agreement,
 * enrolled authority, policy fences or transactional generation activation. */
class CatalogFactsTraversal(private val library: UUID, workspace: CatalogWorkspace, private val source: CatalogObjectSource,
    private val objectBudget: Int = MAX_OBJECT_BUDGET) {
    private val schema = CatalogPublicationReader(workspace)
    private val facts = DriveCatalogFacts(library)
    init { require(objectBudget in 1..MAX_OBJECT_BUDGET) }

    fun readLeaves(verified: VerifiedProtocolDocument): Flow<CatalogFactsLeaf> = flow {
        currentCoroutineContext().ensureActive()
        if (verified.libraryId != library || DriveCatalogFacts.CAPABILITY !in verified.requiredCapabilities)
            throw ProcessorProtocolException("The approved catalog must name this library and the observed Drive facts capability.")
        val commit = withContext(Dispatchers.Default) { schema.readCommit(verified) }
        val visited = HashSet<String>()
        fun reserve(reference: CatalogObjectReference) {
            if (!visited.add(reference.fileId)) throw ProcessorProtocolException("Catalog graph contains an aliased object, role or cycle.")
            if (visited.size > objectBudget) throw CatalogTraversalLimitException()
        }
        reserve(commit.sourceCoverage); commit.parent?.let(::reserve); reserve(commit.rootIndex)
        val pending = ArrayDeque<Pending>(); pending.addLast(Pending("", commit.rootIndex, null))
        while (pending.isNotEmpty()) {
            currentCoroutineContext().ensureActive(); val entry = pending.removeLast()
            if (entry.chunk != null) {
                val chunk = readChunk(entry.chunk, entry.prefix)
                currentCoroutineContext().ensureActive(); emit(CatalogFactsLeaf(entry.prefix, entry.chunk, chunk))
            } else {
                val node = readIndex(entry.reference, entry.prefix)
                node.children.asReversed().forEach { child ->
                    reserve(child.reference); pending.addLast(Pending(entry.prefix + child.edge, child.reference, child.chunk))
                }
            }
        }
        currentCoroutineContext().ensureActive()
    }

    private suspend fun readIndex(reference: CatalogObjectReference, prefix: String): CatalogIndexNode = withContext(Dispatchers.Default) {
        val borrowed = source.read(reference); currentCoroutineContext().ensureActive()
        if (borrowed.size.toLong() != reference.bytes || borrowed.size > CatalogPublicationReader.MAX_INDEX_BYTES)
            throw ProcessorProtocolException("Catalog index does not match its bounded immutable length.")
        val owned = borrowed.copyOf()
        if (!MessageDigest.isEqual(MessageDigest.getInstance("SHA-256").digest(owned), hex(reference.sha256)))
            throw ProcessorProtocolException("Catalog index does not match its immutable SHA-256.")
        val node = schema.readIndex(owned, prefix); currentCoroutineContext().ensureActive(); node
    }
    private suspend fun readChunk(reference: CatalogChunkReference, prefix: String): DriveCatalogChunk = withContext(Dispatchers.Default) {
        val compressed = source.read(reference.encoded)
        val canonical = CatalogChunkDecoder().decode(compressed, reference)
        val chunk = facts.read(canonical, prefix); currentCoroutineContext().ensureActive(); chunk
    }
    private fun hex(value: String) = ByteArray(value.length / 2) { index -> value.substring(index * 2, index * 2 + 2).toInt(16).toByte() }
    private data class Pending(val prefix: String, val reference: CatalogObjectReference, val chunk: CatalogChunkReference?)
    companion object { const val MAX_OBJECT_BUDGET = 65536 }
}

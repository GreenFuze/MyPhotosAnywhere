package com.myphotosanywhere.mpa.processor.protocol

import com.google.gson.JsonArray
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import java.security.MessageDigest
import java.util.Collections
import java.util.UUID

class HumanFieldOperation(
    val id: UUID,
    val actorId: UUID,
    val sequence: Long,
    val targetId: UUID,
    val field: String,
    parents: List<UUID>,
    value: ByteArray,
) {
    val parents: List<UUID>
    val canonicalValue: String
    val fingerprint: String

    init {
        if (id == ZERO || actorId == ZERO || targetId == ZERO || sequence < 1) throw ProcessorProtocolException("Invalid operation identity/sequence.")
        if (!field.matches(Regex("[A-Za-z0-9._-]{1,64}"))) throw ProcessorProtocolException("Invalid field identifier.")
        if (parents.size > 64 || parents.any { it == ZERO || it == id } || parents.toSet().size != parents.size)
            throw ProcessorProtocolException("Invalid causal parent set.")
        this.parents = Collections.unmodifiableList(parents.sortedBy(UUID::toString))
        canonicalValue = CanonicalProtocolJson(64 * 1024).encode(value).toString(Charsets.UTF_8)
        val identity = JsonObject().apply {
            addProperty("id", id.toString()); addProperty("actorId", actorId.toString()); addProperty("sequence", sequence.toString())
            addProperty("targetId", targetId.toString()); addProperty("field", field)
            add("parents", JsonArray().apply { this@HumanFieldOperation.parents.forEach { add(it.toString()) } })
            add("value", JsonParser.parseString(canonicalValue))
        }
        fingerprint = MessageDigest.getInstance("SHA-256").digest(CanonicalProtocolJson(256 * 1024).encode(identity.toString().toByteArray(Charsets.UTF_8)))
            .joinToString("") { "%02x".format(it.toInt() and 255) }
    }

    companion object { private val ZERO = UUID(0, 0) }
}

data class CausalFieldSnapshot(
    val alternatives: List<HumanFieldOperation>,
    val commonPriorValues: List<HumanFieldOperation>,
    val pendingIds: List<UUID>,
) { val hasConflict: Boolean get() = alternatives.size > 1 }

/** Serially owned one-field projection. Persistence/outbox/authority belong to its transactional owner. */
class CausalFieldRegister(private val target: UUID, private val field: String) {
    private var operations = HashMap<UUID, HumanFieldOperation>()
    var snapshot = CausalFieldSnapshot(emptyList(), emptyList(), emptyList())
        private set

    init { require(target != UUID(0, 0) && field.isNotBlank()) }

    fun apply(operation: HumanFieldOperation): Boolean {
        if (operation.targetId != target || operation.field != field) throw ProcessorProtocolException("Operation belongs to another field.")
        operations[operation.id]?.let { prior ->
            if (prior.fingerprint != operation.fingerprint) throw ProcessorProtocolException("Operation ID was reused with different content.")
            return false
        }
        if (operations.size == MAX_OPERATIONS) throw ProcessorProtocolException("Field history requires acknowledged compaction.")
        if (operations.values.any { it.actorId == operation.actorId && it.sequence == operation.sequence })
            throw ProcessorProtocolException("Actor sequence was reused.")
        val candidate = HashMap(operations).apply { put(operation.id, operation) }
        val next = evaluate(candidate)
        operations = candidate
        snapshot = next
        return true
    }

    private fun evaluate(candidate: Map<UUID, HumanFieldOperation>): CausalFieldSnapshot {
        val remaining = candidate.mapValues { (_, operation) -> operation.parents.count { it in candidate } }.toMutableMap()
        val children = candidate.keys.associateWith { ArrayList<UUID>() }
        candidate.values.forEach { operation ->
            operation.parents.forEach { parentId -> candidate[parentId]?.let { parent ->
                if (parent.actorId == operation.actorId && parent.sequence >= operation.sequence)
                    throw ProcessorProtocolException("A same-actor parent must precede its child.")
                children.getValue(parentId).add(operation.id)
            } }
        }
        val ready = ArrayDeque(remaining.filterValues { it == 0 }.keys)
        val active = HashSet<UUID>()
        var seen = 0
        while (ready.isNotEmpty()) {
            val id = ready.removeFirst()
            seen++
            if (candidate.getValue(id).parents.all { it in active }) active.add(id)
            children.getValue(id).forEach { child ->
                val count = remaining.getValue(child) - 1
                remaining[child] = count
                if (count == 0) ready.addLast(child)
            }
        }
        if (seen != candidate.size) throw ProcessorProtocolException("Causal parent cycle.")
        val frontier = active.toMutableSet()
        active.forEach { frontier.removeAll(candidate.getValue(it).parents.toSet()) }
        var common: MutableSet<UUID>? = null
        frontier.forEach { id ->
            val ancestors = ancestors(id, candidate)
            val previous = common
            if (previous == null) common = ancestors else previous.retainAll(ancestors)
        }
        val prior = common.orEmpty().toMutableSet()
        common.orEmpty().forEach { prior.removeAll(ancestors(it, candidate)) }
        fun values(ids: Set<UUID>) = Collections.unmodifiableList(ids.sortedBy(UUID::toString).map(candidate::getValue))
        return CausalFieldSnapshot(values(frontier), values(prior),
            Collections.unmodifiableList(candidate.keys.filter { it !in active }.sortedBy(UUID::toString)))
    }

    private fun ancestors(id: UUID, candidate: Map<UUID, HumanFieldOperation>): MutableSet<UUID> {
        val found = HashSet<UUID>()
        val pending = ArrayDeque(candidate.getValue(id).parents)
        while (pending.isNotEmpty()) {
            val parent = pending.removeLast()
            if (found.add(parent)) pending.addAll(candidate.getValue(parent).parents)
        }
        return found
    }

    companion object { const val MAX_OPERATIONS = 4096 }
}

package com.myphotosanywhere.mpa

import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.CausalFieldRegister
import com.myphotosanywhere.mpa.processor.protocol.HumanFieldOperation
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.UUID

class ProcessorProtocolCausalityTest {
    @Test fun sharedOperationFingerprintsMatchIndependentPythonReference() {
        val fixture = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/causality.json"))
            .bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
        fixture.getAsJsonArray("operations").forEach { item ->
            val row = item.asJsonObject
            val body = row.getAsJsonObject("body")
            val operation = HumanFieldOperation(UUID.fromString(body["id"].asString), UUID.fromString(body["actorId"].asString),
                body["sequence"].asString.toLong(), UUID.fromString(body["targetId"].asString), body["field"].asString,
                body.getAsJsonArray("parents").map { UUID.fromString(it.asString) }, body["value"].toString().toByteArray(Charsets.UTF_8))
            assertEquals(row["fingerprint"].asString, operation.fingerprint)
        }
    }

    private val target = UUID.fromString("94a81164-e980-4ec1-a566-cec36475b839")
    private val actorA = UUID.fromString("11111111-1111-4111-8111-111111111111")
    private val actorB = UUID.fromString("22222222-2222-4222-8222-222222222222")

    private fun edit(id: Int, actor: UUID, sequence: Long, parents: List<UUID>, value: String = "\"name\"") =
        HumanFieldOperation(UUID.fromString("00000000-0000-4000-8000-${id.toString().padStart(12, '0')}"), actor, sequence, target,
            "person.name", parents, value.toByteArray(Charsets.UTF_8))

    @Test fun everyDeliveryOrderPreservesConflictAndLateEditsReopenResolution() {
        val root = edit(1, actorA, 1, emptyList(), "\"original\"")
        val left = edit(2, actorA, 2, listOf(root.id), "\"Alice\"")
        val right = edit(3, actorB, 1, listOf(root.id), "\"Alicia\"")
        listOf(listOf(root, left, right), listOf(root, right, left), listOf(left, root, right), listOf(left, right, root),
            listOf(right, root, left), listOf(right, left, root)).forEach { order ->
            val register = CausalFieldRegister(target, "person.name")
            order.forEach { register.apply(it) }
            assertEquals(listOf(left.id, right.id), register.snapshot.alternatives.map { it.id })
            assertEquals(root.id, register.snapshot.commonPriorValues.single().id)
            val resolution = edit(4, actorA, 3, listOf(left.id, right.id), "\"chosen\"")
            register.apply(resolution)
            assertFalse(register.snapshot.hasConflict)
            val late = edit(5, actorB, 2, listOf(right.id), "\"offline\"")
            register.apply(late)
            assertTrue(register.snapshot.hasConflict)
            assertFalse(register.apply(late))
            assertThrows(ProcessorProtocolException::class.java) { register.apply(edit(5, actorB, 2, listOf(right.id), "\"tampered\"")) }
        }
    }

    @Test fun missingParentsStayPendingAndCyclesNeverReplaceAcceptedState() {
        val root = edit(1, actorA, 1, emptyList())
        val future = edit(3, actorA, 3, listOf(edit(4, actorB, 1, emptyList()).id))
        val register = CausalFieldRegister(target, "person.name")
        register.apply(future)
        assertTrue(register.snapshot.alternatives.isEmpty())
        assertEquals(listOf(future.id), register.snapshot.pendingIds)
        register.apply(root)
        assertThrows(ProcessorProtocolException::class.java) { register.apply(edit(4, actorB, 1, listOf(future.id))) }
        assertThrows(ProcessorProtocolException::class.java) { register.apply(edit(2, actorA, 1, emptyList())) }
        assertEquals(root.id, register.snapshot.alternatives.single().id)
        assertEquals(listOf(future.id), register.snapshot.pendingIds)
    }
}

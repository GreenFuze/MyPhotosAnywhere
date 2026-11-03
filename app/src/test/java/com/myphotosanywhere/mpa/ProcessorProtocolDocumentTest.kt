package com.myphotosanywhere.mpa

import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolCompatibilityException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentReader
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentSigner
import com.myphotosanywhere.mpa.processor.protocol.TrustedProtocolActor
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.UUID

class ProcessorProtocolDocumentTest {
    @Test fun scopeAndCapabilitiesAreRequiredAndOptionalFieldsRemainSigned() {
        val key = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        val signer = ProtocolDocumentSigner(key.private, key.public)
        val actor = TrustedProtocolActor(UUID.fromString(LIBRARY), UUID.fromString(ACTOR), 1, key.public.encoded, setOf("human-operation"))
        val reader = ProtocolDocumentReader(setOf("human.fields.v1"))
        val body = BODY.toByteArray(Charsets.UTF_8)
        val document = reader.verifyAndRead(body, signer.sign(body), actor)
        assertEquals(Long.MAX_VALUE, document.sequence)
        assertEquals(5, document.minor)
        assertEquals("{\"value\":\"שלום\"}", document.canonicalPayload)
        assertThrows(ProtocolCompatibilityException::class.java) { ProtocolDocumentReader(emptySet()).verifyAndRead(body, signer.sign(body), actor) }
        assertThrows(ProcessorProtocolException::class.java) { reader.verifyAndRead(BODY.replace("true", "false").toByteArray(), signer.sign(body), actor) }
        listOf(BODY.replace("\"epoch\":\"1\"", "\"epoch\":\"2\""), BODY.replace("human-operation", "catalog-commit"), BODY.replace(ACTOR, LIBRARY)).forEach { changed ->
            val bytes = changed.toByteArray(Charsets.UTF_8)
            assertThrows(ProcessorProtocolException::class.java) { reader.verifyAndRead(bytes, signer.sign(bytes), actor) }
        }
        listOf("0", "01", "+1", "-1", "9223372036854775808", "١").forEach { counter ->
            val bytes = BODY.replace("9223372036854775807", counter).toByteArray(Charsets.UTF_8)
            assertThrows(ProcessorProtocolException::class.java) { reader.verifyAndRead(bytes, signer.sign(bytes), actor) }
        }
        val incompatible = BODY.replace("\"major\":1", "\"major\":2").toByteArray()
        assertThrows(ProtocolCompatibilityException::class.java) { reader.verifyAndRead(incompatible, signer.sign(incompatible), actor) }
    }

    companion object {
        private const val LIBRARY = "94a81164-e980-4ec1-a566-cec36475b839"
        private const val ACTOR = "11111111-1111-4111-8111-111111111111"
        private const val BODY = """{"format":"mpa","major":1,"minor":5,"libraryId":"94a81164-e980-4ec1-a566-cec36475b839","actorId":"11111111-1111-4111-8111-111111111111","epoch":"1","sequence":"9223372036854775807","kind":"human-operation","requiredCapabilities":["human.fields.v1"],"payload":{"value":"שלום"},"futureOptional":true}"""
    }
}

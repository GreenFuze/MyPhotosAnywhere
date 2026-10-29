package com.myphotosanywhere.mpa

import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.DocumentSignature
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentSigner
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentVerifier
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.Base64

class ProcessorProtocolSignatureTest {
    @Test fun dotNetSignatureVerifiesAndTamperingFails() {
        val fixture = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/signature.json"))
            .bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
        val key = Base64.getDecoder().decode(fixture["publicKeySpki"].asString)
        val verifier = ProtocolDocumentVerifier(key)
        val signature = DocumentSignature(fixture["Algorithm"].asString, fixture["KeyId"].asString, fixture["Value"].asString)
        val body = fixture["body"].asString.toByteArray(Charsets.UTF_8)
        assertTrue(verifier.verify(body, signature))
        assertFalse(verifier.verify(fixture["body"].asString.replace("שלום", "changed").toByteArray(), signature))
        assertFalse(verifier.verify("{}".toByteArray(), signature))
        assertThrows(ProcessorProtocolException::class.java) { ProtocolDocumentVerifier(key + byteArrayOf(0)) }
        assertThrows(ProcessorProtocolException::class.java) { verifier.verify(body, signature.copy(value = signature.value + "==")) }
        assertThrows(ProcessorProtocolException::class.java) { verifier.verify(body, signature.copy(algorithm = "none")) }
    }

    @Test fun localProviderDerConversionRoundTripsAndUnknownKeysFail() {
        val generator = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }
        val pair = generator.generateKeyPair()
        val other = generator.generateKeyPair()
        val signer = ProtocolDocumentSigner(pair.private, pair.public)
        val verifier = ProtocolDocumentVerifier(pair.public.encoded)
        // Exercise both sign-padding lengths across provider-produced r/s values.
        repeat(64) { index ->
            val document = "{\"index\":$index,\"caption\":\"שלום\"}".toByteArray(Charsets.UTF_8)
            val signature = signer.sign(document)
            assertTrue(verifier.verify(document, signature))
            assertFalse(ProtocolDocumentVerifier(other.public.encoded).verify(document, signature))
            assertFalse(verifier.verify("{\"index\":-1}".toByteArray(), signature))
        }
        assertThrows(IllegalArgumentException::class.java) { ProtocolDocumentSigner(pair.private, other.public) }
        generator.initialize(ECGenParameterSpec("secp384r1"))
        assertThrows(ProcessorProtocolException::class.java) { ProtocolDocumentVerifier(generator.generateKeyPair().public.encoded) }
    }
}

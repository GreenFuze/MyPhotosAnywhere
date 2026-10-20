package com.myphotosanywhere.mpa.processor.protocol

import java.math.BigInteger
import java.security.AlgorithmParameters
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.PublicKey
import java.security.Signature
import java.security.SignatureException
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.InvalidKeySpecException
import java.security.spec.X509EncodedKeySpec
import java.util.Base64

data class DocumentSignature(val algorithm: String, val keyId: String, val value: String)

/** Caller owns the key lease. Signing does not itself authorize an actor or an original-file action. */
class ProtocolDocumentSigner(
    private val privateKey: PrivateKey,
    publicKey: PublicKey,
    documentLimit: Int = 256 * 1024,
) {
    private val codec = CanonicalProtocolJson(documentLimit)
    val keyId: String = SignatureEncoding.keyId(SignatureEncoding.requireP256(publicKey))

    init {
        // Check the borrowed private/public pair without exporting private key material.
        val challenge = "mpa:signer-key-pair:1".toByteArray(Charsets.UTF_8)
        val signer = Signature.getInstance("SHA256withECDSA")
        signer.initSign(privateKey)
        signer.update(challenge)
        val proof = signer.sign()
        val verifier = Signature.getInstance("SHA256withECDSA")
        verifier.initVerify(publicKey)
        verifier.update(challenge)
        require(verifier.verify(proof)) { "Signing key does not match its public key." }
    }

    fun sign(document: ByteArray): DocumentSignature {
        val signer = Signature.getInstance("SHA256withECDSA")
        signer.initSign(privateKey)
        signer.update(codec.encode(document))
        val wire = SignatureEncoding.fromDer(signer.sign())
        return DocumentSignature("ES256", keyId, SignatureEncoding.encode(wire))
    }
}

/** Imports public material only. Pairing must independently authorize this key for a library/epoch. */
class ProtocolDocumentVerifier(publicKeyInfo: ByteArray, documentLimit: Int = 256 * 1024) {
    private val codec = CanonicalProtocolJson(documentLimit)
    private val key: ECPublicKey
    val keyId: String

    init {
        if (publicKeyInfo.isEmpty() || publicKeyInfo.size > 512) throw ProcessorProtocolException("Invalid public-key byte bound.")
        key = try {
            SignatureEncoding.requireP256(KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(publicKeyInfo)))
        } catch (error: InvalidKeySpecException) {
            throw ProcessorProtocolException("Invalid public-key encoding.", error)
        }
        if (!key.encoded.contentEquals(publicKeyInfo)) throw ProcessorProtocolException("Non-canonical or trailing public-key data.")
        keyId = SignatureEncoding.keyId(key)
    }

    fun verify(document: ByteArray, signature: DocumentSignature): Boolean {
        if (signature.algorithm != "ES256" || !signature.keyId.matches(Regex("[0-9a-f]{64}")))
            throw ProcessorProtocolException("Unsupported signature/key encoding.")
        val wire = SignatureEncoding.decode(signature.value)
        if (!MessageDigest.isEqual(signature.keyId.toByteArray(Charsets.US_ASCII), keyId.toByteArray(Charsets.US_ASCII))) return false
        val canonical = codec.encode(document)
        val r = BigInteger(1, wire.copyOfRange(0, 32))
        val s = BigInteger(1, wire.copyOfRange(32, 64))
        if (r.signum() == 0 || s.signum() == 0 || r >= key.params.order || s >= key.params.order) return false
        val verifier = Signature.getInstance("SHA256withECDSA")
        verifier.initVerify(key)
        verifier.update(canonical)
        return try { verifier.verify(SignatureEncoding.toDer(wire)) }
            catch (_: SignatureException) { false }
    }
}

internal object SignatureEncoding {
    private val reference: ECParameterSpec = AlgorithmParameters.getInstance("EC").apply {
        init(ECGenParameterSpec("secp256r1"))
    }.getParameterSpec(ECParameterSpec::class.java)

    fun requireP256(key: PublicKey): ECPublicKey {
        val ec = key as? ECPublicKey ?: throw ProcessorProtocolException("Protocol signatures require an EC public key.")
        val parameters = ec.params
        if (parameters.curve != reference.curve || parameters.generator != reference.generator ||
            parameters.order != reference.order || parameters.cofactor != reference.cofactor)
            throw ProcessorProtocolException("Protocol signatures require NIST P-256.")
        return ec
    }

    fun keyId(key: ECPublicKey): String = MessageDigest.getInstance("SHA-256").digest(key.encoded)
        .joinToString("") { "%02x".format(it.toInt() and 255) }

    fun encode(bytes: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)

    fun decode(value: String): ByteArray {
        if (!value.matches(Regex("[A-Za-z0-9_-]{86}"))) throw ProcessorProtocolException("Invalid signature encoding.")
        val bytes = Base64.getUrlDecoder().decode(value)
        if (bytes.size != 64 || encode(bytes) != value) throw ProcessorProtocolException("Non-canonical signature encoding.")
        return bytes
    }

    fun toDer(wire: ByteArray): ByteArray {
        check(wire.size == 64) { "Invalid P-256 wire signature length." }
        val r = BigInteger(1, wire.copyOfRange(0, 32)).toByteArray()
        val s = BigInteger(1, wire.copyOfRange(32, 64)).toByteArray()
        return byteArrayOf(0x30, (4 + r.size + s.size).toByte(), 2, r.size.toByte()) + r + byteArrayOf(2, s.size.toByte()) + s
    }

    fun fromDer(der: ByteArray): ByteArray {
        check(der.size in 8..72 && der[0] == 0x30.toByte() && der[1].toInt() == der.size - 2) { "Invalid provider signature sequence." }
        var position = 2
        fun integer(): ByteArray {
            check(position + 2 <= der.size && der[position++].toInt() == 2) { "Invalid provider signature integer." }
            val length = der[position++].toInt() and 255
            check(length in 1..33 && position + length <= der.size) { "Invalid provider signature integer length." }
            val value = der.copyOfRange(position, position + length)
            position += length
            check(value[0].toInt() >= 0 && !(length > 1 && value[0].toInt() == 0 && value[1].toInt() >= 0)) { "Non-canonical provider signature integer." }
            val number = BigInteger(value)
            check(number.signum() > 0 && number < reference.order) { "Provider returned out-of-range signature." }
            val magnitude = if (length == 33) value.copyOfRange(1, 33) else value
            return ByteArray(32 - magnitude.size) + magnitude
        }
        val result = integer() + integer()
        check(position == der.size) { "Trailing provider signature bytes." }
        return result
    }
}

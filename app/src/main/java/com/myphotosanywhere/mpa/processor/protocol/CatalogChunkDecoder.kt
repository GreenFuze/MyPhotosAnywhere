package com.myphotosanywhere.mpa.processor.protocol

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.zip.CRC32
import java.util.zip.GZIPInputStream
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

/** Owns one bounded in-memory decode on a background dispatcher. It borrows the caller's
 * input, snapshots it before verification, and never activates a catalog or writes a file. */
class CatalogChunkDecoder(
    private val compressedLimit: Int = CatalogPublicationReader.MAX_COMPRESSED_BYTES,
    private val expandedLimit: Int = CatalogPublicationReader.MAX_EXPANDED_BYTES,
) {
    init {
        require(compressedLimit in 1..CatalogPublicationReader.MAX_COMPRESSED_BYTES)
        require(expandedLimit in 1..CatalogPublicationReader.MAX_EXPANDED_BYTES)
    }

    suspend fun decode(compressed: ByteArray, expected: CatalogChunkReference): ByteArray = withContext(Dispatchers.Default) {
        currentCoroutineContext().ensureActive()
        if (compressed.isEmpty() || compressed.size > compressedLimit || expected.encoded.bytes != compressed.size.toLong() ||
            expected.expandedBytes !in 1..expandedLimit.toLong())
            throw ProcessorProtocolException("Catalog chunk exceeds its transport or expanded bound.")
        checkDigest(expected.encoded.sha256); checkDigest(expected.expandedSha256)
        val snapshot = compressed.copyOf()
        verify(snapshot, expected.encoded.sha256)
        val decoded = try {
            ByteArrayInputStream(snapshot).use { source ->
                GZIPInputStream(source).use { inflater ->
                    ByteArrayOutputStream(expected.expandedBytes.toInt()).use { output ->
                        val buffer = ByteArray(64 * 1024)
                        while (true) {
                            currentCoroutineContext().ensureActive()
                            val remaining = expected.expandedBytes - output.size() + 1
                            val count = inflater.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
                            if (count < 0) break
                            if (output.size() + count > expected.expandedBytes)
                                throw ProcessorProtocolException("Catalog decompression exceeds its declared bound.")
                            output.write(buffer, 0, count)
                        }
                        if (output.size().toLong() != expected.expandedBytes)
                            throw ProcessorProtocolException("Catalog expanded bytes are truncated.")
                        output.toByteArray()
                    }
                }
            }
        } catch (error: IOException) {
            throw ProcessorProtocolException("Malformed compressed catalog.", error)
        }
        currentCoroutineContext().ensureActive()
        verify(decoded, expected.expandedSha256)
        validateFooter(snapshot, decoded)
        if (!decoded.contentEquals(CanonicalProtocolJson().encode(decoded)))
            throw ProcessorProtocolException("Expanded catalog bytes must already be canonical JSON.")
        currentCoroutineContext().ensureActive()
        decoded
    }

    private fun checkDigest(value: String) {
        if (value.length != 64 || !value.all { it in '0'..'9' || it in 'a'..'f' })
            throw ProcessorProtocolException("Catalog digest must be lowercase SHA-256.")
    }
    private fun verify(bytes: ByteArray, expected: String) {
        val digest = ByteArray(32) { index -> expected.substring(index * 2, index * 2 + 2).toInt(16).toByte() }
        if (!MessageDigest.isEqual(MessageDigest.getInstance("SHA-256").digest(bytes), digest))
            throw ProcessorProtocolException("Catalog bytes do not match their declared SHA-256.")
    }
    private suspend fun validateFooter(compressed: ByteArray, expanded: ByteArray) {
        if (compressed.size < 18 || compressed[0] != 0x1f.toByte() || compressed[1] != 0x8b.toByte() || compressed[2] != 8.toByte())
            throw ProcessorProtocolException("Catalog gzip footer is missing.")
        val footer = ByteBuffer.wrap(compressed, compressed.size - 8, 8).order(ByteOrder.LITTLE_ENDIAN)
        val declaredCrc = footer.int.toLong() and 0xffffffffL
        val length = footer.int.toLong() and 0xffffffffL
        if (length != expanded.size.toLong()) throw ProcessorProtocolException("Catalog gzip footer has an invalid expanded length.")
        val crc = CRC32()
        var offset = 0
        while (offset < expanded.size) {
            currentCoroutineContext().ensureActive()
            val count = minOf(64 * 1024, expanded.size - offset)
            crc.update(expanded, offset, count); offset += count
        }
        if (crc.value != declaredCrc) throw ProcessorProtocolException("Catalog gzip footer does not match its complete expanded body.")
    }
}

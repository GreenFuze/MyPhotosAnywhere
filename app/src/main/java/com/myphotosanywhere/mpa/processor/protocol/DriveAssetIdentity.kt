package com.myphotosanywhere.mpa.processor.protocol

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.UUID

class DriveAssetIdentity(private val library: UUID) {
    init { require(library != UUID(0, 0)) { "Library identity is required." } }

    fun derive(accountPermissionId: String, fileId: String): UUID {
        checkId(accountPermissionId)
        checkId(fileId)
        val account = accountPermissionId.toByteArray(Charsets.UTF_8)
        val file = fileId.toByteArray(Charsets.UTF_8)
        val framed = ByteArrayOutputStream().use { output ->
            output.write("mpa:drive-representation:1\u0000".toByteArray(Charsets.UTF_8))
            output.write(ByteBuffer.allocate(16).order(ByteOrder.BIG_ENDIAN).putLong(library.mostSignificantBits).putLong(library.leastSignificantBits).array())
            output.write(ByteBuffer.allocate(4).order(ByteOrder.BIG_ENDIAN).putInt(account.size).array())
            output.write(account)
            output.write(ByteBuffer.allocate(4).order(ByteOrder.BIG_ENDIAN).putInt(file.size).array())
            output.write(file)
            output.toByteArray()
        }
        val digest = MessageDigest.getInstance("SHA-256").digest(framed)
        digest[6] = ((digest[6].toInt() and 15) or 0x80).toByte()
        digest[8] = ((digest[8].toInt() and 63) or 0x80).toByte()
        val bytes = ByteBuffer.wrap(digest).order(ByteOrder.BIG_ENDIAN)
        return UUID(bytes.long, bytes.long)
    }

    private fun checkId(value: String) {
        if (value.length !in 1..256 || value.any { it !in 'a'..'z' && it !in 'A'..'Z' && it !in '0'..'9' && it != '-' && it != '_' })
            throw ProcessorProtocolException("Account/file identity must be an opaque Drive identity, not an email or path.")
    }
}

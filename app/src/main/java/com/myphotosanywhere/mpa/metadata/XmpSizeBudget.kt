package com.myphotosanywhere.mpa.metadata

import java.io.ByteArrayOutputStream

internal class XmpSizeLimitException : IllegalArgumentException("XMP exceeds the 2 MiB limit.")

/** Counts UTF-8 without allocating an encoded copy of possibly oversized input. */
internal class XmpSizeBudget(private val maximum: Int=XmpCodec.MAX_BYTES) {
    private var used=0
    init { require(maximum>=0) }
    fun reserve(bytes: Int) {
        require(bytes>=0)
        if(bytes>maximum-used) throw XmpSizeLimitException()
        used+=bytes
    }
    fun add(text: String) {
        if(text.length>maximum-used) throw XmpSizeLimitException()
        var index=0
        while(index<text.length) {
            val char=text[index++]
            val bytes=when {
                char.code<0x80 -> 1
                char.code<0x800 -> 2
                char.isHighSurrogate() -> {
                    require(index<text.length && text[index].isLowSurrogate()) { "Invalid XMP Unicode." }
                    index++;4
                }
                char.isLowSurrogate() -> throw IllegalArgumentException("Invalid XMP Unicode.")
                else -> 3
            }
            reserve(bytes)
        }
    }
}

/** The XML transformer cannot grow its output past the document byte limit. */
internal class BoundedXmpOutput : ByteArrayOutputStream(4096) {
    private val budget=XmpSizeBudget()
    override fun write(value: Int) {
        budget.reserve(1);super.write(value)
    }
    override fun write(bytes: ByteArray,offset: Int,length: Int) {
        if(offset<0 || length<0 || offset>bytes.size-length) throw IndexOutOfBoundsException()
        budget.reserve(length);super.write(bytes,offset,length)
    }
}

package com.myphotosanywhere.mpa.metadata

import org.junit.Assert.*
import org.junit.Test

class XmpSizeBudgetTest {
    @Test fun unicodeIsCountedAsUtf8IncludingSupplementaryCharacters() {
        val text="ASCII א € 😺"
        val count=text.toByteArray(Charsets.UTF_8).size
        XmpSizeBudget(count).add(text)
        assertThrows(XmpSizeLimitException::class.java) { XmpSizeBudget(count-1).add(text) }
        assertThrows(IllegalArgumentException::class.java) { XmpSizeBudget().add("\uD800") }
        assertThrows(IllegalArgumentException::class.java) { XmpSizeBudget().add("\uDC00") }
    }
    @Test fun outputRejectsOverflowBeforeRetainingAnyOfTheRejectedBytes() {
        val output=BoundedXmpOutput()
        val bytes=ByteArray(XmpCodec.MAX_BYTES) { 42 }
        output.write(bytes,0,bytes.size-1);output.write(42)
        assertEquals(XmpCodec.MAX_BYTES,output.size())
        assertThrows(XmpSizeLimitException::class.java) { output.write(43) }
        assertThrows(XmpSizeLimitException::class.java) { output.write(bytes,0,1) }
        assertEquals(XmpCodec.MAX_BYTES,output.size())
        assertArrayEquals(bytes,output.toByteArray())
    }
}

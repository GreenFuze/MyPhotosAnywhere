package com.myphotosanywhere.mpa.importing

import org.junit.Assert.*
import org.junit.Test
import java.nio.ByteBuffer

class DriveRangeChannelTest {
    @Test fun distantSeekFetchesOnlyBoundedRangesAndReusesTheCurrentBlock() {
        val requests=mutableListOf<Pair<Long,Int>>()
        val channel=DriveRangeChannel(5_000_000_000L,2L*DriveRangeChannel.BLOCK) { start,count ->
            requests.add(start to count); ByteArray(count) { ((start+it)%251).toByte() }
        }
        channel.position(4_999_999_990L)
        val end=ByteBuffer.allocate(10); assertEquals(10,channel.read(end)); assertEquals(-1,channel.read(ByteBuffer.allocate(1)))
        assertArrayEquals(ByteArray(10) { ((4_999_999_990L+it)%251).toByte() },end.array())
        channel.position(4_999_999_991L); assertEquals(1,channel.read(ByteBuffer.allocate(1))); assertEquals(1,requests.size)
        channel.position(0); assertEquals(1,channel.read(ByteBuffer.allocate(1))); assertEquals(2,requests.size)
        assertTrue(requests.all { it.second<=DriveRangeChannel.BLOCK })
        channel.close()
        try { channel.position(); fail() } catch(_: java.nio.channels.ClosedChannelException) { }
    }
    @Test fun exhaustedBudgetAndShortResponsesFailBeforeReturningInvalidBytes() {
        var calls=0
        val channel=DriveRangeChannel(2L*DriveRangeChannel.BLOCK,DriveRangeChannel.BLOCK.toLong()) { _,count -> calls++; ByteArray(count) }
        channel.read(ByteBuffer.allocate(1)); channel.position(DriveRangeChannel.BLOCK.toLong())
        try { channel.read(ByteBuffer.allocate(1)); fail() } catch(_: IllegalArgumentException) { }
        assertEquals(1,calls)
        try { DriveRangeChannel(10,10) { _,_-> ByteArray(9) }.read(ByteBuffer.allocate(1)); fail() }
        catch(_: IllegalArgumentException) { }
    }
    @Test fun writesAreRejectedAndEmptyReadAtEofReturnsZero() {
        val channel=DriveRangeChannel(1,1) { _,_->byteArrayOf(1) }
        try { channel.write(ByteBuffer.allocate(1)); fail() } catch(_: java.nio.channels.NonWritableChannelException) { }
        channel.position(1); assertEquals(0,channel.read(ByteBuffer.allocate(0)))
    }
    @Test fun failedFetchNeverReusesOldBytesAtTheNewPosition() {
        var fail=true
        val channel=DriveRangeChannel(2L*DriveRangeChannel.BLOCK,3L*DriveRangeChannel.BLOCK) { start,count ->
            if(start>0 && fail) { fail=false; throw java.io.IOException("interrupted") }
            ByteArray(count) { if(start==0L) 1 else 2 }
        }
        channel.read(ByteBuffer.allocate(1)); channel.position(DriveRangeChannel.BLOCK.toLong())
        try { channel.read(ByteBuffer.allocate(1)); fail() } catch(_: java.io.IOException) { }
        val result=ByteBuffer.allocate(1); channel.read(result); assertEquals(2,result.array()[0].toInt())
    }

}

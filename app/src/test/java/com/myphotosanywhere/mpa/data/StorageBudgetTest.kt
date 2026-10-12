package com.myphotosanywhere.mpa.data

import org.junit.Assert.*
import org.junit.Test
import java.io.File

class StorageBudgetTest {
    private class Capacity(var free: Long,var reclaimable: Long): StorageBudget.Capacity {
        var allocations=0
        override fun available(directory: File)=free
        override fun allocatable(directory: File)=reclaimable
        override fun allocate(directory: File,bytes: Long) { allocations++;free=bytes }
    }
    @Test fun enoughSpaceDoesNotReclaimCache() {
        val capacity=Capacity(100,100)
        StorageBudget(capacity).requireBytes(File("."),50,"full")
        assertEquals(0,capacity.allocations)
    }
    @Test fun reclaimableSpaceIsAllocatedBeforeItIsCountedAsAvailable() {
        val capacity=Capacity(10,100)
        val budget=StorageBudget(capacity)
        assertEquals(10L,budget.available(File(".")))
        budget.requireBytes(File("."),50,"full")
        assertEquals(1,capacity.allocations)
        assertEquals(51L,budget.available(File(".")))
    }
    @Test fun lowSpaceFailsWithoutAllocation() {
        val capacity=Capacity(10,20)
        assertThrows(IllegalArgumentException::class.java) {
            StorageBudget(capacity).requireBytes(File("."),50,"full")
        }
        assertEquals(0,capacity.allocations)
    }
    @Test fun allocatableCapacityCannotBeOverstatedByFilesystemFreeBytes() {
        val capacity=Capacity(100,20)
        assertThrows(IllegalArgumentException::class.java) {
            StorageBudget(capacity).requireBytes(File("."),50,"full")
        }
    }
}

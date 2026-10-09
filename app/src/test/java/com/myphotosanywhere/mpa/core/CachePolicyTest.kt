package com.myphotosanywhere.mpa.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class CachePolicyTest {
    @Test fun protectsUnbackedUpPendingPinnedAndActivelyReadFiles() {
        val files = listOf(
            CachedFile("unbacked", 10, 1, false),
            CachedFile("pending", 10, 2, true, pendingWrite = true),
            CachedFile("pinned", 10, 3, true, pinned = true),
            CachedFile("reading", 10, 4, true, activeReaders = 1),
            CachedFile("disposable", 10, 5, true),
        )
        val plan = CachePolicy.plan(files, 0)
        assertEquals(listOf("disposable"), plan.ids)
        assertEquals(40L, plan.remainingBytes)
        assertFalse(plan.budgetSatisfied)
    }

    @Test fun evictsOldestDownloadsOnlyUntilBudgetIsMet() {
        val plan = CachePolicy.plan(listOf(
            CachedFile("new", 10, 300, true), CachedFile("old", 10, 100, true),
            CachedFile("middle", 10, 200, true),
        ), 15)
        assertEquals(listOf("old", "middle"), plan.ids)
        assertEquals(10L, plan.remainingBytes)
        assertTrue(plan.budgetSatisfied)
    }

    @Test fun doesNotEvictWhenBelowBudget() {
        assertTrue(CachePolicy.plan(listOf(CachedFile("a", 10, 0, true)), 10).ids.isEmpty())
    }

    @Test(expected = IllegalArgumentException::class)
    fun rejectsNegativeBudget() { CachePolicy.plan(emptyList(), -1) }

    @Test(expected = IllegalArgumentException::class)
    fun rejectsDuplicateIds() { CachePolicy.plan(List(2) { CachedFile("same", 10, 0, true) }, 10) }
}

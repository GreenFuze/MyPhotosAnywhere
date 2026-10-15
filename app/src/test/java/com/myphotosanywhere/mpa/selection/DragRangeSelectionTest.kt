package com.myphotosanywhere.mpa.selection

import org.junit.Assert.*
import org.junit.Test

class DragRangeSelectionTest {
    private fun target(index: Int)=SelectionTarget("photo:$index","$index.jpg","$index",null,10,"hash-$index",null,null)
    private fun window(start: Int,end: Int)=(start..end).map(::target)
    @Test fun forwardAndReversePreserveBaselineAndOnlyReconcileGestureRange() {
        val range=DragRangeSelection(listOf(target(8)),window(0,9),"photo:2")
        assertEquals(listOf(8,2,3,4,5).map(::target),range.move(window(0,9),"photo:5").targets)
        assertEquals(listOf(8,2,3).map(::target),range.move(window(0,9),"photo:3").targets)
        assertEquals(listOf(8,2,1,0).map(::target),range.move(window(0,9),"photo:0").targets)
        assertEquals(listOf(8,2).map(::target),range.move(window(0,9),"photo:2").targets)
    }
    @Test fun startingOnAnAlreadySelectedPhotoIsIdempotent() {
        val range=DragRangeSelection(listOf(target(2)),window(0,9),"photo:2")
        assertEquals(listOf(target(2)),range.move(window(0,9),"photo:2").targets)
    }
    @Test fun boundedPageWindowsExtendWithoutLosingVisitedRange() {
        val range=DragRangeSelection(emptyList(),window(0,299),"photo:150")
        assertEquals(150,range.move(window(0,299),"photo:299").targets.size)
        assertEquals(300,range.move(window(150,449),"photo:449").targets.size)
        assertEquals(2,range.move(window(100,399),"photo:151").targets.size)
    }
    @Test fun representationContentRemovalAndReorderingCancelWithoutRetargeting() {
        for(changed in listOf(window(0,9).reversed(),window(0,9).filter { it.photoId!="4" },window(0,9).map { if(it.photoId=="4") it.copy(sha256="changed") else it })) {
            val range=DragRangeSelection(emptyList(),window(0,9),"photo:2")
            val saved=range.move(window(0,9),"photo:5").targets
            val update=range.move(changed,"photo:8")
            assertTrue(update.cancelled);assertEquals(saved,update.targets)
        }
    }
    @Test fun rangeLimitIsExplicitAndReversalCanReduceIt() {
        val range=DragRangeSelection(listOf(target(999)),window(0,299),"photo:0")
        range.move(window(0,299),"photo:299")
        val limited=range.move(window(250,549),"photo:549")
        assertTrue(limited.atLimit);assertEquals(500,limited.targets.size);assertTrue(target(999) in limited.targets)
        val reversed=range.move(window(0,299),"photo:10")
        assertFalse(reversed.atLimit);assertEquals(12,reversed.targets.size)
    }
    @Test fun excessiveHistoryCancelsAndNeverMaterializesALibrary() {
        val range=DragRangeSelection(emptyList(),window(0,299),"photo:0")
        for(start in listOf(250,500)) range.move(window(start,start+299),"photo:${start+299}")
        val update=range.move(window(750,1049),"photo:1049")
        assertTrue(update.cancelled);assertEquals(500,update.targets.size)
    }
    @Test fun edgeSpeedIsSymmetricClampedAndIdleInTheCenter() {
        val scroll=SelectionEdgeScroll(48f,800f)
        assertEquals(-800f,scroll.velocity(-500f,0f,1000f),0f)
        assertEquals(800f,scroll.velocity(1500f,0f,1000f),0f)
        assertEquals(0f,scroll.velocity(500f,0f,1000f),0f)
        assertEquals(-scroll.velocity(24f,0f,1000f),scroll.velocity(976f,0f,1000f),0f)
        assertEquals(0f,scroll.velocity(5f,5f,5f),0f)
    }
    @Test fun duplicateCanonicalKeysFailFast() {
        assertThrows(IllegalStateException::class.java) { DragRangeSelection(emptyList(),listOf(target(1),target(1)),"photo:1") }
    }
}

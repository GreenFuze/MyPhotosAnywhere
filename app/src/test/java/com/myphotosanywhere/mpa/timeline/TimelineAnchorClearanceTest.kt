package com.myphotosanywhere.mpa.timeline

import org.junit.Assert.*
import org.junit.Test

class TimelineAnchorClearanceTest {
    @Test fun shortRepackedGalleryCanRestoreHeldRowWithoutUnboundedBlankSpace() {
        val padding=TimelineAnchorClearance(listOf(100,100),2,2).bottom(1,0,500,60)
        assertTrue("Held row needs 102 pixels of scrolling",204+padding-500>=102)
        assertTrue("At most one viewport of clearance",padding<=500)
        val cropped=TimelineAnchorClearance(listOf(100,100),2,2).bottom(1,60,500,60)
        assertTrue(204+cropped-500>=162);assertTrue(cropped<=500)
    }
    @Test fun populatedLibraryAndTopBoundaryKeepNormalPadding() {
        assertEquals(60,TimelineAnchorClearance(List(30) { 100 },2,2).bottom(2,0,500,60))
        assertEquals(60,TimelineAnchorClearance(listOf(100,100),2,2).bottom(0,0,500,60))
        assertEquals(60,TimelineAnchorClearance(emptyList(),2,2).bottom(null,0,500,60))
    }
    @Test fun densityChangeClampsCropAndDoesNotOverflowPixelArithmetic() {
        val padding=TimelineAnchorClearance(listOf(80,80),2,2).bottom(1,400,500,60)
        assertTrue(padding in 60..500)
        assertEquals(60,TimelineAnchorClearance(listOf(Int.MAX_VALUE,Int.MAX_VALUE),2,2).bottom(0,1,500,60))
    }
    @Test fun invalidGeometryFailsFast() {
        assertThrows(IllegalArgumentException::class.java) { TimelineAnchorClearance(listOf(0),2,2) }
        assertThrows(IllegalArgumentException::class.java) { TimelineAnchorClearance(listOf(100),-1,2) }
        assertThrows(IllegalArgumentException::class.java) { TimelineAnchorClearance(listOf(100),2,2).bottom(1,0,500,60) }
        assertThrows(IllegalArgumentException::class.java) { TimelineAnchorClearance(listOf(100),2,2).bottom(0,0,0,60) }
    }
}

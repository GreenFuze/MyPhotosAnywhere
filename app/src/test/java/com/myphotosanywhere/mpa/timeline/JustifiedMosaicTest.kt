package com.myphotosanywhere.mpa.timeline

import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test

class JustifiedMosaicTest {
    private val planner=JustifiedMosaic()
    @Test fun mixedAspectsFillAlignedRowWithoutReordering() {
        val items=listOf(MosaicInput("portrait",0.67f),MosaicInput("square"),MosaicInput("landscape",1.5f))
        val cells=planner.plan(items,3,407f,GalleryLayout.MOSAIC).cells
        assertTrue(cells[0].span<cells[1].span && cells[1].span<cells[2].span)
        assertEquals(24,cells.sumOf { it.span });assertEquals(1,cells.map { it.height }.toSet().size)
        assertEquals(listOf(0,1,2),cells.map { it.column })
    }
    @Test fun squareModePreservesExistingCellFootprint() {
        val cells=planner.plan(List(3) { MosaicInput("$it",if(it==0) 0.5f else 2f) },3,316f,GalleryLayout.SQUARE).cells
        assertEquals(listOf(8,8,8),cells.map { it.span });assertEquals(104f,requireNotNull(cells[0].height),0.001f)
    }
    @Test fun incompleteDayAndHeadersKeepStableFootprint() {
        val items=listOf(MosaicInput("day1",heading=true),MosaicInput("one"),MosaicInput("day2",heading=true),MosaicInput("two"))
        val cells=planner.plan(items,3,316f,GalleryLayout.MOSAIC).cells
        assertEquals(listOf(24,8,24,8),cells.map { it.span });assertEquals(listOf(0,1,2,3),cells.map { it.row })
        assertEquals(cells[1].height,cells[3].height)
    }
    @Test fun extremeAspectsStayBoundedAndTouchable() {
        val cells=planner.plan(List(4) { MosaicInput("$it",if(it==0) 0.01f else 100f) },4,316f,GalleryLayout.MOSAIC).cells
        assertEquals(24,cells.sumOf { it.span })
        cells.forEach { assertTrue((316f+2)*it.span/24-2>=48);assertTrue(requireNotNull(it.height) in 48f..124f) }
    }
    @Test fun narrowWindowsReduceRowCountToProtectTouchTargets() {
        val result=planner.plan(List(4) { MosaicInput("$it") },4,140f,GalleryLayout.MOSAIC)
        assertEquals(2,result.columns);assertEquals(listOf(0,0,1,1),result.cells.map { it.row })
    }
    @Test fun invalidInputsAndDuplicateIdentitiesFailFast() {
        assertThrows(IllegalArgumentException::class.java) { MosaicInput("bad",Float.NaN) }
        assertThrows(IllegalArgumentException::class.java) { planner.plan(emptyList(),3,0f,GalleryLayout.MOSAIC) }
        assertThrows(IllegalStateException::class.java) { planner.plan(listOf(MosaicInput("same"),MosaicInput("same")),3,316f,GalleryLayout.MOSAIC) }
    }
    @Test fun geometryFreezesMissingPreviewAndKeepsCacheBounded()=runBlocking<Unit> {
        var ratio=1f;var calls=0
        val geometry=ThumbnailGeometry(300) { calls++;ratio }
        assertEquals(1f,geometry.aspect("held","missing"),0f)
        ratio=2f;assertEquals(1f,geometry.aspect("held","arrived"),0f);assertEquals(1,calls)
        repeat(300) { geometry.aspect("next-$it","") }
        assertEquals(300,geometry.retainedCount)
        assertEquals(2f,geometry.aspect("held","arrived"),0f)
    }
    @Test fun geometryReadsOffCallerThreadAndRejectsInvalidHeaders()=runBlocking<Unit> {
        val caller=Thread.currentThread()
        val geometry=ThumbnailGeometry { assertNotSame(caller,Thread.currentThread());0f }
        assertThrows(IllegalStateException::class.java) { runBlocking { geometry.aspect("invalid","") } }
    }
    @Test fun repackingKeepsPreferredCanonicalIdentityOnFirstLine() {
        val first=TimelineAnchor(TimelineCursor(3,"photo:first"),40)
        val held=TimelineAnchor(TimelineCursor(2,"photo:held"),40)
        val later=TimelineAnchor(TimelineCursor(1,"photo:later"),-100)
        assertEquals(held,TimelineViewport.choose(listOf(first,held,later),held))
        assertEquals(first,TimelineViewport.choose(listOf(first,held,later),later))
        assertEquals(held,TimelineViewport.opening(first,held))
        assertEquals(first,TimelineViewport.opening(first,later))
        assertEquals(first,TimelineViewport.opening(first,null))
    }
    @Test fun scrollingBeyondHeldRowAdvancesAnchorAndEmptyHasNoAnchor() {
        val held=TimelineAnchor(TimelineCursor(3,"photo:held"),40)
        val next=TimelineAnchor(TimelineCursor(2,"photo:next"),20)
        assertEquals(next,TimelineViewport.choose(listOf(next),held))
        assertNull(TimelineViewport.choose(emptyList(),held))
    }
}

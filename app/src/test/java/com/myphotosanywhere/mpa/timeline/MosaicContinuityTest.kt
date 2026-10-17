package com.myphotosanywhere.mpa.timeline

import org.junit.Assert.*
import org.junit.Test

class MosaicContinuityTest {
    private fun items(first: Int,count: Int)=List(count) { n -> val id=first+n;MosaicInput("photo:$id",if(id%4==0) 0.6f else if(id%4==1) 2.2f else 1f) }
    private fun geometry(plan: BlockMosaicPlan,key: String): List<Any> {
        val (block,tile)=plan.locations.getValue(key)
        return listOf(plan.blocks[block].key,tile.x,tile.y,tile.width,tile.height,tile.rowSpan)
    }
    @Test fun appendingAndLateDimensionsKeepEveryExistingTileInPlace() {
        for(layout in GalleryLayout.entries) {
            val owner=MosaicContinuity();val first=items(0,60)
            val before=owner.plan(first,3,407f,false,layout)
            val after=owner.plan((first+items(60,60)).map { it.copy(aspect=1.7f) },3,407f,false,layout)
            first.forEach { assertEquals(layout.toString(),geometry(before,it.key),geometry(after,it.key)) }
        }
    }
    @Test fun prependPreservesExistingBlockKeysAndGeometryInEveryLayout() {
        for(layout in GalleryLayout.entries) {
            val owner=MosaicContinuity();val first=items(60,90)
            val before=owner.plan(first,4,407f,false,layout)
            val after=owner.plan(items(0,60)+first,4,407f,false,layout)
            first.forEach { assertEquals(geometry(before,it.key),geometry(after,it.key)) }
            assertEquals((0 until 150).map { "photo:$it" },after.tiles.map { it.key })
        }
    }
    @Test fun droppingAndRestoringBoundaryMembersPreservesSlotsAndTraversal() {
        for(layout in GalleryLayout.entries) {
            val owner=MosaicContinuity();val first=items(0,300)
            val before=owner.plan(first,3,407f,false,layout)
            val dropped=owner.plan(first.drop(61),3,407f,false,layout)
            first.drop(61).forEach { assertEquals(geometry(before,it.key),geometry(dropped,it.key)) }
            val restored=owner.plan(first,3,407f,false,layout)
            first.forEach { assertEquals(geometry(before,it.key),geometry(restored,it.key)) }
        }
    }
    @Test fun interiorInsertRebuildsOnlyTheAffectedBlockAndRetainsEveryIdentity() {
        val owner=MosaicContinuity();val first=items(0,120);val before=owner.plan(first,3,407f,false,GalleryLayout.MOSAIC)
        val fresh=first.take(2)+MosaicInput("photo:new",1f)+first.drop(2)
        val after=owner.plan(fresh,3,407f,false,GalleryLayout.MOSAIC)
        assertEquals(fresh.map { it.key },after.tiles.map { it.key })
        before.blocks.drop(1).flatMap { it.tiles }.forEach { assertEquals(geometry(before,it.key),geometry(after,it.key)) }
    }
    @Test fun templatesStayBoundedAcrossLargeArchivesAndResetForExplicitGeometryChanges() {
        val owner=MosaicContinuity()
        for(start in 0 until 10000 step 60) owner.plan(items(start,300),3,407f,false,GalleryLayout.MOSAIC)
        assertTrue(owner.retainedTiles<=600)
        val changed=owner.plan(items(10000,90),2,700f,true,GalleryLayout.SQUARE)
        assertEquals(2,changed.columns);assertEquals(90,changed.tiles.size);assertTrue(owner.retainedTiles<=90)
    }
    @Test fun invalidInputsFailFast() {
        val owner=MosaicContinuity()
        assertThrows(IllegalStateException::class.java) { owner.plan(listOf(MosaicInput("same"),MosaicInput("same")),3,407f,true,GalleryLayout.MOSAIC) }
        assertThrows(IllegalArgumentException::class.java) { owner.plan(items(0,1),3,0f,true,GalleryLayout.MOSAIC) }
    }
    private fun cursors(vararg times: Long)=times.map { TimelineCursor(it,"photo:$it") }
    @Test fun refreshedArrivalsStayDeferredButOrdinaryPagingIsVisible() {
        val owner=TimelineWindowContinuity();val initial=cursors(100,90,80,70)
        assertEquals(initial.map { it.key }.toSet(),owner.present(initial,1,true))
        val refresh=cursors(110,100,95,90,80,75,70)
        assertEquals(initial.map { it.key }.toSet(),owner.present(refresh,2,true));assertEquals(3,owner.pendingCount)
        val extended=refresh+cursors(60,50)
        assertTrue(owner.present(extended,2,true).contains("photo:50"));assertFalse(owner.present(extended,2,true).contains("photo:95"))
        assertEquals(extended.map { it.key }.toSet(),owner.present(extended,2,false));assertEquals(0,owner.pendingCount)
    }
    @Test fun removedAnchorsAndUnknownDatesDoNotCreateDuplicatesOrUnboundedQueues() {
        val owner=TimelineWindowContinuity();owner.present(cursors(100,80,60),1,true)
        val removed=cursors(100,90,60)+TimelineCursor(null,"photo:unknown")
        assertEquals(setOf("photo:100","photo:60","photo:unknown"),owner.present(removed,2,true))
        owner.clear();assertEquals(removed.map { it.key }.toSet(),owner.present(removed,3,true));assertEquals(0,owner.pendingCount)
    }
    @Test fun completeWindowRemovalRevealsAvailableNeighborsAndConfirmedEmptyClearsTheNotice() {
        val owner=TimelineWindowContinuity();owner.present(cursors(100,80,60),1,true)
        val replacement=listOf(TimelineCursor(90,"photo:replacement"))
        assertEquals(setOf("photo:replacement"),owner.present(replacement,2,true));assertEquals(0,owner.pendingCount)
        owner.present(cursors(90,80,60),3,true)
        assertEquals(emptySet<String>(),owner.present(emptyList(),4,false));assertEquals(0,owner.pendingCount)
    }

    @Test fun globalFrontierDetectsArrivalsOutsideRetainedPagesWithoutCountingRemovalsAsNew() {
        val frontier=TimelineFrontier();val first=TimelineCursor(100,"photo:first");val newer=TimelineCursor(110,"photo:new")
        frontier.observe(first);assertFalse(frontier.hasNewer)
        frontier.observe(newer);assertTrue(frontier.hasNewer);assertFalse(frontier.acknowledge(first));assertTrue(frontier.hasNewer)
        assertTrue(frontier.acknowledge(newer));assertFalse(frontier.hasNewer)
        frontier.observe(TimelineCursor(90,"photo:older"));assertFalse(frontier.hasNewer)
        frontier.observe(null);assertFalse(frontier.hasNewer)
    }
    @Test fun firstContentAfterAnAcknowledgedEmptyLibraryCanBeRevealedExplicitly() {
        val frontier=TimelineFrontier();frontier.observe(null);assertFalse(frontier.hasNewer)
        val first=TimelineCursor(100,"photo:first");frontier.observe(first);assertTrue(frontier.hasNewer)
        assertTrue(frontier.acknowledge(first));assertFalse(frontier.hasNewer);assertFalse(frontier.acknowledge(first))
    }

}

package com.myphotosanywhere.mpa.timeline

import org.junit.Assert.*
import org.junit.Test

class BlockMosaicTest {
    private val planner=BlockMosaic()
    private fun items(count: Int)=List(count) { MosaicInput("photo:$it",if(it%4==0) 0.6f else if(it%4==1) 2.2f else 1f) }
    @Test fun portraitSpansRowsAndEveryCanonicalIdentityKeepsTraversalOrder() {
        val input=items(90);val plan=planner.plan(input,3,407f,true)
        assertEquals(input.map { it.key },plan.tiles.map { it.key })
        val hero=plan.tiles.first();assertTrue(hero.rowSpan>=2)
        assertTrue(hero.height>plan.tiles[1].height*1.9f)
        assertTrue(hero.width>plan.tiles[1].width*1.9f)
        assertEquals(plan.tiles[1].y,hero.y,0f)
        assertTrue(plan.tiles.all { it.width>=48 && it.height>=48 })
    }
    @Test fun noTilesOverlapAndCompleteBlocksHaveNoUnusedArea() {
        val plan=planner.plan(items(90),3,407f,true)
        for(block in plan.blocks) for(a in block.tiles) for(b in block.tiles) if(a!=b) {
            assertFalse(a.x<b.x+b.width-0.01f && b.x<a.x+a.width-0.01f && a.y<b.y+b.height-0.01f && b.y<a.y+a.height-0.01f)
        }
        assertTrue(plan.blocks.all { it.tiles.all { tile -> tile.y+tile.height<=it.height+0.01f } })
    }
    @Test fun appendingIntoPartialBlockDoesNotMoveAlreadyPlacedTiles() {
        val before=planner.plan(items(3),3,407f,false)
        val after=planner.plan(items(90),3,407f,false)
        assertEquals(before.tiles,after.tiles.take(3))
        val terminal=planner.plan(items(3),3,407f,true)
        assertEquals(1,terminal.blocks.first().rows)
        assertEquals(3,terminal.tiles.size)
    }
    @Test fun densityAndNarrowWidthsKeepTargetsAndBoundedWindowPlans() {
        for(columns in 2..4) for(width in listOf(140f,316f,407f,700f)) {
            val plan=planner.plan(items(300),columns,width,true)
            assertEquals(300,plan.locations.size)
            assertTrue(plan.tiles.all { it.width>=47.99f && it.height>=48 })
        }
        // Library size never enters the planner: both archives provide one retained page budget.
        for(size in listOf(10000,100000)) {
            val window=(size-300 until size).map { MosaicInput("photo:$it",0.67f) }
            assertEquals(planner.plan(window,3,407f,true),planner.plan(window,3,407f,true))
        }
    }
    @Test fun duplicateIdentitiesAndInvalidGeometryFailFast() {
        assertThrows(IllegalStateException::class.java) { planner.plan(listOf(MosaicInput("same"),MosaicInput("same")),3,407f,true) }
        assertThrows(IllegalArgumentException::class.java) { planner.plan(items(1),3,0f,true) }
    }
}

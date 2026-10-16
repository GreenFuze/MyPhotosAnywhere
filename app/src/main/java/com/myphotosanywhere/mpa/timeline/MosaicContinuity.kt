package com.myphotosanywhere.mpa.timeline

import kotlin.math.roundToInt

/** Retain geometry and row membership for overlapping page windows, never original media. */
internal class MosaicContinuity(private val capacity: Int=600) {
    init { require(capacity in 300..1200) }
    private data class Context(val columns: Int,val width: Float,val layout: GalleryLayout)
    private var context: Context?=null
    private val templates=LinkedHashMap<String,PhotoBlock>()
    val retainedTiles: Int get()=templates.values.sumOf { it.tiles.size }

    fun plan(items: List<MosaicInput>,columns: Int,width: Float,endReached: Boolean,layout: GalleryLayout): BlockMosaicPlan {
        require(items.size<=capacity && columns in 2..4 && width.isFinite() && width>=48)
        check(items.none { it.heading } && items.map { it.key }.toSet().size==items.size)
        val next=Context(columns,width,layout)
        if(context!=next) { templates.clear();context=next }
        val positions=items.withIndex().associate { it.value.key to it.index }
        // A retained block may lose boundary members as Paging drops a page. Its surviving
        // tiles keep their original slots; a later prepend can fill those same slots again.
        val candidates=templates.values.mapNotNull { block ->
            val surviving=block.tiles.filter { it.key in positions }
            if(surviving.isEmpty()) null else {
                val indices=surviving.map { positions.getValue(it.key) }
                if(indices!=indices.sorted() || indices.zipWithNext().any { (a,b) -> b!=a+1 }) null
                else Triple(indices.first(),indices.last()+1,block.copy(tiles=surviving))
            }
        }.sortedBy { it.first }
        val result=ArrayList<PhotoBlock>();val kept=mutableSetOf<String>();var index=0
        fun appendGap(until: Int) {
            if(index==until) return
            val input=items.subList(index,until)
            result+=fresh(input,columns,width,if(until==items.size) endReached else true,layout)
            index=until
        }
        for((start,end,block) in candidates) {
            if(start<index) continue
            appendGap(start);result+=block;kept+=block.key;index=end
        }
        appendGap(items.size)
        var logicalRow=0
        val packed=result.map { block ->
            val firstRow=block.tiles.first().let { it.row-(it.y/(it.height+BlockMosaic.GAP)).roundToInt() }
            block.copy(tiles=block.tiles.map { it.copy(index=positions.getValue(it.key),row=logicalRow+it.row-firstRow) })
                .also { logicalRow+=it.rows }
        }
        // Refresh templates without replacing the untrimmed boundary geometry of a kept block.
        for(block in packed) {
            val old=templates[block.key]
            if(old==null || block.key !in kept) {
                val keys=block.tiles.map { it.key }.toSet()
                templates.entries.removeAll { (_,value) -> value.tiles.any { it.key in keys } }
                templates[block.key]=block
            } else {
                templates.remove(block.key);templates[block.key]=old
            }
        }
        while(retainedTiles>capacity) templates.remove(templates.keys.first())
        val physical=JustifiedMosaic().plan(emptyList(),columns,width,layout).columns
        return BlockMosaicPlan(packed,physical).also { check(it.tiles.map { tile -> tile.key }==items.map { item -> item.key }) }
    }
    private fun fresh(items: List<MosaicInput>,columns: Int,width: Float,endReached: Boolean,layout: GalleryLayout): List<PhotoBlock> {
        if(layout==GalleryLayout.MOSAIC) return BlockMosaic().plan(items,columns,width,endReached).blocks
        val rows=JustifiedMosaic().plan(items,columns,width,layout)
        return items.indices.groupBy { rows.cells[it].row }.values.map { indices ->
            var x=0f
            val tiles=indices.map { position ->
                val cell=rows.cells[position];val size=(width+BlockMosaic.GAP)*cell.span/JustifiedMosaic.SLOTS-BlockMosaic.GAP
                BlockTile(items[position].key,position,x,0f,size,requireNotNull(cell.height),cell.row,cell.column)
                    .also { x+=size+BlockMosaic.GAP }
            }
            PhotoBlock("row:${tiles.first().key}",tiles,tiles.first().height,1)
        }
    }
}

/** Defer arrivals inside an already browsed window; page extension is never mistaken for import. */
internal class TimelineWindowContinuity(private val capacity: Int=300) {
    init { require(capacity in 90..600) }
    private var epoch: Long?=null
    private var previous: List<TimelineCursor> = emptyList()
    private val pending=linkedSetOf<String>()
    val pendingCount: Int get()=pending.size
    fun hidden(key: String)=key in pending
    fun clear() { pending.clear();previous=emptyList();epoch=null }
    fun present(items: List<TimelineCursor>,generation: Long,holdArrivals: Boolean): Set<String> {
        check(items.map { it.key }.toSet().size==items.size)
        require(items.size<=capacity*2)
        if(items.isEmpty()) { clear();epoch=generation;return emptySet() }
        if(!holdArrivals) pending.clear()
        else if(epoch!=null && epoch!=generation && previous.isNotEmpty()) {
            val known=previous.map { it.key }.toSet()
            val last=previous.last()
            items.filter { it.key !in known && TimelineOrder.compare(it,last)<=0 }
                .forEach { pending+=it.key }
            while(pending.size>capacity) pending.remove(pending.first())
        }
        if(items.none { it.key !in pending }) pending.clear()
        previous=items.toList();epoch=generation
        return items.map { it.key }.filterNot { it in pending }.toSet()
    }
}

/** Compare the newest indexed identity without loading the library or changing its anchor. */
internal class TimelineFrontier {
    private var initialized=false
    private var acknowledged: TimelineCursor?=null
    private var available: TimelineCursor?=null
    val hasNewer: Boolean get() {
        val latest=available ?: return false
        val previous=acknowledged
        return initialized && (previous==null || TimelineOrder.compare(latest,previous)<0)
    }
    fun observe(cursor: TimelineCursor?) {
        available=cursor
        if(!initialized) { initialized=true;acknowledged=cursor }
    }
    fun acknowledge(cursor: TimelineCursor?): Boolean {
        if(!initialized || cursor!=available || cursor==acknowledged) return false
        acknowledged=cursor;return true
    }
}

package com.myphotosanywhere.mpa.timeline

import kotlin.math.ceil
import kotlin.math.floor

internal enum class GalleryLayout(val title: String) { MOSAIC("Mosaic"), ROWS("Justified rows"), SQUARE("Square") }
internal data class MosaicInput(val key: String,val aspect: Float=1f,val heading: Boolean=false) {
    init { require(key.isNotBlank() && aspect.isFinite() && aspect>0) }
}
internal data class MosaicCell(val span: Int,val height: Float?,val row: Int,val column: Int,val heading: Boolean=false)
internal data class MosaicPlan(val cells: List<MosaicCell>,val columns: Int,val rowCount: Int)

/** Packs only the retained paging window. Allocation changes widths, never identity or order. */
internal class JustifiedMosaic {
    fun plan(items: List<MosaicInput>,requestedColumns: Int,width: Float,layout: GalleryLayout): MosaicPlan {
        require(requestedColumns in 2..4 && width.isFinite() && width>0)
        check(items.map { it.key }.toSet().size==items.size) { "Mosaic has duplicate canonical identities" }
        val columns=minOf(requestedColumns,floor((width+GAP)/(MIN_TARGET+GAP)).toInt().coerceAtLeast(1))
        val base=(width-(columns-1)*GAP)/columns
        val result=ArrayList<MosaicCell>(items.size)
        var index=0;var row=0
        while(index<items.size) {
            if(items[index].heading) { result.add(MosaicCell(SLOTS,null,row++,0,true));index++;continue }
            var end=index
            while(end<items.size && !items[end].heading && end-index<columns) end++
            val ratios=(index until end).map { if(layout==GalleryLayout.SQUARE) 1f else items[it].aspect.coerceIn(0.5f,2.5f) }
            val count=ratios.size
            // Incomplete days/pages keep their normal footprint rather than enlarging a lone photo.
            val slots=SLOTS*count/columns
            val minimum=ceil((MIN_TARGET+GAP)*SLOTS/(width+GAP)).toInt().coerceAtLeast(1).coerceAtMost(slots/count)
            val spans=allocate(ratios,slots,minimum)
            val height=(((width+GAP)*slots/SLOTS-count*GAP)/ratios.sum()).coerceIn(maxOf(MIN_TARGET,base*0.6f),maxOf(MIN_TARGET,base*1.6f))
            spans.forEachIndexed { column,span -> result.add(MosaicCell(span,height,row,column)) }
            row++;index=end
        }
        return MosaicPlan(result,columns,row)
    }
    private fun allocate(ratios: List<Float>,slots: Int,minimum: Int): List<Int> {
        val result=IntArray(ratios.size)
        val pending=ratios.indices.toMutableList();var available=slots
        while(pending.isNotEmpty()) {
            val sum=pending.sumOf { ratios[it].toDouble() }
            val small=pending.filter { available*ratios[it]/sum<minimum }
            if(small.isNotEmpty()) {
                small.forEach { result[it]=minimum;available-=minimum;pending.remove(it) }
            } else {
                val exact=pending.associateWith { available*ratios[it]/sum }
                pending.forEach { result[it]=floor(requireNotNull(exact[it])).toInt() }
                var rest=available-pending.sumOf { result[it] }
                pending.sortedWith(compareByDescending<Int> { requireNotNull(exact[it])-result[it] }.thenBy { it }).forEach {
                    if(rest>0) { result[it]++;rest-- }
                }
                pending.clear()
            }
        }
        check(result.sum()==slots && result.all { it>=minimum })
        return result.toList()
    }
    companion object { const val SLOTS=24;const val GAP=2f;const val MIN_TARGET=48f }
}

/** A row repack may put another tile before the anchored photo on the same top line. */
internal object TimelineViewport {
    /** Keep a tapped photo on the top row as the held identity when imports repack it. */
    fun opening(origin: TimelineAnchor?,tapped: TimelineAnchor?): TimelineAnchor? =
        tapped?.takeIf { origin==null || it.offset==origin.offset } ?: origin

    fun choose(visible: List<TimelineAnchor>,preferred: TimelineAnchor?): TimelineAnchor? {
        val first=visible.firstOrNull() ?: return null
        if(preferred==null) return first
        // A tall earlier tile can remain partially visible beside a sought small tile.
        // Keep the requested identity while it remains at its exact saved pixel offset;
        // actual scrolling changes that offset and restores ordinary first-visible tracking.
        return visible.firstOrNull { it.cursor.key==preferred.cursor.key &&
            (it.offset==first.offset || it.offset>=0 && it.offset==preferred.offset) } ?: first
    }
}

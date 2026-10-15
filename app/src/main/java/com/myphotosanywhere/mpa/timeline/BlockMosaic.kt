package com.myphotosanywhere.mpa.timeline

import kotlin.math.floor
import kotlin.math.roundToInt

internal data class BlockTile(val key: String,val index: Int,val x: Float,val y: Float,val width: Float,val height: Float,
    val row: Int,val column: Int,val rowSpan: Int=1)
internal data class PhotoBlock(val key: String,val tiles: List<BlockTile>,val height: Float,val rows: Int)
internal data class BlockMosaicPlan(val blocks: List<PhotoBlock>,val columns: Int) {
    val tiles=blocks.flatMap { it.tiles }
    val rows=blocks.sumOf { it.rows }
    val locations=blocks.flatMapIndexed { index,block -> block.tiles.map { it.key to (index to it) } }.toMap()
    init { check(tiles.map { it.index }==tiles.indices.toList());check(locations.size==tiles.size) }
}

/** Packs only retained pages. Portrait heroes span rows; traversal remains source order. */
internal class BlockMosaic {
    fun plan(items: List<MosaicInput>,requestedColumns: Int,width: Float,endReached: Boolean): BlockMosaicPlan {
        require(requestedColumns in 2..4 && width.isFinite() && width>=48)
        check(items.none { it.heading } && items.map { it.key }.toSet().size==items.size)
        val columns=minOf(requestedColumns,floor((width+GAP)/(48+GAP)).toInt().coerceAtLeast(1))
        val blocks=ArrayList<PhotoBlock>();var index=0;var logicalRow=0
        while(index<items.size) {
            val heroWidth=(width-GAP)/2
            val sideColumns=columns-1
            val small=if(sideColumns>0) (width-heroWidth-GAP-(sideColumns-1)*GAP)/sideColumns else width
            val heroRows=if(sideColumns>0) (heroWidth/items[index].aspect/(small+GAP)).roundToInt().coerceIn(2,6) else 1
            val count=1+heroRows*sideColumns
            if(columns>1 && small>=48 && items[index].aspect<0.85f && (!endReached || items.size-index>=count)) {
                val end=minOf(items.size,index+count);val height=heroRows*(small+GAP)-GAP
                val tiles=ArrayList<BlockTile>()
                tiles.add(BlockTile(items[index].key,index,0f,0f,heroWidth,height,logicalRow,0,heroRows))
                for(position in index+1 until end) {
                    val n=position-index-1;val row=n/sideColumns;val column=n%sideColumns
                    tiles.add(BlockTile(items[position].key,position,heroWidth+GAP+column*(small+GAP),row*(small+GAP),small,small,logicalRow+row,column+1))
                }
                blocks.add(PhotoBlock("block:${items[index].key}",tiles,height,heroRows));index=end;logicalRow+=heroRows
            } else {
                val end=minOf(items.size,index+columns);val input=items.subList(index,end)
                val row=JustifiedMosaic().plan(input,requestedColumns,width,GalleryLayout.ROWS)
                var x=0f
                val tiles=input.mapIndexed { n,item ->
                    val cell=row.cells[n];val tileWidth=(width+GAP)*cell.span/JustifiedMosaic.SLOTS-GAP
                    BlockTile(item.key,index+n,x,0f,tileWidth,requireNotNull(cell.height),logicalRow,n).also { x+=tileWidth+GAP }
                }
                blocks.add(PhotoBlock("block:${items[index].key}",tiles,requireNotNull(row.cells.first().height),1));index=end;logicalRow++
            }
        }
        return BlockMosaicPlan(blocks,columns)
    }
    companion object { const val GAP=2f }
}

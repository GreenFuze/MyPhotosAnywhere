package com.myphotosanywhere.mpa.people

import com.myphotosanywhere.mpa.data.FaceRecord

/** Coordinates refer to the oriented preview, so they are independent of preview resolution. */
data class FaceRegion(val left: Float,val top: Float,val right: Float,val bottom: Float) {
    init { require(listOf(left,top,right,bottom).all { it.isFinite() && it in 0f..1f } && left<right && top<bottom) }
    fun overlap(other: FaceRegion): Float {
        val area=(minOf(right,other.right)-maxOf(left,other.left)).coerceAtLeast(0f)*
            (minOf(bottom,other.bottom)-maxOf(top,other.top)).coerceAtLeast(0f)
        return area/((right-left)*(bottom-top)+(other.right-other.left)*(other.bottom-other.top)-area)
    }
    companion object { fun of(face: FaceRecord): FaceRegion? {
        val l=face.left ?: return null; val t=face.top ?: return null
        val r=face.right ?: return null; val b=face.bottom ?: return null
        return runCatching { FaceRegion(l,t,r,b) }.getOrNull()
    } }
}
object FaceRegions {
    /** Mutual, unambiguous geometric matches only. Unknown legacy geometry is preserved separately. */
    fun matches(found: List<FaceRegion>,previous: List<FaceRecord>): Map<Int,FaceRecord> {
        val candidates=found.map { region -> previous.mapNotNull { face ->
            FaceRegion.of(face)?.overlap(region)?.takeIf { it>=.65f }?.let { face to it }
        }.sortedByDescending { it.second } }
        return candidates.mapIndexedNotNull { index,list ->
            val best=list.firstOrNull() ?: return@mapIndexedNotNull null
            if(list.size>1 && best.second-list[1].second<.15f) return@mapIndexedNotNull null
            val reverse=found.mapIndexed { i,region -> i to (FaceRegion.of(best.first)?.overlap(region) ?: 0f) }.sortedByDescending { it.second }
            if(reverse.first().first!=index || reverse.size>1 && reverse[0].second-reverse[1].second<.15f) null else index to best.first
        }.toMap()
    }
}

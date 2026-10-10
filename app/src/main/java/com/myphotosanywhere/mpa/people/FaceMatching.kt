package com.myphotosanywhere.mpa.people

import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.sqrt

object FaceMatching {
    const val DIMENSIONS=128
    // Conservative suggestion gates, not a calibrated identity guarantee.
    const val MIN_SIMILARITY=.72f
    const val MIN_MARGIN=.08f
    fun normalize(input: FloatArray): FloatArray {
        require(input.size==DIMENSIONS && input.all { it.isFinite() })
        val norm=sqrt(input.sumOf { it.toDouble()*it }.toFloat())
        require(norm>1e-8f)
        return FloatArray(input.size) { input[it]/norm }
    }
    fun encode(input: FloatArray): ByteArray = ByteBuffer.allocate(DIMENSIONS*4).order(ByteOrder.LITTLE_ENDIAN)
        .apply { normalize(input).forEach { putFloat(it) } }.array()
    fun decode(bytes: ByteArray): FloatArray {
        require(bytes.size==DIMENSIONS*4)
        val buffer=ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        return normalize(FloatArray(DIMENSIONS) { buffer.float })
    }
    fun cosine(a: FloatArray,b: FloatArray): Float {
        require(a.size==DIMENSIONS && b.size==DIMENSIONS)
        return a.indices.sumOf { a[it].toDouble()*b[it] }.toFloat().coerceIn(-1f,1f)
    }
    data class Assessment(val nearest: Pair<String,Float>?,val runnerUp: Pair<String,Float>?) {
        val accepted: Boolean get() = nearest?.second?.let { score ->
            score>=MIN_SIMILARITY && (runnerUp==null || score-runnerUp.second>=MIN_MARGIN)
        }==true
    }
    fun assess(query: FloatArray,confirmed: List<Pair<String,FloatArray>>): Assessment {
        val candidates=confirmed.groupBy { it.first }.map { (person,faces) ->
            person to faces.maxOf { cosine(query,it.second) }
        }.sortedWith(compareByDescending<Pair<String,Float>> { it.second }.thenBy { it.first })
        return Assessment(candidates.firstOrNull(),candidates.getOrNull(1))
    }
    fun suggest(query: FloatArray,confirmed: List<Pair<String,FloatArray>>): Pair<String,Float>? {
        val result=assess(query,confirmed)
        return result.nearest.takeIf { result.accepted }
    }
    /** Complete-link clusters prevent a chain of weak matches from merging unrelated faces. */
    fun clusters(faces: List<Pair<String,FloatArray>>,canGroup: (String,String)->Boolean = { _,_ -> true }): List<List<String>> {
        val groups=mutableListOf<MutableList<Pair<String,FloatArray>>>()
        faces.sortedBy { it.first }.forEach { candidate ->
            val matching=groups.filter { group -> group.all {
                canGroup(candidate.first,it.first) && cosine(candidate.second,it.second)>=MIN_SIMILARITY
            } }
            if(matching.size==1) matching.first().add(candidate) else groups.add(mutableListOf(candidate))
        }
        return groups.map { group -> group.map { it.first } }
    }
}

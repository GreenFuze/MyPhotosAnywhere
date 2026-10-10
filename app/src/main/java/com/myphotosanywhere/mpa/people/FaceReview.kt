package com.myphotosanywhere.mpa.people

import com.myphotosanywhere.mpa.data.FaceRecord

/** Local-only review data. Scores are cosine similarities, never identity probabilities. */
object FaceReview {
    enum class Status { NO_EMBEDDING, INVALID_EMBEDDING, DIFFERENT_MODEL, MANUAL_ONLY, NO_REFERENCES, WEAK_MATCH, AMBIGUOUS, SUGGESTED }
    data class Diagnostic(val status: Status,val named: FaceMatching.Assessment?=null,
        val nearestFaceId: String?=null,val nearestFaceScore: Float?=null)
    data class Result(val groups: List<List<String>>,val diagnostics: Map<String,Diagnostic>)

    private fun vectors(faces: List<FaceRecord>,modelVersion: String) =
        faces.filter { it.modelVersion==null || it.modelVersion==modelVersion }.mapNotNull { face ->
            face.embedding?.let { bytes -> runCatching { face.id to FaceMatching.decode(bytes) }.getOrNull() }
        }.toMap()

    fun analyze(faces: List<FaceRecord>,modelVersion: String): Result {
        val records=faces.associateBy { it.id }
        val vectors=vectors(faces,modelVersion)
        val unknown=faces.filter { it.personId==null }
        val eligible=unknown.filter { !it.corrected && it.id in vectors }
        val groups=FaceMatching.clusters(eligible.map { it.id to vectors.getValue(it.id) }) { a,b ->
            // Distinct detected faces in one photo cannot establish an automatic identity group.
            records.getValue(a).photoId!=records.getValue(b).photoId
        } + unknown.filter { it.corrected || it.id !in vectors }.map { listOf(it.id) }
        return Result(groups,assess(faces,modelVersion))
    }

    fun assess(faces: List<FaceRecord>,modelVersion: String): Map<String,Diagnostic> {
        val vectors=vectors(faces,modelVersion)
        val confirmed=faces.mapNotNull { face -> face.personId?.let { person -> vectors[face.id]?.let { person to it } } }
        val unknown=faces.filter { it.personId==null }
        val present=faces.groupBy { it.photoId }.mapValues { (_,rows) -> rows.mapNotNull { it.personId }.toSet() }
        return unknown.associate { face ->
            val vector=vectors[face.id]
            val assessment=vector?.let { query -> FaceMatching.assess(query,confirmed.filter { it.first !in present.getValue(face.photoId) }) }
            val status=when {
                face.embedding==null -> Status.NO_EMBEDDING
                face.modelVersion!=null && face.modelVersion!=modelVersion -> Status.DIFFERENT_MODEL
                vector==null -> Status.INVALID_EMBEDDING
                face.corrected -> Status.MANUAL_ONLY
                assessment?.nearest==null -> Status.NO_REFERENCES
                assessment.nearest.second<FaceMatching.MIN_SIMILARITY -> Status.WEAK_MATCH
                !assessment.accepted -> Status.AMBIGUOUS
                else -> Status.SUGGESTED
            }
            face.id to Diagnostic(status,assessment)
        }
    }

    fun diagnose(id: String,faces: List<FaceRecord>,modelVersion: String): Diagnostic? {
        val face=faces.find { it.id==id } ?: return null
        val diagnostic=assess(faces,modelVersion)[id] ?: return null
        val vectors=vectors(faces,modelVersion)
        val query=vectors[id] ?: return diagnostic
        val nearest=faces.asSequence().filter { it.id!=id && it.photoId!=face.photoId }.mapNotNull { other ->
            vectors[other.id]?.let { other.id to FaceMatching.cosine(query,it) }
        }.maxByOrNull { it.second }
        return diagnostic.copy(nearestFaceId=nearest?.first,nearestFaceScore=nearest?.second)
    }
}

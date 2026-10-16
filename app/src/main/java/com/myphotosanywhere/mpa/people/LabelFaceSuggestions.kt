package com.myphotosanywhere.mpa.people

import com.myphotosanywhere.mpa.data.FaceRecord
import com.myphotosanywhere.mpa.data.PhotoPersonLabel

/** Labels provide reviewable hints; only the user's confirmation assigns a detected face. */
internal class LabelFaceSuggestions {
    data class Candidate(val personId: String,val similarity: Float?)
    fun suggest(faces: List<FaceRecord>,members: List<PhotoPersonLabel>,model: String): Map<String,Candidate> {
        val byId=faces.associateBy { it.id }
        check(byId.size==faces.size)
        val byPhoto=faces.groupBy { it.photoId }
        val labels=members.filterNot { it.excluded }.groupBy { it.photoId }.mapValues { (_,rows) -> rows.map { it.personId }.toSet() }
        val direct=faces.filter { it.personId==null && !it.corrected && byPhoto.getValue(it.photoId).size==1 }
            .mapNotNull { face -> labels[face.photoId]?.singleOrNull()?.let { face.id to it } }.toMap()
        val vectors=faces.mapNotNull { face ->
            if(face.modelVersion!=null && face.modelVersion!=model) null else face.embedding?.let { bytes ->
                try { face.id to FaceMatching.decode(bytes) } catch(_: IllegalArgumentException) { null }
            }
        }.toMap()
        val confirmed=faces.mapNotNull { face -> face.personId?.let { person -> vectors[face.id]?.let { Seed(face.photoId,person,it) } } }
        val safeHints=direct.filter { (id,person) ->
            val face=byId.getValue(id)
            val nearest=vectors[id]?.let { query -> FaceMatching.suggest(query,confirmed.filter { it.photo!=face.photoId }.map { it.person to it.vector }) }
            nearest==null || nearest.first==person
        }
        val provisional=faces.mapNotNull { face ->
            if(face.modelVersion!=model || face.corrected) null else safeHints[face.id]?.let { person -> vectors[face.id]?.let { Seed(face.photoId,person,it) } }
        }
        val present=byPhoto.mapValues { (_,rows) -> rows.mapNotNull { it.personId }.toSet() }
        val result=linkedMapOf<String,Candidate>()
        for(face in faces.filter { it.personId==null && !it.corrected }) {
            val query=vectors[face.id]
            val hint=direct[face.id]
            val reviewed=confirmed.filter { it.photo!=face.photoId && it.person !in present.getValue(face.photoId) }
            if(hint!=null) {
                if(safeHints[face.id]==hint) result[face.id]=Candidate(hint,null)
            } else if(query!=null) {
                val seeds=(reviewed+provisional.filter { it.photo!=face.photoId && it.person !in present.getValue(face.photoId) })
                FaceMatching.suggest(query,seeds.map { it.person to it.vector })?.let { result[face.id]=Candidate(it.first,it.second) }
            }
        }
        return result
    }
    private data class Seed(val photo: String,val person: String,val vector: FloatArray)
}

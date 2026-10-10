package com.myphotosanywhere.mpa.people

import com.myphotosanywhere.mpa.data.FaceRecord
import org.junit.Assert.*
import org.junit.Test

class FaceReviewTest {
    private val version="test-model"
    private fun vector(a: Float=1f,b: Float=0f)=FaceMatching.encode(FloatArray(128).apply { this[0]=a; this[1]=b })
    private fun face(id: String,photo: String=id,embedding: ByteArray?=vector(),person: String?=null)=
        FaceRecord(id,photo,"",embedding,personId=person,modelVersion=version)

    @Test fun twoAppearancesGroupWithoutEnrollmentAndRegardlessOfInputOrder() {
        val rows=listOf(face("a"),face("b",embedding=vector(.98f,.02f)),face("c",embedding=vector(0f,1f)))
        assertEquals(listOf(listOf("a","b"),listOf("c")),FaceReview.analyze(rows,version).groups)
        assertEquals(FaceReview.analyze(rows,version).groups,FaceReview.analyze(rows.reversed(),version).groups)
    }
    @Test fun cooccurringFacesCannotGroupOrSuggestThePersonAlreadyInThePhoto() {
        val rows=listOf(face("a","together"),face("b","together"))
        assertEquals(listOf(listOf("a"),listOf("b")),FaceReview.analyze(rows,version).groups)
        val named=rows.map { if(it.id=="a") it.copy(personId="sam") else it }
        assertEquals(FaceReview.Status.NO_REFERENCES,FaceReview.assess(named,version).getValue("b").status)
    }
    @Test fun namingOneAppearanceSuggestsTheOtherAcrossPhotos() {
        val rows=listOf(face("a",person="sam"),face("b"))
        val diagnostic=FaceReview.assess(rows,version).getValue("b")
        assertEquals(FaceReview.Status.SUGGESTED,diagnostic.status)
        assertEquals("sam",diagnostic.named?.nearest?.first)
    }
    @Test fun missingInvalidAndIncompatibleEmbeddingsStaySeparateAndExplainWhy() {
        val rows=listOf(face("a"),face("missing",embedding=null),face("invalid",embedding=byteArrayOf(1)),
            face("old").copy(modelVersion="other"))
        val review=FaceReview.analyze(rows,version)
        assertTrue(review.groups.all { it.size==1 })
        assertEquals(FaceReview.Status.NO_EMBEDDING,review.diagnostics.getValue("missing").status)
        assertEquals(FaceReview.Status.INVALID_EMBEDDING,review.diagnostics.getValue("invalid").status)
        assertEquals(FaceReview.Status.DIFFERENT_MODEL,review.diagnostics.getValue("old").status)
    }
    @Test fun weakAndAmbiguousMatchesExposeActualScoresWithoutPromotingThem() {
        val weak=listOf(face("a",person="sam"),face("b",embedding=vector(0f,1f)))
        val first=FaceReview.assess(weak,version).getValue("b")
        assertEquals(FaceReview.Status.WEAK_MATCH,first.status)
        assertEquals(0f,requireNotNull(first.named?.nearest?.second),.0001f)
        val ambiguous=listOf(face("a",person="sam"),face("b",person="alex"),face("c"))
        assertEquals(FaceReview.Status.AMBIGUOUS,FaceReview.assess(ambiguous,version).getValue("c").status)
    }
    @Test fun correctionsRemainManualAndNearestUnnamedFaceCanBeInspected() {
        val rows=listOf(face("a").copy(corrected=true),face("b"))
        val review=FaceReview.analyze(rows,version)
        assertTrue(review.groups.all { it.size==1 })
        val diagnostic=requireNotNull(FaceReview.diagnose("a",rows,version))
        assertEquals(FaceReview.Status.MANUAL_ONLY,diagnostic.status)
        assertEquals("b",diagnostic.nearestFaceId)
        assertEquals(1f,requireNotNull(diagnostic.nearestFaceScore),.0001f)
    }
}

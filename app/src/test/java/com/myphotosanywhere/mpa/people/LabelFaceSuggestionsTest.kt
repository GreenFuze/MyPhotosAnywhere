package com.myphotosanywhere.mpa.people

import com.myphotosanywhere.mpa.data.FaceRecord
import com.myphotosanywhere.mpa.data.PhotoPersonLabel
import org.junit.Assert.*
import org.junit.Test
import kotlin.math.cos
import kotlin.math.sin

class LabelFaceSuggestionsTest {
    private val subject=LabelFaceSuggestions()
    private fun face(id: String,photo: String=id,angle: Double=0.0)=FaceRecord(id,photo,"",FaceMatching.encode(
        FloatArray(FaceMatching.DIMENSIONS) { when(it) { 0->cos(angle).toFloat();1->sin(angle).toFloat();else->0f } }),modelVersion="model")
    private fun member(photo: String,person: String="sam",alias: String=person)=PhotoPersonLabel(photo,alias,person)
    @Test fun labelHintDoesNotAssignOrClaimBiometricConfidence() {
        val original=face("one").copy(embedding=null)
        assertEquals(LabelFaceSuggestions.Candidate("sam",null),subject.suggest(listOf(original),listOf(member("one")),"model")["one"])
        assertNull(original.personId)
    }
    @Test fun compatibleHintSuggestsSameIdentityOnNewPhotoWithoutAssignment() {
        val faces=listOf(face("seed"),face("new"))
        val suggestion=subject.suggest(faces,listOf(member("seed")),"model").getValue("new")
        assertEquals("sam",suggestion.personId);assertTrue(requireNotNull(suggestion.similarity)>=FaceMatching.MIN_SIMILARITY)
        assertTrue(faces.all { it.personId==null })
    }
    @Test fun severalAliasesOfOneIdentityAreNotCompetingPeople() {
        val hints=listOf(member("a",alias="old"),member("b",alias="renamed"))
        assertEquals("sam",subject.suggest(listOf(face("a"),face("b"),face("new")),hints,"model")["new"]?.personId)
    }
    @Test fun ambiguousPhotoLabelsOrFacesCannotSeedSuggestions() {
        val faces=listOf(face("a","group"),face("b","group"),face("c","labels"),face("new"))
        val labels=listOf(member("group"),member("labels"),member("labels","other"))
        assertTrue(subject.suggest(faces,labels,"model").isEmpty())
    }
    @Test fun rejectedFaceNeitherSeedsNorReceivesAutomaticSuggestions() {
        val faces=listOf(face("seed").copy(corrected=true),face("new").copy(corrected=true),face("other"))
        assertTrue(subject.suggest(faces,listOf(member("seed")),"model").isEmpty())
    }
    @Test fun invalidOrDifferentModelEmbeddingCannotSeedNewPhoto() {
        for(seed in listOf(face("seed").copy(embedding=byteArrayOf(3)),face("seed").copy(modelVersion="old"))) {
            val result=subject.suggest(listOf(seed,face("new")),listOf(member("seed")),"model")
            assertEquals(LabelFaceSuggestions.Candidate("sam",null),result["seed"]);assertNull(result["new"])
        }
    }
    @Test fun closeCompetingIdentitiesRemainAmbiguous() {
        val result=subject.suggest(listOf(face("a",angle=.02),face("b",angle=-.02),face("new")),listOf(member("a"),member("b","other")),"model")
        assertNull(result["new"])
    }
    @Test fun reviewedContradictionPreventsPromotionOfLabelHint() {
        val result=subject.suggest(listOf(face("reviewed").copy(personId="other"),face("seed")),listOf(member("seed")),"model")
        assertNull(result["seed"])
    }
    @Test fun personAlreadyConfirmedInPhotoCannotBeSuggestedForAnotherFace() {
        val faces=listOf(face("seed").copy(personId="sam"),face("assigned","group").copy(personId="sam"),face("new","group"))
        assertNull(subject.suggest(faces,emptyList(),"model")["new"])
    }
    @Test fun contradictedHintCannotSeedAnotherPhotoWhenReviewedIdentityIsAlreadyPresent() {
        val faces=listOf(face("reviewed").copy(personId="other"),face("seed"),
            face("assigned","group").copy(personId="other"),face("new","group"))
        val result=subject.suggest(faces,listOf(member("seed")),"model")
        assertNull(result["seed"]);assertNull(result["new"])
    }
    @Test fun excludedMembershipCannotSeedSuggestions() {
        assertTrue(subject.suggest(listOf(face("seed"),face("new")),listOf(member("seed").copy(excluded=true)),"model").isEmpty())
    }
    @Test fun duplicateFaceIdentitiesFailImmediately() {
        assertThrows(IllegalStateException::class.java) { subject.suggest(listOf(face("same"),face("same")),emptyList(),"model") }
    }
}

package com.myphotosanywhere.mpa.people

import org.junit.Assert.*
import org.junit.Test

class FaceMatchingTest {
    private fun vector(a: Float=1f,b: Float=0f)=FaceMatching.normalize(FloatArray(128).apply { this[0]=a;this[1]=b })
    @Test fun embeddingsRoundTripAndNormalize() {
        assertArrayEquals(vector(),FaceMatching.decode(FaceMatching.encode(vector())),1e-6f)
    }
    @Test fun ambiguousDifferentIdentitiesStayUnknown() {
        assertNull(FaceMatching.suggest(vector(),listOf("Alice" to vector(),"Bob" to vector(.99f,.01f))))
    }
    @Test fun severalExamplesOfOnePersonDoNotCountAsCompetingPeople() {
        assertEquals("Alice",FaceMatching.suggest(vector(),listOf("Alice" to vector(),"Alice" to vector(.95f,.05f),"Bob" to vector(0f,1f)))?.first)
    }
    @Test fun distantFacesStayUnknown() { assertNull(FaceMatching.suggest(vector(),listOf("Bob" to vector(0f,1f)))) }
    @Test fun clusteringDoesNotMergeThroughAnIntermediateFace() {
        val faces=listOf("A" to vector(1f,0f),"B" to vector(.8f,.6f),"C" to vector(.3f,.95f))
        assertEquals(listOf(listOf("A","B"),listOf("C")),FaceMatching.clusters(faces))
    }
    @Test(expected=IllegalArgumentException::class) fun invalidEmbeddingIsRejected() { FaceMatching.normalize(FloatArray(128)) }
}

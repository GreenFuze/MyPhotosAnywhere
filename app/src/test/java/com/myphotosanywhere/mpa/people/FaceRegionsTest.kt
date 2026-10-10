package com.myphotosanywhere.mpa.people

import com.myphotosanywhere.mpa.data.FaceRecord
import org.junit.Assert.*
import org.junit.Test

class FaceRegionsTest {
    private fun record(id: String,r: FaceRegion)=FaceRecord(id,"asset","crop",null,"person",corrected=true,
        left=r.left,top=r.top,right=r.right,bottom=r.bottom)
    @Test fun detectorOrderChangesKeepTheSameStableReviewedIdentity() {
        val left=FaceRegion(.1f,.1f,.3f,.4f); val right=FaceRegion(.6f,.1f,.8f,.4f)
        val matches=FaceRegions.matches(listOf(right,left),listOf(record("left",left),record("right",right)))
        assertEquals("right",matches[0]?.id); assertEquals("left",matches[1]?.id)
        assertEquals("person",matches[0]?.personId); assertTrue(matches[0]?.corrected==true)
    }
    @Test fun ambiguousOverlapsAndMissingLegacyGeometryAreNotAssignedByOrder() {
        val region=FaceRegion(.1f,.1f,.4f,.4f)
        assertTrue(FaceRegions.matches(listOf(region),listOf(record("one",region),record("two",region))).isEmpty())
        assertTrue(FaceRegions.matches(listOf(region),listOf(FaceRecord("legacy:0","asset","crop",null,"person",corrected=true))).isEmpty())
        assertTrue(FaceRegions.matches(listOf(region,region),listOf(record("one",region))).isEmpty())
    }
    @Test fun distantRegionsAndNonFiniteGeometryAreRejected() {
        assertTrue(FaceRegions.matches(listOf(FaceRegion(.6f,.6f,.8f,.8f)),listOf(record("left",FaceRegion(.1f,.1f,.3f,.3f)))).isEmpty())
        assertThrows(IllegalArgumentException::class.java) { FaceRegion(Float.NaN,0f,1f,1f) }
    }
}

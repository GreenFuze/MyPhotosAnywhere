package com.myphotosanywhere.mpa.thumbnails

import org.junit.Assert.*
import org.junit.Test

class ThumbnailPolicyTest {
    private val policy=ThumbnailPolicy(1024L*1024*1024)
    @Test fun cropDemandPreservesPortraitDetailAndCapsExtremeShapes() {
        assertEquals(768,policy.dimension(360,360,.5f))
        assertEquals(512,policy.dimension(360,200,2f))
        assertEquals(1536,policy.dimension(5000,5000,1000f))
        assertEquals(256,policy.dimension(120,120,1f))
    }
    @Test fun smallHeapsAndConfiguredBudgetsRemainBounded() {
        val small=ThumbnailPolicy(16L*1024*1024)
        assertTrue(small.maximum in 256..363)
        assertEquals(small.maximum,small.dimension(2048,2048,1f))
        assertEquals(0L,policy.diskBudget(0))
        assertEquals(1024L,policy.diskBudget(4096))
        assertEquals(64L*1024*1024,policy.diskBudget(Long.MAX_VALUE))
    }
    @Test fun cacheKeysSeparateIdentityRevisionAndSize() {
        val key=policy.key("photo:one","sha1:orientation1",768)
        assertTrue(key.matches(Regex("[0-9a-f]{64}")))
        assertEquals(key,policy.key("photo:one","sha1:orientation1",768))
        assertNotEquals(key,policy.key("photo:two","sha1:orientation1",768))
        assertNotEquals(key,policy.key("photo:one","sha2:orientation6",768))
        assertNotEquals(key,policy.key("photo:one","sha1:orientation1",1024))
        assertFalse(ThumbnailPolicy.currentProvider("1","1"))
        assertTrue(ThumbnailPolicy.currentProvider("q2:1","1"))
        assertFalse(ThumbnailPolicy.currentProvider("q2:1","2"))
        assertFalse(ThumbnailPolicy.currentProvider(null,null))
    }
    @Test fun invalidRequestsFailFast() {
        for(action in listOf<()->Unit>({ policy.dimension(0,10,1f) },{ policy.dimension(10,-1,1f) },
            { policy.dimension(10,10,Float.NaN) },{ policy.dimension(10,10,0f) },
            { policy.key("","revision",512) },{ policy.key("photo:one","revision",1537) },{ policy.diskBudget(-1) })) {
            try { action();fail("Expected invalid request rejection") } catch(_: IllegalArgumentException) { }
        }
    }
}

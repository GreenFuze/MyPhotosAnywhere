package com.myphotosanywhere.mpa.importing

import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.metadata.*
import org.junit.Assert.*
import org.junit.Test

class TakeoutCodecTest {
    private val codec=TakeoutCodec()
    private val raw="""{"title":"猫.jpg","description":"Family <holiday> & friends","photoTakenTime":{"timestamp":"123"},"geoData":{"latitude":0.0,"longitude":0.0},"people":[{"name":"Sam"}],"unknown":{"keep":true}}"""
    private fun photo()=Photo("id","猫.jpg","","","image/jpeg",8,1,"a".repeat(64))

    @Test fun unicodeZeroGpsAndNamesRemainUnconfirmed() {
        val metadata=codec.parse(raw)
        assertEquals(0.0,metadata.latitude!!,0.0); assertEquals(0.0,metadata.longitude!!,0.0)
        assertEquals(123000L,metadata.captured); assertEquals(listOf("Sam"),metadata.people)
        val applied=codec.apply(photo(),metadata)
        assertEquals("",applied.labels)
        assertTrue(applied.importedMetadata.contains("unknown"))
    }
    @Test fun reviewedMpaFieldsWinAndConflictingReimportFails() {
        val applied=codec.apply(photo().copy(capturedAt=555,latitude=10.0,longitude=20.0,placeLabel="Reviewed",labels="cat"),codec.parse(raw))
        assertEquals(555L,applied.capturedAt); assertEquals(10.0,applied.latitude!!,0.0)
        assertEquals("Reviewed",applied.placeLabel); assertEquals("cat",applied.labels)
        assertEquals(applied,codec.apply(applied,codec.parse(raw)))
        assertThrows(IllegalArgumentException::class.java) { codec.apply(applied,codec.parse(raw.replace("123","124"))) }
    }
    @Test fun matchingNeverCrossesDirectoryAndCollisionsStayAmbiguous() {
        val metadata=codec.parse(raw)
        assertEquals(listOf("album/猫.jpg"),codec.candidates("album/猫.jpg.supplemental-metadata.json",metadata,listOf("other/猫.jpg","album/猫.jpg")))
        val pair=codec.candidates("album/other.jpg.json",metadata,listOf("album/猫.jpg","album/other.jpg"))
        assertTrue(pair.isEmpty())
    }
    @Test fun rejectsTraversalAndUnboundedMetadata() {
        listOf("../p.jpg","a/../p.jpg","/p.jpg","C:/p.jpg","a\\p.jpg","a/./p.jpg").forEach {
            assertThrows(IllegalArgumentException::class.java) { TakeoutCodec.safePath(it) }
        }
        assertThrows(IllegalArgumentException::class.java) { codec.parse(" ".repeat(TakeoutCodec.MAX_JSON_BYTES+1)) }
        assertThrows(IllegalArgumentException::class.java) { codec.parse(raw.replace("0.0","999.0")) }
    }
    @Test fun numberedAndTruncatedMatchesNeedConsistentTitles() {
        val numbered=codec.parse(raw.replace("猫.jpg","photo.jpg"))
        assertEquals(listOf("album/photo(1).jpg"),codec.candidates("album/photo.jpg(1).json",numbered,listOf("album/photo.jpg","album/photo(1).jpg")))
        val long="x".repeat(60)+".jpg"
        val metadata=codec.parse(raw.replace("猫.jpg",long))
        assertEquals(listOf("album/$long"),codec.candidates("album/${long.take(46)}.json",metadata,listOf("album/$long","other/$long")))
        assertThrows(IllegalArgumentException::class.java) { codec.parse("{\"title\":\"photo.jpg\"}") }
    }
    @Test fun fullSupplementalMetadataSurvivesXmpRoundTrip() {
        val applied=codec.apply(photo(),codec.parse(raw))
        val asset=PortableAsset("id","remote",applied.sha256,8,false,false,"",applied.latitude,applied.longitude,
            applied.capturedAt,null,null,null,"",null,emptyList(),emptyList(),applied.importedMetadata)
        val catalog=XmpCatalog("account","folder",listOf(asset),emptyList(),emptyList())
        assertEquals(catalog,XmpCodec.decode(XmpCodec.encode(catalog)))
    }
}

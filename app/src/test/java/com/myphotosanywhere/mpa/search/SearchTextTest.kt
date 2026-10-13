package com.myphotosanywhere.mpa.search

import org.junit.Assert.*
import org.junit.Test
import java.time.Instant
import java.time.ZoneId

class SearchTextTest {
    private val zone=ZoneId.of("UTC")
    private val photo=SearchDocument("ÉTÉ_%'2026.jpg","image/jpeg",true,Instant.parse("2026-03-28T23:30:00Z").toEpochMilli(),
        "Tel Aviv","Beach, Dog","Takeout title: Wedding","Alice כינרת","Family trip",true,true,0.0,0.0)
    @Test fun preservesAllTokenMetadataMatching() { assertTrue(SearchText("alice aviv dog wedding").matches(photo,zone));assertFalse(SearchText("alice missing").matches(photo,zone)) }
    @Test fun unicodeAndHebrewRemainLiteralCaseInsensitiveMatches() { assertTrue(SearchText("été כינרת").matches(photo,zone)) }
    @Test fun punctuationIsNotSqlOrWildcardSyntax() { assertTrue(SearchText("_%'").matches(photo,zone));assertFalse(SearchText("' OR 1=1 --").matches(photo,zone)) }
    @Test fun knownPeopleAndAlbumNamesAreIndexedMetadata() { assertTrue(SearchText("alice family trip").matches(photo,zone)) }
    @Test fun calendarSearchUsesTheChosenDeviceZone() { assertTrue(SearchText("2026-03-29").matches(photo,ZoneId.of("Asia/Jerusalem")));assertFalse(SearchText("2026-03-29").matches(photo,zone)) }
    @Test fun existingFavoriteTypeEditAndGpsKeywordsCompose() { assertTrue(SearchText("favorites image edit gps").matches(photo,zone));assertFalse(SearchText("video").matches(photo,zone));assertFalse(SearchText("gps").matches(photo.copy(latitude=Double.NaN),zone)) }
    @Test fun remoteNameAndMimeMatchingDoesNotClaimUnavailableLabels() {
        val remote=photo.copy(local=false,name="beach dog.png",mime="image/png")
        assertTrue(SearchText("beach dog").matches(remote,zone));assertFalse(SearchText("dog beach").matches(remote,zone));assertFalse(SearchText("Alice").matches(remote,zone));assertTrue(SearchText("image/png").matches(remote,zone))
    }
    @Test fun whitespaceAndEmptyQueriesArePredictable() { assertTrue(SearchText("  alice\t dog  ").matches(photo,zone));assertTrue(SearchText(" ").matches(photo,zone)) }
}

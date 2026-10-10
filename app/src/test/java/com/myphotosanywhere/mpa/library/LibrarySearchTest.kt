package com.myphotosanywhere.mpa.library

import com.myphotosanywhere.mpa.data.Photo
import org.junit.Assert.*
import org.junit.Test

class LibrarySearchTest {
    private val photo=Photo("one","holiday.jpg","/original","/thumb","image/jpeg",1,1700000000000,"sha",
        favorite=true,placeLabel="Tel Aviv",labels="Beach, Dog")
    @Test fun searchCombinesPeoplePlacesAndObjects() { assertTrue(LibrarySearch.matches(photo,"alice aviv dog",listOf("Alice"))) }
    @Test fun dateSearchUsesCaptureTime() { assertTrue(LibrarySearch.matches(photo.copy(capturedAt=946728000000),"2000-01")) }
    @Test fun missingGpsIsUnknownRatherThanZero() { assertFalse(LibrarySearch.matches(photo,"gps")) }
    @Test fun invalidCoordinatesAreRejectedAndZeroIsValid() {
        assertFalse(LibrarySearch.validCoordinates(Double.NaN,0.0));assertFalse(LibrarySearch.validCoordinates(0.0,181.0))
        assertTrue(LibrarySearch.validCoordinates(0.0,0.0))
    }
    @Test fun typeAndFavoritesFiltersCompose() {
        assertFalse(LibrarySearch.matches(photo,"favorite video")); assertTrue(LibrarySearch.matches(photo,"favorite image"))
    }
}

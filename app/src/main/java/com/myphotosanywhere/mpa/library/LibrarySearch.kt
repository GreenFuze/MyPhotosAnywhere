package com.myphotosanywhere.mpa.library

import com.myphotosanywhere.mpa.data.Photo
import java.time.ZoneId

object LibrarySearch {
    fun matches(photo: Photo,query: String,peopleNames: List<String> = emptyList()): Boolean {
        return com.myphotosanywhere.mpa.search.SearchText(query).matches(
            com.myphotosanywhere.mpa.search.SearchDocument.from(photo,peopleNames,emptyList()),ZoneId.systemDefault())
    }

    fun validCoordinates(latitude: Double?,longitude: Double?) = latitude!=null && longitude!=null &&
        latitude.isFinite() && longitude.isFinite() && latitude in -90.0..90.0 && longitude in -180.0..180.0
}

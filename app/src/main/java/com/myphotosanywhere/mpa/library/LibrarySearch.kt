package com.myphotosanywhere.mpa.library

import com.myphotosanywhere.mpa.data.Photo
import java.time.Instant
import java.time.ZoneId

object LibrarySearch {
    fun matches(photo: Photo,query: String,peopleNames: List<String> = emptyList()): Boolean {
        val date=Instant.ofEpochMilli(photo.capturedAt ?: photo.importedAt).atZone(ZoneId.systemDefault()).toLocalDate().toString()
        val haystack=listOf(photo.displayName,photo.placeLabel,photo.labels,date,photo.mimeType,peopleNames.joinToString(" ")).joinToString(" ").lowercase()
        return query.trim().lowercase().split(Regex("\\s+")).filter { it.isNotBlank() }.all { token ->
            when(token) {
                "favorite", "favorites" -> photo.favorite
                "video", "videos" -> photo.mimeType.startsWith("video/")
                "image", "images", "photo", "photos" -> photo.mimeType.startsWith("image/")
                "edit", "edits" -> photo.parentId!=null
                "gps", "location" -> validCoordinates(photo.latitude,photo.longitude)
                else -> token in haystack
            }
        }
    }
    fun validCoordinates(latitude: Double?,longitude: Double?) = latitude!=null && longitude!=null &&
        latitude.isFinite() && longitude.isFinite() && latitude in -90.0..90.0 && longitude in -180.0..180.0
}

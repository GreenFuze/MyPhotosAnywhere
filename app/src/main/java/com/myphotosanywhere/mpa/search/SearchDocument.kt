package com.myphotosanywhere.mpa.search

import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.library.LibrarySearch
import java.time.Instant
import java.time.ZoneId

/** Ephemeral indexed metadata only. Neither matching nor suggestions retrieve original bytes. */
internal data class SearchDocument(val name: String,val mime: String,val local: Boolean,
    val timestamp: Long?=null,val place: String="",val labels: String="",val metadata: String="",
    val people: String="",val albums: String="",val favorite: Boolean=false,val edited: Boolean=false,
    val latitude: Double?=null,val longitude: Double?=null) {
    companion object {
        fun from(photo: Photo,people: List<String>,albums: List<String>)=SearchDocument(photo.displayName,photo.mimeType,true,
            photo.capturedAt ?: photo.importedAt,photo.placeLabel,photo.labels,photo.importedMetadata,
            people.joinToString(" "),albums.joinToString(" "),photo.favorite,photo.parentId!=null,photo.latitude,photo.longitude)
    }
}

/** Compile tokens once per query; keep Kotlin's Unicode case handling and literal punctuation. */
internal class SearchText(query: String) {
    private val text=query.trim()
    private val tokens=text.lowercase().split(Regex("\\s+")).filter(String::isNotBlank)
    fun matches(document: SearchDocument,zone: ZoneId): Boolean {
        if(!document.local) return text.isBlank() || document.name.contains(text,true) || document.mime.contains(text,true)
        val date=document.timestamp?.let { Instant.ofEpochMilli(it).atZone(zone).toLocalDate().toString() }.orEmpty()
        val haystack=listOf(document.name,document.place,document.labels,date,document.mime,document.people,document.albums,document.metadata).joinToString(" ").lowercase()
        return tokens.all { token -> when(token) {
            "favorite","favorites" -> document.favorite
            "video","videos" -> document.mime.startsWith("video/")
            "image","images","photo","photos" -> document.mime.startsWith("image/")
            "edit","edits" -> document.edited
            "gps","location" -> LibrarySearch.validCoordinates(document.latitude,document.longitude)
            else -> token in haystack
        } }
    }
}

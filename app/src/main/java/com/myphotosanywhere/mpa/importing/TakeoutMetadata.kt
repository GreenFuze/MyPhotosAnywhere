package com.myphotosanywhere.mpa.importing

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.data.Photo
import java.util.Locale

data class TakeoutMetadata(val title: String, val description: String, val captured: Long?,
    val latitude: Double?, val longitude: Double?, val people: List<String>, val raw: String)

/** Keep the complete bounded provider record, including fields MPA does not understand yet. */
class TakeoutCodec {
    fun parse(raw: String): TakeoutMetadata {
        require(raw.toByteArray(Charsets.UTF_8).size <= MAX_JSON_BYTES) { "Takeout metadata exceeds 128 KiB; JSON preserved." }
        var depth=0; var quoted=false; var escaped=false
        for(char in raw) {
            if(quoted) { if(escaped) escaped=false else if(char=='\\') escaped=true else if(char=='\"') quoted=false }
            else when(char) { '\"' -> quoted=true; '{','[' -> { depth++; require(depth<=32) { "Takeout metadata is too deeply nested." } }; '}',']' -> depth-- }
        }
        val json = JsonParser.parseString(raw).asJsonObject
        require(listOf("photoTakenTime","creationTime","geoData","geoDataExif","googlePhotosOrigin").any { json.has(it) }) {
            "Not a Google Photos media sidecar. Album or unrelated JSON is preserved."
        }
        fun JsonObject.text(key: String) = get(key)?.takeUnless { it.isJsonNull }?.asString
        val title = json.text("title") ?: error("Not a Google Photos media sidecar.")
        require(title.isNotBlank() && title.length <= 1024 && !title.contains('/') && !title.contains('\\'))
        val captured = json.getAsJsonObject("photoTakenTime")?.text("timestamp")?.toLong()?.let { Math.multiplyExact(it, 1000) }
        val geo = json.getAsJsonObject("geoData") ?: json.getAsJsonObject("geoDataExif")
        val lat = geo?.get("latitude")?.asDouble
        val lon = geo?.get("longitude")?.asDouble
        require((lat == null) == (lon == null))
        require(lat == null || lat.isFinite() && lon!!.isFinite() && lat in -90.0..90.0 && lon in -180.0..180.0)
        val people = json.getAsJsonArray("people")?.mapNotNull { it.asJsonObject.text("name") } ?: emptyList()
        require(people.size <= 100 && people.all { it.length <= 200 })
        return TakeoutMetadata(title, json.text("description") ?: "", captured, lat, lon, people, raw)
    }

    /** Reviewed/existing values win. Incoming conflicts remain visible in the retained provenance. */
    fun apply(photo: Photo, incoming: TakeoutMetadata): Photo {
        val old = if (photo.importedMetadata.isBlank()) JsonObject() else JsonParser.parseString(photo.importedMetadata).asJsonObject
        val google = JsonParser.parseString(incoming.raw).asJsonObject
        // Conflicting reimports cannot erase the previous source record or allow JSON cleanup.
        val previous = old.get("googleTakeout")
        require(previous == null || previous == google) { "Conflicting Takeout metadata. Existing annotations and Google JSON were preserved." }
        old.add("googleTakeout", google)
        val result = old.toString()
        require(result.toByteArray(Charsets.UTF_8).size <= MAX_JSON_BYTES + 1024)
        return photo.copy(capturedAt = photo.capturedAt ?: incoming.captured,
            latitude = photo.latitude ?: incoming.latitude, longitude = photo.longitude ?: incoming.longitude,
            importedMetadata = result)
    }

    fun candidates(sidecarPath: String, metadata: TakeoutMetadata, media: List<String>): List<String> {
        val directory = sidecarPath.substringBeforeLast('/', "")
        val name = sidecarPath.substringAfterLast('/')
        val stem = name.removeSuffix(".json").replace(Regex("\\.supplemental-metadata(?=\\(\\d+\\)$|$)"),"")
        val normalized=Regex("^(.*)(\\.[^.]+)\\((\\d+)\\)$").matchEntire(stem)?.let { "${it.groupValues[1]}(${it.groupValues[3]})${it.groupValues[2]}" } ?: stem
        val unnumbered=normalized.replace(Regex("\\(\\d+\\)(?=\\.[^.]+$)"),"")
        return media.filter { path ->
            val file=path.substringAfterLast('/')
            path.substringBeforeLast('/', "") == directory &&
                (file==normalized && metadata.title in listOf(normalized,stem,unnumbered) ||
                    // Google truncates long sidecar stems. A title is still required; collisions stay unresolved.
                    stem.length >= 40 && file==metadata.title && file.startsWith(stem) &&
                    metadata.title.startsWith(stem))
        }.distinct()
    }

    companion object {
        const val MAX_JSON_BYTES = 128 * 1024
        fun validatePortable(raw: String) {
            if(raw.isBlank()) return
            require(raw.toByteArray(Charsets.UTF_8).size<=MAX_JSON_BYTES+1024)
            var depth=0; var quoted=false; var escaped=false
            for(char in raw) {
                if(quoted) { if(escaped) escaped=false else if(char=='\\') escaped=true else if(char=='\"') quoted=false }
                else when(char) { '\"' -> quoted=true; '{','[' -> { depth++; require(depth<=34) }; '}',']' -> depth-- }
            }
            val json=JsonParser.parseString(raw).asJsonObject
            json.get("googleTakeout")?.let { TakeoutCodec().parse(it.toString()) }
            json.get("googlePhotosPicker")?.let { require(it.isJsonObject) }
        }
        fun safePath(path: String): String {
            require(path.length <= 2048 && path.isNotBlank() && !path.startsWith('/') &&
                !path.contains('\\') && !path.contains('\u0000') && !Regex("^[A-Za-z]:").containsMatchIn(path) &&
                path.split('/').none { it == ".." || it == "." }) { "Unsafe archive path." }
            return path
        }
        fun mime(path: String): String? = when (path.substringAfterLast('.').lowercase(Locale.ROOT)) {
            "jpg", "jpeg" -> "image/jpeg"; "png" -> "image/png"; "webp" -> "image/webp"
            "heic", "heif" -> "image/heic"; "gif" -> "image/gif"; "avif" -> "image/avif"
            "mp4", "m4v" -> "video/mp4"; "mov" -> "video/quicktime"; "webm" -> "video/webm"
            else -> null
        }
    }
}

package com.myphotosanywhere.mpa.drive

import com.google.gson.JsonElement
import com.google.gson.JsonParser
import java.io.IOException
import java.util.Collections

/** Transport observations only. Membership, pinned account, immutable reads and policy
 * admission belong to the catalog consumer; a listed object grants no authority. */
internal class DriveWorkspaceMetadata(val id: String,val mimeType: String,val version: Long,val bytes: Long?,
    parents: List<String>,val trashed: Boolean,val sha256: String?,val downloadable: Boolean?) {
    val parents: List<String> = Collections.unmodifiableList(parents.sorted())
    init {
        opaque(id); if (mimeType.length !in 1..128 || mimeType.any { it.code !in 33..126 } || version < 1 || bytes != null && bytes < 0)
            throw IOException("Drive returned invalid metadata workspace fields.")
        if (parents.size > 16 || parents.distinct().size != parents.size) throw IOException("Drive returned invalid workspace parents.")
        parents.forEach(::opaque)
        if (sha256 != null && !sha256.matches(Regex("[0-9a-f]{64}"))) throw IOException("Drive returned an invalid workspace checksum.")
    }
    fun sameRevision(other: DriveWorkspaceMetadata): Boolean = id == other.id && mimeType == other.mimeType && version == other.version &&
        bytes == other.bytes && parents == other.parents && trashed == other.trashed && sha256 == other.sha256 && downloadable == other.downloadable
    companion object {
        fun opaque(value: String) { if (!value.matches(Regex("[A-Za-z0-9_-]{1,256}")) || value == "root") throw IOException("A concrete opaque Drive identity is required.") }
        fun decode(bytes: ByteArray): DriveWorkspaceMetadata = parse(DriveWorkspacePage.json(bytes))
        internal fun parse(value: JsonElement): DriveWorkspaceMetadata {
            if (!value.isJsonObject) throw IOException("Drive workspace metadata must be an object.")
            val parents = value.asJsonObject["parents"]
            if (parents != null && (!parents.isJsonArray || parents.asJsonArray.size() > 16)) throw IOException("Invalid Drive workspace parents.")
            val trashed = value.asJsonObject["trashed"]
            if (trashed == null || !trashed.isJsonPrimitive || !trashed.asJsonPrimitive.isBoolean) throw IOException("Drive did not return the workspace trash state.")
            val capabilities = value.asJsonObject["capabilities"]
            if (capabilities != null && !capabilities.isJsonObject) throw IOException("Invalid Drive workspace capabilities.")
            val canDownload = capabilities?.asJsonObject?.get("canDownload")?.let {
                if (!it.isJsonPrimitive || !it.asJsonPrimitive.isBoolean) throw IOException("Invalid Drive download capability.")
                it.asBoolean
            }
            return DriveWorkspaceMetadata(text(value,"id"),text(value,"mimeType"),decimal(text(value,"version"),false),
                optionalText(value,"size")?.let { decimal(it,true) }, parents?.asJsonArray?.map { scalar(it) } ?: emptyList(),
                trashed.asBoolean,optionalText(value,"sha256Checksum"),canDownload)
        }
        fun account(bytes: ByteArray): DriveUser {
            val root = DriveWorkspacePage.json(bytes)
            if (!root.isJsonObject) throw IOException("Drive account response must be an object.")
            val user = root.asJsonObject["user"]
            if (user == null || !user.isJsonObject) throw IOException("Drive did not return the actual account.")
            val id = text(user,"permissionId"); opaque(id)
            val email = text(user,"emailAddress")
            if (email.isBlank() || email.length > 320 || email.any { it.isWhitespace() || it.isISOControl() } || !Charsets.UTF_8.newEncoder().canEncode(email))
                throw IOException("Drive returned an invalid account email.")
            return DriveUser(id,email)
        }
        private fun decimal(value: String,zero: Boolean): Long {
            val number = value.toLongOrNull()
            if (number == null || number < (if (zero) 0L else 1L) || number.toString() != value) throw IOException("Drive returned an invalid metadata revision or size.")
            return number
        }
        private fun scalar(value: JsonElement): String {
            if (!value.isJsonPrimitive || !value.asJsonPrimitive.isString) throw IOException("Drive workspace fields must be text.")
            return value.asString
        }
        private fun text(value: JsonElement,name: String): String = value.asJsonObject[name]?.let(::scalar) ?: throw IOException("Missing Drive workspace field: $name")
        private fun optionalText(value: JsonElement,name: String): String? = value.asJsonObject[name]?.let(::scalar)
    }
}
internal class DriveWorkspacePage(files: List<DriveWorkspaceMetadata>,val nextPage: String?,val incompleteSearch: Boolean) {
    val files: List<DriveWorkspaceMetadata> = Collections.unmodifiableList(files.toList())
    companion object {
        fun decode(bytes: ByteArray): DriveWorkspacePage {
            val root = json(bytes)
            if (!root.isJsonObject) throw IOException("Drive workspace page must be an object.")
            val rows = root.asJsonObject["files"]
            if (rows == null || !rows.isJsonArray || rows.asJsonArray.size() > 100) throw IOException("Drive workspace page exceeds its file bound.")
            val next = root.asJsonObject["nextPageToken"]?.let {
                if (!it.isJsonPrimitive || !it.asJsonPrimitive.isString) throw IOException("Invalid Drive workspace page token.")
                it.asString.also(::pageToken)
            }
            val incomplete = root.asJsonObject["incompleteSearch"]?.let {
                if (!it.isJsonPrimitive || !it.asJsonPrimitive.isBoolean) throw IOException("Invalid Drive incomplete-search flag.")
                it.asBoolean
            } ?: false
            val files = rows.asJsonArray.map(DriveWorkspaceMetadata::parse)
            if (files.map { it.id }.distinct().size != files.size) throw IOException("Drive returned duplicate workspace identities.")
            return DriveWorkspacePage(files,next,incomplete)
        }
        fun startCursor(bytes: ByteArray): String {
            val root = json(bytes)
            val cursor = if (root.isJsonObject) root.asJsonObject["startPageToken"] else null
            if (cursor == null || !cursor.isJsonPrimitive || !cursor.asJsonPrimitive.isString) throw IOException("Drive did not return the actual workspace cursor.")
            return cursor.asString.also(::pageToken)
        }
        fun pageToken(value: String) { if (value.length !in 1..4096 || value.any { it.isISOControl() } || !Charsets.UTF_8.newEncoder().canEncode(value)) throw IOException("Drive workspace page token exceeds its bound.") }
        internal fun json(bytes: ByteArray): JsonElement = try { JsonParser.parseString(Charsets.UTF_8.newDecoder().decode(java.nio.ByteBuffer.wrap(bytes)).toString()) }
            catch (error: com.google.gson.JsonParseException) { throw IOException("Drive returned malformed metadata JSON.",error) }
    }
}

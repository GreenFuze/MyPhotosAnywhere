package com.myphotosanywhere.mpa.drive

import com.google.gson.JsonElement
import java.io.IOException
import java.util.Collections

/** Captured Google account/corpus context. Tokens alone never select another account,
 * drive or metadata workspace. A cursor is transport evidence, never authority. */
internal data class DriveWorkspaceCursor(val accountId: String,val sharedDriveId: String?,val token: String) {
    init { DriveWorkspaceMetadata.opaque(accountId);sharedDriveId?.let(DriveWorkspaceMetadata::opaque);DriveWorkspacePage.pageToken(token) }
    fun sameScope(other: DriveWorkspaceCursor) = accountId == other.accountId && sharedDriveId == other.sharedDriveId
}
internal data class DriveWorkspaceChange(val kind: String,val id: String,val removed: Boolean,val file: DriveWorkspaceMetadata?) {
    init {
        DriveWorkspaceMetadata.opaque(id)
        if (kind !in setOf("file","drive") || file != null && (kind != "file" || file.id != id) || kind == "file" && !removed && file == null)
            throw IOException("Drive change lost its actual identity, kind or state.")
    }
}
internal class DriveWorkspaceChangesPage(changes: List<DriveWorkspaceChange>,val next: DriveWorkspaceCursor?,val atEnd: DriveWorkspaceCursor?) {
    val changes: List<DriveWorkspaceChange> = Collections.unmodifiableList(changes.toList())
    init {
        if (this.changes.size > 100 || (next == null) == (atEnd == null)) throw IOException("Drive changes lost their bounded explicit continuation.")
    }
    companion object {
        fun decode(bytes: ByteArray,from: DriveWorkspaceCursor): DriveWorkspaceChangesPage {
            val root = DriveWorkspacePage.json(bytes)
            if (!root.isJsonObject) throw IOException("Drive changes must be an object.")
            val rows = root.asJsonObject["changes"]
            if (rows == null || !rows.isJsonArray || rows.asJsonArray.size() > 100) throw IOException("Drive changes exceed their page bound.")
            fun cursor(name: String): DriveWorkspaceCursor? = root.asJsonObject[name]?.let { from.copy(token=text(it)) }
            val next = cursor("nextPageToken");val end = cursor("newStartPageToken")
            if ((next == null) == (end == null) || next?.token == from.token) throw IOException("Drive changes have ambiguous or nonadvancing continuation.")
            val changes = rows.asJsonArray.map { item ->
                if (!item.isJsonObject) throw IOException("Invalid Drive change row.")
                val kind = text(property(item,"changeType"));val id = text(property(item,if(kind == "file") "fileId" else "driveId"))
                val removed = property(item,"removed")
                if (!removed.isJsonPrimitive || !removed.asJsonPrimitive.isBoolean) throw IOException("Drive change removal must be explicit.")
                DriveWorkspaceChange(kind,id,removed.asBoolean,item.asJsonObject["file"]?.let(DriveWorkspaceMetadata::parse))
            }
            return DriveWorkspaceChangesPage(changes,next,end)
        }
        private fun property(root: JsonElement,name: String): JsonElement = root.asJsonObject[name] ?: throw IOException("Drive change field is missing: $name")
        private fun text(value: JsonElement): String {
            if (!value.isJsonPrimitive || !value.asJsonPrimitive.isString) throw IOException("Drive change fields must be text.")
            return value.asString
        }
    }
}

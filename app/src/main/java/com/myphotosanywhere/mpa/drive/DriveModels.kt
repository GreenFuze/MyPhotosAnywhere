package com.myphotosanywhere.mpa.drive

import java.security.MessageDigest

enum class DriveMode(val scope: String, val title: String, val explanation: String) {
    APP_FILES("https://www.googleapis.com/auth/drive.file", "New MPA folder",
        "Access files created by MPA. Existing photo collections are not included."),
    EXISTING_LIBRARY("https://www.googleapis.com/auth/drive", "Existing photo collection",
        "Google grants access to your whole Drive. MPA works inside selected folders and preserves media originals. Optional metadata conversion moves verified Google JSON to Drive trash."),
}

data class DriveBinding(val accountId: String, val folderId: String) {
    init { require(accountId.isNotBlank() && folderId.isNotBlank()) }
    val key: String get() = MessageDigest.getInstance("SHA-256")
        .digest("$accountId\u0000$folderId".toByteArray(Charsets.UTF_8))
        .joinToString("") { "%02x".format(it) }
    fun accepts(account: String, folder: String) = accountId == account && folderId == folder
}

data class DriveUser(val id: String, val email: String)
data class DriveFile(
    val id: String,
    val name: String,
    val mimeType: String,
    val bytes: Long = 0,
    val md5: String? = null,
    val version: String? = null,
    val parents: List<String> = emptyList(),
    val canAddChildren: Boolean = false,
    val modifiedTime: String? = null,
    val capturedAt: Long? = null,
    val latitude: Double? = null,
    val longitude: Double? = null,
    val thumbnailLink: String? = null,
    val trashed: Boolean = false,
    val assetId: String? = null,
    val sourceAssetId: String? = null,
    val sha256: String? = null,
)
data class DrivePage(val files: List<DriveFile>, val nextPage: String?, val incompleteSearch: Boolean = false)
data class UploadProgress(val offset: Long, val complete: Boolean)

class DriveHttpException(val status: Int, val reason: String? = null) : java.io.IOException(
    when (status) {
        401 -> "Google Drive could not renew access. Reconnect this account to continue."
        403 -> if (reason == "rateLimitExceeded" || reason == "userRateLimitExceeded")
            "Drive is temporarily limiting requests. Backup will retry." else "Drive denied access to this file or folder. Check its permissions and the granted Drive access."
        404 -> "The Drive file or folder is no longer available."
        429 -> "Drive is temporarily limiting requests. Backup will retry."
        else -> "Drive request failed (HTTP $status)."
    },
) {
    val retryable: Boolean get() = status == 429 || status in 500..599 ||
        (status == 403 && reason in listOf("rateLimitExceeded", "userRateLimitExceeded"))
}
class DriveAuthorizationNeeded : java.io.IOException("Google Drive needs your permission. Reconnect this account to continue.")

object UploadRanges {
    fun nextOffset(range: String?, total: Long): Long {
        require(total >= 0)
        if (range == null) return 0
        val match = Regex("bytes=0-(\\d+)").matchEntire(range) ?: error("Invalid Drive upload range.")
        val last = match.groupValues[1].toLong()
        require(last >= 0 && last < total) { "Drive returned an out-of-bounds upload range." }
        return Math.addExact(last, 1)
    }
}

data class DriveChange(val id: String, val removed: Boolean, val file: DriveFile?)
data class ChangesPage(val changes: List<DriveChange>, val nextPage: String?, val newStart: String?)

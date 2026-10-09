package com.myphotosanywhere.mpa.drive

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response

/** Small REST boundary. Tokens never appear in URLs, database rows, or error messages. */
class DriveGateway(
    private val token: String,
    private val client: OkHttpClient = OkHttpClient.Builder()
        .followRedirects(false).followSslRedirects(false)
        .connectTimeout(20, TimeUnit.SECONDS).readTimeout(60, TimeUnit.SECONDS).build(),
    private val api: HttpUrl = "https://www.googleapis.com/drive/v3/".toHttpUrl(),
    private val upload: HttpUrl = "https://www.googleapis.com/upload/drive/v3/files".toHttpUrl(),
) {
    suspend fun user(): DriveUser {
        val json = json(Request.Builder().url(url("about", "fields" to "user(permissionId,emailAddress)")))
            .getAsJsonObject("user") ?: error("Drive did not return an account.")
        return DriveUser(json.string("permissionId") ?: error("Drive account ID missing."),
            json.string("emailAddress") ?: error("Drive account email missing."))
    }

    suspend fun folders(parent: String, page: String? = null): DrivePage = list(
        "'${literal(parent)}' in parents and trashed = false and mimeType = '$FOLDER_MIME'", page,
    )

    suspend fun images(parent: String, page: String? = null): DrivePage = list(
        "'${literal(parent)}' in parents and trashed = false and mimeType contains 'image/'", page,
    )

    private suspend fun list(query: String, page: String?): DrivePage {
        val json = json(Request.Builder().url(url("files", "q" to query, "pageSize" to "100",
            "fields" to "nextPageToken,files($FILE_FIELDS)", "pageToken" to page,
            "supportsAllDrives" to "true", "includeItemsFromAllDrives" to "true")))
        val files = json.getAsJsonArray("files")?.map { parseFile(it.asJsonObject) } ?: emptyList()
        return DrivePage(files, json.string("nextPageToken"))
    }

    suspend fun file(id: String): DriveFile = parseFile(json(Request.Builder()
        .url(url("files/$id", "fields" to FILE_FIELDS, "supportsAllDrives" to "true"))))

    suspend fun generateId(): String = json(Request.Builder().url(url("files/generateIds", "count" to "1", "space" to "drive")))
        .getAsJsonArray("ids")?.firstOrNull()?.asString ?: error("Drive did not reserve a file ID.")

    suspend fun createFolder(name: String, parent: String = "root"): DriveFile {
        require(name.isNotBlank() && name.length <= 200)
        val metadata = JsonObject().apply {
            addProperty("name", name.trim()); addProperty("mimeType", FOLDER_MIME)
            add("parents", com.google.gson.JsonArray().apply { add(parent) })
        }
        return parseFile(json(Request.Builder().url(url("files", "fields" to FILE_FIELDS, "supportsAllDrives" to "true"))
            .post(metadata.toString().toRequestBody(JSON))))
    }

    suspend fun beginUpload(id: String, name: String, mime: String, size: Long, parent: String,
        assetId: String, sourceId: String?): String {
        val metadata = JsonObject().apply {
            addProperty("id", id); addProperty("name", name)
            add("parents", com.google.gson.JsonArray().apply { add(parent) })
            add("appProperties", JsonObject().apply {
                addProperty("mpaAssetId", assetId)
                if (sourceId != null) addProperty("mpaParentId", sourceId)
            })
        }
        return execute(Request.Builder().url(upload.newBuilder().addQueryParameter("uploadType", "resumable")
            .addQueryParameter("supportsAllDrives", "true").build())
            .header("X-Upload-Content-Type", mime).header("X-Upload-Content-Length", size.toString())
            .post(metadata.toString().toRequestBody(JSON))) { response ->
                checkSuccess(response)
                val location = response.header("Location") ?: error("Drive did not return an upload session.")
                sessionUrl(location)
                location
            }
    }

    suspend fun probe(session: String, total: Long): UploadProgress = execute(Request.Builder()
        .url(sessionUrl(session)).header("Content-Range", "bytes */$total").put(ByteArray(0).toRequestBody(null))) { r ->
            when (r.code) {
                200, 201 -> UploadProgress(total, true)
                308 -> UploadProgress(UploadRanges.nextOffset(r.header("Range"), total), false)
                else -> { checkSuccess(r); error("Unexpected upload response.") }
            }
        }

    suspend fun chunk(session: String, total: Long, offset: Long, bytes: ByteArray): UploadProgress {
        require(bytes.isNotEmpty() && offset >= 0 && offset + bytes.size <= total)
        return execute(Request.Builder().url(sessionUrl(session))
            .header("Content-Range", "bytes $offset-${offset + bytes.size - 1}/$total")
            .put(bytes.toRequestBody("application/octet-stream".toMediaType()))) { r ->
                when (r.code) {
                    200, 201 -> UploadProgress(total, true)
                    308 -> UploadProgress(UploadRanges.nextOffset(r.header("Range"), total), false)
                    else -> { checkSuccess(r); error("Unexpected upload response.") }
                }
            }
    }

    suspend fun download(id: String, target: File, maximumBytes: Long) = execute(Request.Builder()
        .url(url("files/$id", "alt" to "media", "supportsAllDrives" to "true"))) { r ->
            checkSuccess(r)
            val body = r.body ?: error("Drive returned no image.")
            if (body.contentLength() > maximumBytes) error("This image exceeds the local import limit.")
            body.byteStream().use { input -> target.outputStream().use { output ->
                val buffer = ByteArray(64 * 1024)
                var total = 0L
                while (true) {
                    val n = input.read(buffer); if (n < 0) break
                    total += n; require(total <= maximumBytes) { "This image exceeds the local import limit." }
                    output.write(buffer, 0, n)
                }
                output.fd.sync()
            } }
        }

    private fun sessionUrl(raw: String): HttpUrl {
        val result = raw.toHttpUrl()
        require(result.scheme == upload.scheme && result.host == upload.host && result.port == upload.port &&
            result.encodedPath == upload.encodedPath && result.username.isEmpty() && result.password.isEmpty()) {
            "Drive returned an invalid upload destination."
        }
        return result
    }

    private fun url(path: String, vararg params: Pair<String, String?>): HttpUrl {
        val builder = api.newBuilder()
        path.split('/').forEach { builder.addPathSegment(it) }
        params.forEach { (key, value) -> if (value != null) builder.addQueryParameter(key, value) }
        return builder.build()
    }

    private suspend fun json(builder: Request.Builder): JsonObject = execute(builder) { r ->
        checkSuccess(r)
        JsonParser.parseString(r.body?.string() ?: error("Drive returned no response.")).asJsonObject
    }

    private suspend fun <T> execute(builder: Request.Builder, read: (Response) -> T): T = withContext(Dispatchers.IO) {
        client.newCall(builder.header("Authorization", "Bearer $token").build()).execute().use(read)
    }

    private fun checkSuccess(r: Response) {
        if (r.isSuccessful) return
        val reason = runCatching {
            JsonParser.parseString(r.body?.string()).asJsonObject.getAsJsonObject("error")
                .getAsJsonArray("errors")?.firstOrNull()?.asJsonObject?.string("reason")
        }.getOrNull()
        throw DriveHttpException(r.code, reason)
    }

    companion object {
        const val FOLDER_MIME = "application/vnd.google-apps.folder"
        private const val FILE_FIELDS = "id,name,mimeType,size,md5Checksum,version,parents,capabilities(canAddChildren)"
        private val JSON = "application/json; charset=utf-8".toMediaType()
        fun literal(value: String) = value.replace("\\", "\\\\").replace("'", "\\'")
        private fun JsonObject.string(key: String): String? = get(key)?.takeUnless { it.isJsonNull }?.asString
        private fun parseFile(j: JsonObject) = DriveFile(
            id = j.string("id") ?: error("Missing Drive file ID."), name = j.string("name") ?: "Photo",
            mimeType = j.string("mimeType") ?: "application/octet-stream", bytes = j.string("size")?.toLongOrNull() ?: 0,
            md5 = j.string("md5Checksum"), version = j.string("version"),
            parents = j.getAsJsonArray("parents")?.map { it.asString } ?: emptyList(),
            canAddChildren = j.getAsJsonObject("capabilities")?.get("canAddChildren")?.asBoolean ?: false,
        )
    }
}

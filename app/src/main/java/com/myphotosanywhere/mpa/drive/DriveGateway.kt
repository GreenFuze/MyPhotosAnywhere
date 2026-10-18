package com.myphotosanywhere.mpa.drive

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response

/** Small REST boundary. Tokens never appear in URLs, database rows, or error messages. */
class DriveGateway(
    token: String,
    private val client: OkHttpClient = OkHttpClient.Builder()
        .followRedirects(false).followSslRedirects(false)
        .connectTimeout(20, TimeUnit.SECONDS).readTimeout(60, TimeUnit.SECONDS).build(),
    private val api: HttpUrl = "https://www.googleapis.com/drive/v3/".toHttpUrl(),
    private val upload: HttpUrl = "https://www.googleapis.com/upload/drive/v3/files".toHttpUrl(),
    refreshToken: (suspend (String) -> String)? = null,
) {
    private val access = DriveAccessSession(token, refreshToken)

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

    suspend fun children(parent: String, page: String? = null): DrivePage = list(
        "'${literal(parent)}' in parents and trashed = false and (mimeType = '$FOLDER_MIME' or mimeType contains 'image/' or mimeType contains 'video/' or name contains '.json')", page)

    suspend fun startToken(): String = json(Request.Builder().url(url("changes/startPageToken", "supportsAllDrives" to "true")))
        .string("startPageToken") ?: error("Drive change token missing.")

    suspend fun changes(page: String): ChangesPage {
        val j=json(Request.Builder().url(url("changes", "pageToken" to page, "pageSize" to "100",
            "supportsAllDrives" to "true", "includeItemsFromAllDrives" to "true",
            "fields" to "nextPageToken,newStartPageToken,changes(fileId,removed,file($FILE_FIELDS))")))
        return ChangesPage(j.getAsJsonArray("changes")?.map {
            val c=it.asJsonObject
            DriveChange(requireNotNull(c.string("fileId")),c.get("removed")?.asBoolean ?: false,
                c.getAsJsonObject("file")?.let { file -> parseFile(file) })
        } ?: emptyList(),j.string("nextPageToken"),j.string("newStartPageToken"))
    }

    suspend fun thumbnail(link: String, target: File) = execute(Request.Builder().url(link.toHttpUrl().also {
        require(it.scheme == "https" && (it.host == "googleusercontent.com" || it.host.endsWith(".googleusercontent.com")) &&
            it.username.isEmpty() && it.password.isEmpty()) { "Invalid Drive thumbnail host." }
    })) { r ->
        checkSuccess(r)
        val body=r.body
        if(body.contentLength()>2*1024*1024) throw java.io.IOException("Drive thumbnail too large.")
        body.byteStream().use { input -> target.outputStream().use { output ->
            val data=ByteArray(16384); var total=0
            while(true) { val n=input.read(data); if(n<0) break; total+=n
                if(total>2*1024*1024) throw java.io.IOException("Drive thumbnail too large."); output.write(data,0,n) }
        } }
    }

    private suspend fun list(query: String, page: String?): DrivePage {
        val json = json(Request.Builder().url(url("files", "q" to query, "pageSize" to "100",
            "fields" to "nextPageToken,incompleteSearch,files($FILE_FIELDS)", "pageToken" to page,
            "supportsAllDrives" to "true", "includeItemsFromAllDrives" to "true")))
        val files = json.getAsJsonArray("files")?.map { parseFile(it.asJsonObject) } ?: emptyList()
        return DrivePage(files, json.string("nextPageToken"), json.get("incompleteSearch")?.asBoolean ?: false)
    }

    suspend fun file(id: String): DriveFile = parseFile(json(Request.Builder()
        .url(url("files/$id", "fields" to FILE_FIELDS, "supportsAllDrives" to "true"))))

    /** Capture the conditional metadata revision without reading original media bytes. */
    suspend fun mediaRevision(id: String): Pair<DriveFile,String> = execute(Request.Builder()
        .url(url("files/$id","fields" to FILE_FIELDS,"supportsAllDrives" to "true"))) { response ->
        checkSuccess(response)
        val etag=response.header("ETag")?.takeIf(String::isNotBlank)
            ?: throw DriveRevisionUnavailable()
        parseFile(JsonParser.parseString(response.body.string()).asJsonObject) to etag
    }

    /** Media-only, conditional mutation. Metadata cleanup retains its separate strict boundary. */
    suspend fun setMediaTrashed(expected: DriveFile,etag: String,trashed: Boolean) {
        require(expected.id.isNotBlank() && etag.isNotBlank() && expected.version!=null)
        require(expected.mimeType.startsWith("image/") || expected.mimeType.startsWith("video/"))
        require(expected.trashed!=trashed)
        require(if(trashed) expected.canTrash else expected.canUntrash)
        execute(Request.Builder().url(url("files/${expected.id}","supportsAllDrives" to "true"))
            .header("If-Match",etag).patch("{\"trashed\":$trashed}".toRequestBody(JSON))) { checkSuccess(it) }
    }

    /** Explicit archive browser; ZIPs remain excluded from ordinary gallery discovery. */
    suspend fun archiveChildren(parent: String, page: String?=null): DrivePage = list(
        "'${literal(parent)}' in parents and trashed = false and (mimeType = '$FOLDER_MIME' or name contains '.zip')",page)

    /** Reject ignored, shifted, truncated or changed ranges instead of silently downloading the whole ZIP. */
    suspend fun readRange(id: String, offset: Long, count: Int, total: Long, etag: String?): Pair<ByteArray,String> {
        require(offset>=0 && count in 1..(256*1024) && total>0 && offset<=total-count)
        val end=offset+count-1
        return execute(Request.Builder().url(url("files/$id","alt" to "media","supportsAllDrives" to "true"))
            .header("Range","bytes=$offset-$end").header("Accept-Encoding","identity")
            .apply { if(etag!=null) header("If-Match",etag) }) { response ->
            checkSuccess(response)
            require(response.code==206 && response.header("Content-Range")=="bytes $offset-$end/$total") {
                "Drive did not return the requested ZIP range. Source preserved; use a local ZIP instead."
            }
            val revision=response.header("ETag") ?: error("Drive ZIP revision missing. Source preserved.")
            require(etag==null || revision==etag) { "Drive ZIP changed during import. Source preserved." }
            val body=response.body
            require(body.contentLength()==-1L || body.contentLength()==count.toLong()) { "Drive ZIP range length mismatch." }
            val bytes=body.byteStream().use { input ->
                val data=ByteArray(count); var read=0
                while(read<count) { val n=input.read(data,read,count-read); require(n>0) { "Truncated Drive ZIP range." }; read+=n }
                require(input.read()==-1) { "Excess Drive ZIP range data." }; data
            }
            bytes to revision
        }
    }

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
            val body = r.body
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

    /** Catalog discovery uses an exact filename and remains inside the explicitly selected root. */
    suspend fun metadataFiles(parent: String): List<DriveFile> {
        val result=mutableListOf<DriveFile>(); var page: String?=null
        do {
            val next=list("'${literal(parent)}' in parents and trashed = false and name = '.mpa-catalog.xmp'",page)
            result.addAll(next.files); require(result.size<=1) { "Multiple MPA catalogs found. Resolve duplicate files in Drive before syncing annotations." }
            page=next.nextPage
        } while(page!=null)
        return result
    }
    suspend fun readMetadata(id: String): Pair<String,String> = execute(Request.Builder()
        .url(url("files/$id", "alt" to "media", "supportsAllDrives" to "true"))) { r ->
        checkSuccess(r)
        val tag=r.header("ETag") ?: error("Drive did not return an annotation revision. Metadata is preserved.")
        val body=r.body
        require(body.contentLength()<=com.myphotosanywhere.mpa.metadata.XmpCodec.MAX_BYTES)
        val bytes=body.byteStream().use { input ->
            val out=java.io.ByteArrayOutputStream(); val buffer=ByteArray(16384)
            while(true) { val n=input.read(buffer); if(n<0) break
                require(out.size()+n<=com.myphotosanywhere.mpa.metadata.XmpCodec.MAX_BYTES); out.write(buffer,0,n) }
            out.toByteArray()
        }
        bytes.toString(Charsets.UTF_8) to tag
    }
    /** A conditional media PATCH can never replace photo bytes or silently clobber another device. */
    suspend fun writeMetadata(id: String,parent: String,xml: String,etag: String?,name: String=".mpa-catalog.xmp"): String {
        val bytes=xml.toByteArray(Charsets.UTF_8)
        require(bytes.size<=com.myphotosanywhere.mpa.metadata.XmpCodec.MAX_BYTES)
        val request=if(etag!=null) Request.Builder().url(upload.newBuilder().addPathSegment(id)
            .addQueryParameter("uploadType","media").addQueryParameter("supportsAllDrives","true").build())
            .header("If-Match",etag).patch(bytes.toRequestBody("application/rdf+xml".toMediaType()))
        else {
            val metadata=JsonObject().apply { addProperty("id",id); addProperty("name",name)
                addProperty("mimeType","application/rdf+xml"); add("parents",com.google.gson.JsonArray().apply { add(parent) }) }
            val body=okhttp3.MultipartBody.Builder().setType("multipart/related".toMediaType())
                .addPart(metadata.toString().toRequestBody(JSON))
                .addPart(bytes.toRequestBody("application/rdf+xml".toMediaType())).build()
            Request.Builder().url(upload.newBuilder().addQueryParameter("uploadType","multipart")
                .addQueryParameter("supportsAllDrives","true").build()).post(body)
        }
        return execute(request) { r -> checkSuccess(r)
            // A missing response ETag is safe: the next pass must read and compare the stored content.
            r.header("ETag") ?: ""
        }
    }

    private fun sessionUrl(raw: String): HttpUrl {
        val result = raw.toHttpUrl()
        require(result.scheme == upload.scheme && result.host == upload.host && result.port == upload.port &&
            result.encodedPath == upload.encodedPath && result.username.isEmpty() && result.password.isEmpty()) {
            "Drive returned an invalid upload destination."
        }
        return result
    }

    /** Only revision-checked JSON sidecars may be trashed; media originals cannot use this path. */
    suspend fun trashTakeoutJson(expected: DriveFile, etag: String) {
        require(expected.name.endsWith(".json",true) && !expected.mimeType.startsWith("image/") &&
            !expected.mimeType.startsWith("video/") && expected.mimeType!=FOLDER_MIME && etag.isNotBlank())
        val current=file(expected.id)
        require(!current.trashed && current.version==expected.version && current.md5==expected.md5 && current.parents==expected.parents && current.name==expected.name) {
            "Google JSON changed or moved. It was preserved."
        }
        execute(Request.Builder().url(url("files/${expected.id}","supportsAllDrives" to "true"))
            .header("If-Match",etag).patch("{\"trashed\":true}".toRequestBody(JSON))) { checkSuccess(it) }
    }

    private fun url(path: String, vararg params: Pair<String, String?>): HttpUrl {
        val builder = api.newBuilder()
        path.split('/').forEach { builder.addPathSegment(it) }
        params.forEach { (key, value) -> if (value != null) builder.addQueryParameter(key, value) }
        return builder.build()
    }

    private suspend fun json(builder: Request.Builder): JsonObject = execute(builder) { r ->
        checkSuccess(r)
        JsonParser.parseString(r.body.string()).asJsonObject
    }

    private suspend fun <T> execute(builder: Request.Builder, read: (Response) -> T): T = withContext(Dispatchers.IO) {
        try {
            val request = builder.build()
            val token = access.current()
            var response = client.newCall(request.newBuilder().header("Authorization", "Bearer $token").build()).execute()
            if (response.code == 401 && access.canRefresh) {
                // Close the rejected response before awaiting Google. No body/file/checkpoint has been read.
                response.close()
                currentCoroutineContext().ensureActive()
                check(request.body?.isOneShot() != true && request.body?.isDuplex() != true) {
                    "Drive request cannot be replayed safely."
                }
                val replacement = access.rejected(token)
                currentCoroutineContext().ensureActive()
                response = client.newCall(request.newBuilder().header("Authorization", "Bearer $replacement").build()).execute()
            }
            // Exactly one retry, only after an explicit 401. Never replay a timeout, 403 or server failure.
            response.use(read)
        }
        catch(e: IOException) {
            currentCoroutineContext().ensureActive()
            throw (DriveConnectionException.from(e) ?: e)
        }
    }

    private fun checkSuccess(r: Response) {
        if (r.isSuccessful) return
        val reason = runCatching {
            JsonParser.parseString(r.body.string()).asJsonObject.getAsJsonObject("error")
                .getAsJsonArray("errors")?.firstOrNull()?.asJsonObject?.string("reason")
        }.getOrNull()
        throw DriveHttpException(r.code, reason)
    }

    companion object {
        const val FOLDER_MIME = "application/vnd.google-apps.folder"
        private const val FILE_FIELDS = "id,name,mimeType,size,md5Checksum,sha256Checksum,version,parents,capabilities(canAddChildren,canTrash,canUntrash),modifiedTime,thumbnailLink,trashed,imageMediaMetadata(time,location(latitude,longitude)),appProperties"
        private val JSON = "application/json; charset=utf-8".toMediaType()
        fun literal(value: String) = value.replace("\\", "\\\\").replace("'", "\\'")
        private fun JsonObject.string(key: String): String? = get(key)?.takeUnless { it.isJsonNull }?.asString
        private fun parseFile(j: JsonObject) = DriveFile(
            id = j.string("id") ?: error("Missing Drive file ID."), name = j.string("name") ?: "Photo",
            mimeType = j.string("mimeType") ?: "application/octet-stream", bytes = j.string("size")?.toLongOrNull() ?: 0,
            sha256 = j.string("sha256Checksum"), md5 = j.string("md5Checksum"), version = j.string("version"),
            parents = j.getAsJsonArray("parents")?.map { it.asString } ?: emptyList(),
            canAddChildren = j.getAsJsonObject("capabilities")?.get("canAddChildren")?.asBoolean ?: false,
            modifiedTime = j.string("modifiedTime"), thumbnailLink = j.string("thumbnailLink"),
            trashed = j.get("trashed")?.asBoolean ?: false,
            canTrash = j.getAsJsonObject("capabilities")?.get("canTrash")?.asBoolean ?: false,
            canUntrash = j.getAsJsonObject("capabilities")?.get("canUntrash")?.asBoolean ?: false,
            capturedAt = j.getAsJsonObject("imageMediaMetadata")?.string("time")?.let { time ->
                runCatching { java.time.Instant.parse(time).toEpochMilli() }.getOrNull() ?:
                runCatching { java.text.SimpleDateFormat("yyyy:MM:dd HH:mm:ss", java.util.Locale.US).apply { isLenient=false }.parse(time)?.time }.getOrNull()
            },
            latitude = j.getAsJsonObject("imageMediaMetadata")?.getAsJsonObject("location")?.get("latitude")?.asDouble,
            longitude = j.getAsJsonObject("imageMediaMetadata")?.getAsJsonObject("location")?.get("longitude")?.asDouble,
            assetId = j.getAsJsonObject("appProperties")?.string("mpaAssetId"),
            sourceAssetId = j.getAsJsonObject("appProperties")?.string("mpaParentId"),
        )
    }
}

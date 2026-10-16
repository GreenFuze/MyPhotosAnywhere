package com.myphotosanywhere.mpa.importing

import android.accounts.Account
import android.content.Context
import com.google.android.gms.auth.api.identity.AuthorizationRequest
import com.google.android.gms.auth.api.identity.Identity
import com.google.android.gms.common.api.Scope
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.tasks.await
import kotlinx.coroutines.withContext
import okhttp3.*
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.File
import java.util.concurrent.TimeUnit

class PhotosPickerAuthorizationNeeded: java.io.IOException("Reconnect Google Photos to resume this import.")
class PhotosPickerRequestException(val status: Int,val selection: Boolean,message: String): IllegalArgumentException(message) {
    val blocksJob: Boolean get()=status in setOf(401,403,429) || status in 500..599 || selection && status in setOf(404,410)
}

class PhotosAuthorization(private val context: Context) {
    suspend fun request(email: String) = Identity.getAuthorizationClient(context).authorize(
        AuthorizationRequest.builder().setAccount(Account(email, "com.google"))
            .setRequestedScopes(listOf(Scope(SCOPE))).setOptOutIncludingGrantedScopes(true).build()).await()
    suspend fun token(email: String): String {
        val result = request(email)
        if(result.hasResolution() || SCOPE !in result.grantedScopes) throw PhotosPickerAuthorizationNeeded()
        return result.accessToken ?: throw PhotosPickerAuthorizationNeeded()
    }
    companion object { const val SCOPE = "https://www.googleapis.com/auth/photospicker.mediaitems.readonly" }
}

data class PickerSession(val id: String, val uri: String, val ready: Boolean, val interval: Long, val timeout: Long)
data class PickedMedia(val id: String, val name: String, val mime: String, val baseUrl: String,
    val captured: Long?, val videoReady: Boolean)

/** Official Picker REST API. Access tokens and expiring media URLs are never persisted. */
class PhotosPickerGateway(private val token: String,
    private val client: OkHttpClient = OkHttpClient.Builder().followRedirects(false).followSslRedirects(false)
        .connectTimeout(20, TimeUnit.SECONDS).readTimeout(60, TimeUnit.SECONDS).build(),
    private val api: HttpUrl = "https://photospicker.googleapis.com/v1/".toHttpUrl()) {
    suspend fun create(): PickerSession = parseSession(json(Request.Builder().url(url("sessions"))
        .post("{}".toRequestBody("application/json".toMediaType()))))
    suspend fun session(id: String): PickerSession = parseSession(json(Request.Builder().url(url("sessions/$id"))))
    suspend fun delete(id: String) = withContext(Dispatchers.IO) {
        client.newCall(Request.Builder().url(url("sessions/$id")).header("Authorization", "Bearer $token").delete().build())
            .execute().use { require(it.isSuccessful || it.code == 404) { "Could not close the Photos selection session." } }
    }
    suspend fun items(session: String, page: String? = null): Pair<List<PickedMedia>, String?> {
        val j = json(Request.Builder().url(url("mediaItems").newBuilder().addQueryParameter("sessionId",session)
            .addQueryParameter("pageSize","100").apply { if(page!=null) addQueryParameter("pageToken",page) }.build()))
        val rows = j.getAsJsonArray("mediaItems")?.map { element ->
            val item = element.asJsonObject; val file = item.getAsJsonObject("mediaFile")
            val mime = file.get("mimeType").asString
            PickedMedia(item.get("id").asString, file.get("filename").asString, mime, file.get("baseUrl").asString,
                item.get("createTime")?.asString?.let { java.time.Instant.parse(it).toEpochMilli() },
                !mime.startsWith("video/") || file.getAsJsonObject("mediaFileMetadata")?.getAsJsonObject("videoMetadata")?.get("processingStatus")?.asString == "READY")
        } ?: emptyList()
        return rows to j.get("nextPageToken")?.asString
    }
    suspend fun find(session: String, id: String): PickedMedia {
        var page: String? = null
        val seen = mutableSetOf<String>()
        repeat(21) {
            val result = items(session, page)
            result.first.find { it.id == id }?.let { return it }
            page = result.second ?: error("This photo is no longer selected. Select it again in Google Photos.")
            require(seen.add(requireNotNull(page))) { "Photos repeated a page cursor." }
        }
        error("Photos selection exceeds the supported 2,000 items.")
    }
    suspend fun download(media: PickedMedia, target: File, storage: com.myphotosanywhere.mpa.data.StorageBudget, keepGoing: suspend () -> Unit) = withContext(Dispatchers.IO) {
        require(media.videoReady) { "Google is still processing this video. Retry later." }
        val base = media.baseUrl.toHttpUrl()
        require(base.scheme == "https" && (base.host == "googleusercontent.com" || base.host.endsWith(".googleusercontent.com")) &&
            base.username.isEmpty() && base.password.isEmpty() && base.query == null && base.fragment == null) { "Invalid Photos media destination." }
        val suffix = if(media.mime.startsWith("video/")) "=dv" else "=d"
        client.newCall(Request.Builder().url(media.baseUrl + suffix).header("Authorization", "Bearer $token").build()).execute().use { response ->
            checkResponse(response)
            val body = response.body
            require(body.contentLength() <= TakeoutDocuments.MAX_MEDIA) { "Photos media exceeds 250 MiB. Source is preserved." }
            body.byteStream().use { TakeoutDocuments.copyBounded(it,target,storage,keepGoing) }
        }
    }
    private fun url(path: String) = api.newBuilder().apply { path.split('/').forEach { addPathSegment(it) } }.build()
    private suspend fun json(request: Request.Builder): JsonObject = withContext(Dispatchers.IO) {
        client.newCall(request.header("Authorization","Bearer $token").build()).execute().use { response ->
            checkResponse(response,selection=true)
            JsonParser.parseString(response.body.string()).asJsonObject
        }
    }
    private fun checkResponse(response: Response,selection: Boolean=false) {
        if(!response.isSuccessful) throw PhotosPickerRequestException(response.code,selection,when(response.code) {
            401 -> "Reconnect Google Photos."
            403 -> "Google Photos denied access. Check Picker API, consent scope, test users and the installed signing certificate."
            404,410 -> if(selection) "Google Photos selection expired. Select these items again; verified imports are preserved."
                else "This Google Photos media file is unavailable. Other selected files can continue; retry this file later."
            429 -> "Google Photos is limiting requests. Retry later."
            else -> "Google Photos request failed (HTTP ${response.code}). Retry from Imports."
        })
    }
    private fun parseSession(j: JsonObject): PickerSession {
        val config = j.getAsJsonObject("pollingConfig")
        fun duration(key: String) = config?.get(key)?.asString?.removeSuffix("s")?.toDouble()?.let { (it*1000).toLong().coerceAtLeast(0) } ?: 0
        val uri = j.get("pickerUri")?.asString ?: ""
        if(uri.isNotEmpty()) require(uri.toHttpUrl().let { it.scheme=="https" && it.host=="photos.google.com" && it.username.isEmpty() && it.password.isEmpty() })
        return PickerSession(j.get("id").asString,uri,j.get("mediaItemsSet")?.asBoolean ?: false,
            maxOf(1000,duration("pollInterval")),duration("timeoutIn"))
    }
}

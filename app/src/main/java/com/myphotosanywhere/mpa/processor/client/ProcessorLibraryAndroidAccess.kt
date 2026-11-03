package com.myphotosanywhere.mpa.processor.client

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import com.google.android.gms.auth.api.identity.AuthorizationResult
import com.google.android.gms.auth.api.identity.Identity
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.drive.DriveAuthorization
import com.myphotosanywhere.mpa.drive.DriveAuthorizationNeeded
import com.myphotosanywhere.mpa.drive.DriveGateway
import com.myphotosanywhere.mpa.drive.DriveMode
import com.myphotosanywhere.mpa.drive.GoogleAuthorizationReturn
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

internal sealed interface ProcessorLibraryAuthorization {
    class Granted internal constructor(internal val token: String) : ProcessorLibraryAuthorization {
        init { require(token.isNotBlank()) }
        override fun toString() = "Google authorization (redacted)"
    }
    class Consent(val pending: PendingIntent) : ProcessorLibraryAuthorization
}
internal interface ProcessorLibraryConnectionAccess {
    suspend fun current(): ProcessorLibraryRegistration?
    suspend fun authorize(email: String): ProcessorLibraryAuthorization
    fun returned(resultCode: Int, intent: Intent?): ProcessorLibraryAuthorization.Granted
    suspend fun review(code: String, email: String, grant: ProcessorLibraryAuthorization.Granted): ProcessorLibraryConnectionReview
    suspend fun approve(review: ProcessorLibraryConnectionReview, approvedDigest: String): ProcessorLibraryRegistration
}

/** Actual native Drive-only setup adapter. Each call owns and joins its temporary
 * SQLite handle. Google SDK tasks are joined before cancellation can retire the
 * caller; no token, pending intent or consent approval is persisted in SavedState. */
internal class ProcessorLibraryAndroidAccess(context: Context) : ProcessorLibraryConnectionAccess {
    private val application = context.applicationContext
    private val auth = DriveAuthorization(application)
    private val budget = StorageBudget(application)
    private val mode = DriveMode.EXISTING_LIBRARY
    override suspend fun current(): ProcessorLibraryRegistration? = withPins { it.registration() }
    override suspend fun authorize(email: String): ProcessorLibraryAuthorization {
        val result = withContext(NonCancellable) { auth.request(email,mode) }
        currentCoroutineContext().ensureActive()
        return if (result.hasResolution()) ProcessorLibraryAuthorization.Consent(checkNotNull(result.pendingIntent)) else grant(result)
    }
    override fun returned(resultCode: Int, intent: Intent?): ProcessorLibraryAuthorization.Granted =
        grant(GoogleAuthorizationReturn.resolve(resultCode,intent) { Identity.getAuthorizationClient(application).getAuthorizationResultFromIntent(it) })
    private fun grant(result: AuthorizationResult): ProcessorLibraryAuthorization.Granted {
        if (result.hasResolution() || !result.grantedScopes.contains(mode.scope)) throw DriveAuthorizationNeeded()
        return ProcessorLibraryAuthorization.Granted(result.accessToken?.takeIf { it.isNotBlank() } ?: throw DriveAuthorizationNeeded())
    }
    private suspend fun gateway(email: String, token: String? = null): DriveGateway {
        val initial = token ?: run {
            val result = authorize(email)
            if (result !is ProcessorLibraryAuthorization.Granted) throw DriveAuthorizationNeeded()
            result.token
        }
        return DriveGateway(initial, refreshToken = { rejected ->
            val result = withContext(NonCancellable) { auth.invalidate(rejected); auth.request(email,mode) }
            currentCoroutineContext().ensureActive()
            grant(result).token
        })
    }
    override suspend fun review(code: String, email: String, grant: ProcessorLibraryAuthorization.Granted): ProcessorLibraryConnectionReview {
        val reader = ProcessorLibraryDriveReader(gateway(email,grant.token))
        return withPins { ProcessorLibraryConnection(it,reader).review(code) }
    }
    override suspend fun approve(review: ProcessorLibraryConnectionReview, approvedDigest: String): ProcessorLibraryRegistration {
        val reader = ProcessorLibraryDriveReader(gateway(review.account.email))
        return withPins { pins ->
            ProcessorLibraryConnection(pins,reader).approve(review,approvedDigest)
            pins.registration() ?: throw ProcessorLibraryStateCorrupt("Approved library registration disappeared after its commit.")
        }
    }
    private suspend fun <T> withPins(action: suspend (ProcessorLibraryStateStore) -> T): T {
        val owner = ProcessorLibraryStateStore(application,budget)
        try { return action(owner) } finally { owner.close() }
    }
}

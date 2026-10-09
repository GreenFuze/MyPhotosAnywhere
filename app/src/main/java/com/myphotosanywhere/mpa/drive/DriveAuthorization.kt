package com.myphotosanywhere.mpa.drive

import android.accounts.Account
import android.content.Context
import com.google.android.gms.auth.api.identity.AuthorizationRequest
import com.google.android.gms.auth.api.identity.AuthorizationResult
import com.google.android.gms.auth.api.identity.ClearTokenRequest
import com.google.android.gms.auth.api.identity.Identity
import com.google.android.gms.common.api.Scope
import kotlinx.coroutines.tasks.await

class DriveAuthorization(private val context: Context) {
    suspend fun request(email: String, mode: DriveMode): AuthorizationResult = Identity.getAuthorizationClient(context)
        .authorize(AuthorizationRequest.builder().setAccount(Account(email, "com.google"))
            .setRequestedScopes(listOf(Scope(mode.scope))).build()).await()

    suspend fun token(email: String, mode: DriveMode): String {
        val result = request(email, mode)
        if (result.hasResolution() || !result.grantedScopes.contains(mode.scope)) throw DriveAuthorizationNeeded()
        return result.accessToken ?: throw DriveAuthorizationNeeded()
    }

    suspend fun invalidate(token: String) {
        Identity.getAuthorizationClient(context).clearToken(ClearTokenRequest.builder().setToken(token).build()).await()
    }
}

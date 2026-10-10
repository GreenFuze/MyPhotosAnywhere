package com.myphotosanywhere.mpa.drive

import android.app.Activity
import android.content.Intent
import androidx.activity.result.contract.ActivityResultContracts
import com.google.android.gms.common.api.ApiException
import com.google.android.gms.common.api.CommonStatusCodes

/** Google can return its error Status in a canceled activity result. Always let its SDK parse it. */
internal object GoogleAuthorizationReturn {
    @Suppress("DEPRECATION")
    fun <T> resolve(resultCode: Int,data: Intent?,parse: (Intent)->T): T {
        val sendFailure=data?.getSerializableExtra(ActivityResultContracts.StartIntentSenderForResult.EXTRA_SEND_INTENT_EXCEPTION)
        if(sendFailure!=null) throw GoogleAuthorizationLaunchFailed()
        if(data!=null) return parse(data)
        if(resultCode==Activity.RESULT_CANCELED) throw GoogleAuthorizationCanceled()
        throw GoogleAuthorizationMissingResult()
    }
    fun message(error: Exception): String = when(error) {
        is GoogleAuthorizationLaunchFailed -> "Google’s consent screen could not open. Retry, or update Google Play services. Local photos are preserved."
        is GoogleAuthorizationCanceled -> "Google sign-in closed without granting access. Retry; if Google shows blocked access, check the account’s test-user access and requested Drive scope. Local photos are preserved."
        is GoogleAuthorizationMissingResult -> "Google returned no authorization data. Retry. Open Google setup details to check this installed build."
        is ApiException -> when(error.statusCode) {
            CommonStatusCodes.CANCELED -> "Google sign-in was canceled (code 16). Retry and complete the consent screen. If Google blocks access, check test users and Drive scopes in Google Cloud."
            CommonStatusCodes.DEVELOPER_ERROR -> "Google OAuth configuration error (code 10). Open Google setup details and register this installed package and signing SHA-1 as an Android OAuth client in your Google Cloud project."
            CommonStatusCodes.NETWORK_ERROR -> "Google sign-in network error (code 7). Check your connection and retry."
            CommonStatusCodes.SIGN_IN_REQUIRED -> "Google requires sign-in again (code 4). Choose your account and complete consent."
            else -> "Google authorization failed (code ${error.statusCode}). Check Google’s displayed error, test-user access and requested Drive scopes. Open Google setup details for this installed build."
        }
        else -> error.message ?: "Drive operation failed. Local photos are preserved."
    }
}
internal class GoogleAuthorizationCanceled: IllegalStateException()
internal class GoogleAuthorizationLaunchFailed: IllegalStateException()
internal class GoogleAuthorizationMissingResult: IllegalStateException()

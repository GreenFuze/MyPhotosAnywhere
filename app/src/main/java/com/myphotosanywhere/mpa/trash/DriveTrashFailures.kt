package com.myphotosanywhere.mpa.trash

import com.google.android.gms.common.api.CommonStatusCodes
import com.myphotosanywhere.mpa.drive.*
import java.io.IOException

internal class DriveOperationFailure(message: String,val retryable: Boolean): IOException(message)

/** Expected external failures remain actionable; internal invariants and cancellation are not caught. */
internal object DriveTrashFailures {
    fun retryable(error: IOException)=when(error) {
        is DriveHttpException -> error.retryable
        is DriveOperationFailure -> error.retryable
        is TrashUnavailable,is DriveAuthorizationNeeded,is DriveRevisionUnavailable -> false
        else -> true // A lost transport reply must be reconciled from fresh Drive state.
    }
    fun google(code: Int): IOException {
        if(code==CommonStatusCodes.SIGN_IN_REQUIRED) return DriveAuthorizationNeeded()
        val transient=code in setOf(CommonStatusCodes.NETWORK_ERROR,CommonStatusCodes.TIMEOUT,
            CommonStatusCodes.INTERNAL_ERROR,CommonStatusCodes.API_NOT_CONNECTED,CommonStatusCodes.DEAD_CLIENT)
        val message=if(transient) "Google Drive authorization could not connect. Saved operations will retry after checking Drive state."
            else "Google Drive authorization needs attention (code $code). Review this source's Google access configuration."
        return DriveOperationFailure(message,transient)
    }
}

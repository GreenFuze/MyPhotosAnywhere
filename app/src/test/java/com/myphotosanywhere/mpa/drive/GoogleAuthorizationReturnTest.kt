package com.myphotosanywhere.mpa.drive

import android.app.Activity
import android.app.Application
import android.content.Intent
import androidx.activity.result.contract.ActivityResultContracts
import com.google.android.gms.common.api.ApiException
import com.google.android.gms.common.api.Status
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class GoogleAuthorizationReturnTest {
    @Test fun canceledResultWithDeveloperErrorReachesTheSdkAndKeepsTheActualCode() {
        val data=Intent().putExtra("diagnostic-fixture",true)
        var parsed=false
        try {
            GoogleAuthorizationReturn.resolve(Activity.RESULT_CANCELED,data) {
                parsed=true;throw ApiException(Status(10))
            }
            fail("Expected SDK error")
        } catch(e: ApiException) {
            assertTrue(parsed);assertEquals(10,e.statusCode)
            assertTrue(GoogleAuthorizationReturn.message(e).contains("code 10"))
            assertTrue(GoogleAuthorizationReturn.message(e).contains("signing SHA-1"))
        }
    }
    @Test fun successfulReturnUsesTheSdkRatherThanTrustingTheActivityCode() {
        val data=Intent()
        assertEquals("sdk-verified",GoogleAuthorizationReturn.resolve(Activity.RESULT_OK,data) { "sdk-verified" })
        try { GoogleAuthorizationReturn.resolve(Activity.RESULT_OK,data) { throw ApiException(Status(7)) };fail("SDK error swallowed") }
        catch(e: ApiException) { assertTrue(GoogleAuthorizationReturn.message(e).contains("network error")) }
    }
    @Test fun emptyCancellationAndMissingSuccessfulResultAreDifferentFailures() {
        try { GoogleAuthorizationReturn.resolve(Activity.RESULT_CANCELED,null) { true };fail("Expected cancellation") }
        catch(e: GoogleAuthorizationCanceled) { assertTrue(GoogleAuthorizationReturn.message(e).contains("closed without granting")) }
        try { GoogleAuthorizationReturn.resolve(Activity.RESULT_OK,null) { true };fail("Expected missing data") }
        catch(e: GoogleAuthorizationMissingResult) { assertTrue(GoogleAuthorizationReturn.message(e).contains("no authorization data")) }
    }
    @Test fun consentLaunchFailureDoesNotReachTheGoogleParser() {
        val data=Intent().putExtra(ActivityResultContracts.StartIntentSenderForResult.EXTRA_SEND_INTENT_EXCEPTION,
            android.content.IntentSender.SendIntentException("fixture"))
        var parsed=false
        try { GoogleAuthorizationReturn.resolve(Activity.RESULT_CANCELED,data) { parsed=true;true };fail("Expected launch failure") }
        catch(e: GoogleAuthorizationLaunchFailed) { assertFalse(parsed);assertTrue(GoogleAuthorizationReturn.message(e).contains("could not open")) }
    }
}

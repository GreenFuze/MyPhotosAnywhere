package com.myphotosanywhere.mpa.trash

import com.google.android.gms.common.api.CommonStatusCodes
import com.myphotosanywhere.mpa.drive.*
import org.junit.Assert.*
import org.junit.Test
import java.io.IOException

class DriveTrashFailuresTest {
    @Test fun sdkNetworkProblemsRetryButConsentAndConfigurationNeedAnAction() {
        for(code in listOf(CommonStatusCodes.NETWORK_ERROR,CommonStatusCodes.TIMEOUT,CommonStatusCodes.INTERNAL_ERROR))
            assertTrue(DriveTrashFailures.retryable(DriveTrashFailures.google(code)))
        assertTrue(DriveTrashFailures.google(CommonStatusCodes.SIGN_IN_REQUIRED) is DriveAuthorizationNeeded)
        assertFalse(DriveTrashFailures.retryable(DriveTrashFailures.google(CommonStatusCodes.DEVELOPER_ERROR)))
        assertTrue(DriveTrashFailures.google(CommonStatusCodes.DEVELOPER_ERROR).message.orEmpty().contains("code 10"))
    }
    @Test fun lostRepliesAreReconciledButConditionalPermissionAndContentFailuresStop() {
        assertTrue(DriveTrashFailures.retryable(IOException("Lost response")))
        assertTrue(DriveTrashFailures.retryable(DriveHttpException(503)))
        for(error in listOf(DriveHttpException(401),DriveHttpException(403),DriveHttpException(412),
            DriveRevisionUnavailable(),TrashUnavailable("Changed content")))
            assertFalse(DriveTrashFailures.retryable(error))
    }
}

package com.myphotosanywhere.mpa.drive

import kotlinx.coroutines.runBlocking
import okhttp3.Dns
import okhttp3.OkHttpClient
import org.junit.Assert.*
import org.junit.Test
import java.io.IOException
import java.net.SocketTimeoutException
import java.net.UnknownHostException

class DriveConnectionTest {
    @Test fun dnsFailureNamesDriveWithoutLeakingTokenOrUnderlyingText() = runBlocking<Unit> {
        val client=OkHttpClient.Builder().dns(object: Dns {
            override fun lookup(hostname: String): List<java.net.InetAddress> = throw UnknownHostException("secret-session")
        }).build()
        try { DriveGateway("secret-token",client=client).user(); fail() }
        catch(e: DriveConnectionException) {
            assertTrue(requireNotNull(e.message).contains("www.googleapis.com"))
            assertTrue(requireNotNull(e.message).contains("Private DNS"))
            assertFalse(requireNotNull(e.message).contains("secret"))
        }
    }
    @Test fun socketTimeoutIsRecoverableWithoutClaimingSourceFailure() = runBlocking<Unit> {
        var requests=0
        val client=OkHttpClient.Builder().addInterceptor { requests++;throw SocketTimeoutException("secret") }.build()
        try { DriveGateway("test",client=client,refreshToken={ error("Transport failures must never renew authorization") }).user(); fail() }
        catch(e: DriveConnectionException) {
            assertTrue(requireNotNull(e.message).contains("timed out"))
            assertTrue(requireNotNull(e.message).contains("files and upload checkpoints are preserved"))
        }
        assertEquals(1,requests)
    }
    @Test fun genericIoAndHttpErrorsAreNotClassifiedAsRetryableTransportFailures() = runBlocking<Unit> {
        val local=IOException("Local storage failed")
        val client=OkHttpClient.Builder().addInterceptor { throw local }.build()
        try { DriveGateway("test",client=client).user(); fail() }
        catch(e: IOException) { assertEquals(IOException::class.java,e.javaClass); assertEquals(local.message,e.message) }
        assertNull(DriveConnectionException.from(DriveHttpException(403)))
        assertNull(DriveConnectionException.from(DriveAuthorizationNeeded()))
    }
}

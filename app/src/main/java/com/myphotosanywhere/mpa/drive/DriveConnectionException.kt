package com.myphotosanywhere.mpa.drive

import java.io.IOException
import java.net.SocketException
import java.net.SocketTimeoutException
import java.net.UnknownHostException

/** A transport failure, distinct from permission, HTTP, local storage and invalid-data errors. */
class DriveConnectionException private constructor(message: String, cause: IOException): IOException(message,cause) {
    companion object {
        fun from(cause: IOException): DriveConnectionException? {
            val problem=when(cause) {
                is UnknownHostException -> "Cannot resolve Google Drive's server (www.googleapis.com)."
                is SocketTimeoutException -> "The connection to Google Drive timed out."
                is SocketException -> "The connection to Google Drive was interrupted."
                else -> return null
            }
            // Never expose an upload-session URL, token or server-supplied exception text.
            return DriveConnectionException("$problem Check your connection, VPN or Private DNS. Your files and upload checkpoints are preserved.",cause)
        }
    }
}

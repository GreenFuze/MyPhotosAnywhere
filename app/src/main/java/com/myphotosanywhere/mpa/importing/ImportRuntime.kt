package com.myphotosanywhere.mpa.importing

import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.drive.DriveGateway
import com.myphotosanywhere.mpa.drive.DriveSource

/** Internal dependency boundary for deterministic, byte-verified import integration fixtures. */
internal interface ImportRuntime {
    val storage: StorageBudget
    suspend fun gateway(source: DriveSource,client: okhttp3.OkHttpClient?): DriveGateway
    suspend fun committed(source: DriveSource)
}

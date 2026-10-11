package com.myphotosanywhere.mpa.importing

import android.net.Network
import okhttp3.Dns
import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit

/** Bind only transfer sockets/DNS to JobScheduler's network; never bind the whole app process. */
object ImportNetwork {
    fun client(network: Network): OkHttpClient = OkHttpClient.Builder()
        .socketFactory(network.socketFactory).dns(object: Dns { override fun lookup(hostname: String)=network.getAllByName(hostname).toList() })
        .followRedirects(false).followSslRedirects(false)
        .connectTimeout(20,TimeUnit.SECONDS).readTimeout(60,TimeUnit.SECONDS).build()
}

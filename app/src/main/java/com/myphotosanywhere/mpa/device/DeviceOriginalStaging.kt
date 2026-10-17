package com.myphotosanywhere.mpa.device

import java.io.File
import java.io.IOException

/** Only this owner's temporary files are removed; camera URIs and committed originals are untouched. */
internal class DeviceOriginalStaging(private val directory: File) {
    private var recovered=false
    fun create(): File {
        if(!directory.isDirectory && !directory.mkdirs()) throw IOException("Cannot prepare a device original. The camera file is unchanged.")
        if(!recovered) {
            val children=directory.listFiles() ?: throw IOException("Cannot inspect owned device staging files. Retry.")
            for(file in children) if(file.name.startsWith("mpa-device-") && file.name.endsWith(".part")) remove(file)
            recovered=true
        }
        return File.createTempFile("mpa-device-",".part",directory)
    }
    fun remove(file: File) {
        check(file.canonicalFile.parentFile==directory.canonicalFile) { "Device staging cleanup escaped its owned directory." }
        check(file.name.startsWith("mpa-device-") && file.name.endsWith(".part"))
        if(file.exists() && !file.delete()) throw IOException("Cannot remove an owned device staging file. The camera original is unchanged.")
    }
}

package com.myphotosanywhere.mpa.importing

import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.metadata.XmpCodec

/** The only cleanup gate. Tests exercise both successful conversion and every failed verification. */
class TakeoutCleanup(private val api: DriveGateway) {
    suspend fun verifyAndTrash(account: String,folder: String,json: DriveFile,jsonEtag: String,raw: String,
        media: DriveFile,sha256: String,catalogId: String,stillAllowed: suspend () -> Unit) {
        val file=api.file(catalogId)
        require(!file.trashed && file.name==".mpa-catalog.xmp" && folder in file.parents) { "MPA metadata backup moved or disappeared. Google JSON preserved." }
        val written=api.readMetadata(catalogId)
        val catalog=XmpCodec.decode(written.first)
        require(catalog.accountId==account && catalog.folderId==folder) { "MPA metadata backup belongs to another source. Google JSON preserved." }
        val asset=catalog.assets.singleOrNull { it.remoteId==media.id && it.sha256==sha256 && it.bytes==media.bytes }
            ?: error("MPA metadata backup verification failed. Google JSON preserved.")
        val backedUp=JsonParser.parseString(asset.importedMetadata).asJsonObject.get("googleTakeout")
        require(backedUp!=null && backedUp==JsonParser.parseString(raw)) { "MPA backup does not contain all source metadata. Google JSON preserved." }
        val checkMedia=api.file(media.id)
        require(!checkMedia.trashed && checkMedia.version==media.version && checkMedia.md5==media.md5 && checkMedia.bytes==media.bytes && checkMedia.parents==media.parents) {
            "Media changed before metadata cleanup. Google JSON preserved."
        }
        stillAllowed()
        api.trashTakeoutJson(json,jsonEtag)
    }
}

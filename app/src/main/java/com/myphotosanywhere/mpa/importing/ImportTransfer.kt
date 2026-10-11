package com.myphotosanywhere.mpa.importing

import com.myphotosanywhere.mpa.drive.*
import java.io.File
import java.io.RandomAccessFile

/** Each acknowledgement is durable. An upload completed just before death is found by its reserved ID. */
class ImportTransfer(private val api: DriveGateway) {
    suspend fun upload(initial: ImportItem, parent: String, save: suspend (ImportItem) -> Unit,
        keepGoing: suspend () -> Unit): Pair<ImportItem, DriveFile> {
        var item = initial
        val file = File(requireNotNull(item.staged))
        require(file.isFile && file.length() == item.size && DriveBackupWorker.digest(file,"SHA-256") == item.sha256 &&
            DriveBackupWorker.digest(file,"MD5") == item.md5) { "Staged bytes changed. Source and checkpoints are preserved." }
        keepGoing()
        if(item.remoteId==null) {
            item = item.copy(remoteId = api.generateId(), state = "UPLOADING")
            save(item)
        }
        val id = requireNotNull(item.remoteId)
        val existing = try { api.file(id) } catch(e: DriveHttpException) { if(e.status==404) null else throw e }
        if(existing==null) {
            var session = item.sessionUri
            var progress = if(session!=null) try { api.probe(session,item.size) } catch(e: DriveHttpException) {
                if(e.status in listOf(404,410)) { session=null; UploadProgress(0,false) } else throw e
            } else UploadProgress(0,false)
            if(session==null) {
                session=api.beginUpload(id,item.name,item.mime,item.size,parent,item.id,null)
                item=item.copy(sessionUri=session,offset=0); save(item)
            }
            RandomAccessFile(file,"r").use { input ->
                while(!progress.complete && progress.offset<item.size) {
                    keepGoing()
                    val start=progress.offset
                    val bytes=ByteArray(minOf(1024L*1024,item.size-start).toInt())
                    input.seek(start); input.readFully(bytes)
                    progress=api.chunk(requireNotNull(session),item.size,start,bytes)
                    require(progress.complete || progress.offset>start) { "Drive did not acknowledge import progress." }
                    item=item.copy(offset=progress.offset); save(item)
                }
            }
        }
        keepGoing()
        val remote=api.file(id)
        verify(remote,item,parent)
        item=item.copy(state="VERIFIED",offset=item.size,sessionUri=null,error=null); save(item)
        return item to remote
    }

    companion object {
        fun verify(remote: DriveFile, item: ImportItem, parent: String) {
            require(!remote.trashed && remote.version!=null && remote.md5==item.md5 && remote.bytes==item.size && parent in remote.parents) {
                "Remote checksum, size or destination changed. Import paused without overwriting or removing source bytes."
            }
        }
    }
}

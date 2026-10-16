package com.myphotosanywhere.mpa.importing

import com.myphotosanywhere.mpa.drive.*

/** Indexed candidates never authorize reuse: verify current bytes and selected-folder ancestry. */
class ImportDuplicateFinder(private val cloud: CloudDao, private val api: DriveGateway) {
    data class Match(val cached: CloudPhoto, val remote: DriveFile)
    suspend fun find(binding: String, root: String, item: ImportItem, keepGoing: suspend () -> Unit): Match? {
        val sha=requireNotNull(item.sha256)
        require(sha.matches(Regex("[0-9a-f]{64}")) && item.md5!=null && item.size>0)
        var after=""
        while(true) {
            val candidates=cloud.exactCandidates(binding,sha,item.size,after)
            if(candidates.isEmpty()) return null
            for(candidate in candidates) {
                keepGoing()
                try {
                    val remote=api.file(candidate.remoteId)
                    if(remote.id==candidate.remoteId && !remote.trashed && remote.version!=null && remote.sha256==sha && remote.md5==item.md5 &&
                        remote.bytes==item.size && inside(remote,root,keepGoing)) return Match(candidate,remote)
                } catch(e: DriveHttpException) { if(e.status!=404) throw e }
            }
            after=candidates.last().key
        }
    }
    private suspend fun inside(file: DriveFile, root: String, keepGoing: suspend () -> Unit): Boolean {
        val pending=ArrayDeque(file.parents); val seen=mutableSetOf<String>()
        while(pending.isNotEmpty()) {
            val id=pending.removeFirst()
            if(id==root) return true
            if(!seen.add(id)) continue
            if(seen.size>64) throw ImportDestinationUnavailable("Drive ancestry is too deep. Review the destination before resuming.")
            keepGoing()
            val folder=api.file(id)
            if(!folder.trashed && folder.mimeType==DriveGateway.FOLDER_MIME) pending.addAll(folder.parents)
        }
        return false
    }
}

package com.myphotosanywhere.mpa.trash

import androidx.room.withTransaction
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.selection.SelectionTarget
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.IOException

internal class TrashUnavailable(message: String): IOException(message)
internal data class TrashCandidate(val record: DriveTrashRecord,val account: String,val folder: String)

/** Mutations belong to a captured source and content identity, never to a mutable grid position. */
internal class DriveTrashRepository(private val app: MpaApplication,private val db: PhotoDatabase=app.database,
    private val gateway: suspend (DriveSource)->DriveGateway={ source ->
        DriveAuthorization(app).gateway(source.email,DriveMode.valueOf(source.mode))
    }) {
    private val lock=Mutex()

    suspend fun prepare(target: SelectionTarget): TrashCandidate=withContext(Dispatchers.IO) {
        val photo=target.photoId?.let { db.photos().find(it) }
        if(target.photoId!=null && (photo==null || photo.bytes!=target.bytes || photo.sha256!=target.sha256))
            throw TrashUnavailable("This photo changed. Select it again.")
        val key=target.cloudKey ?: photo?.let { p ->
            if(p.sourceBindingKey!=null && p.sourceRemoteId!=null) "${p.sourceBindingKey}:${p.sourceRemoteId}"
            else {
                val candidates=db.drive().backupsForPhoto(p.id).filter { it.state=="COMPLETE" && it.remoteId!=null && db.drive().source(it.bindingKey)!=null }
                if(candidates.size>1) throw TrashUnavailable("This photo has several Drive copies. Choose its source in Photos filters before moving a copy to Trash.")
                candidates.singleOrNull()?.let { "${it.bindingKey}:${it.remoteId}" }
            }
        } ?: throw TrashUnavailable("This original is only on this device. Store it in Drive before using Drive Trash; it has been preserved.")
        val cloud=db.cloud().photo(key) ?: throw TrashUnavailable("Refresh this Drive source before moving the photo to Trash.")
        if(!target.accepts(cloud)) throw TrashUnavailable("This Drive photo changed or is unavailable. Refresh and select it again.")
        val source=db.drive().source(cloud.bindingKey) ?: throw TrashUnavailable("Reconnect this photo's Drive source.")
        if(cloud.version==null || cloud.md5==null && cloud.sha256==null)
            throw TrashUnavailable("Refresh this source to verify the original before moving it to Trash.")
        if(db.trash().unfinishedTransfer(source.accountId,cloud.remoteId))
            throw TrashUnavailable("This original has an unfinished transfer. Finish or cancel its import before moving it to Trash.")
        if(db.trash().records(source.accountId,cloud.remoteId).any { it.confirmed })
            throw TrashUnavailable("This original is already in Drive Trash. Open Collections > Trash to restore it.")
        val record=DriveTrashRecord(cloud.key,source.key,source.accountId,cloud.remoteId,photo?.id ?: cloud.photoId,
            cloud.name,cloud.mimeType,cloud.bytes,cloud.md5,cloud.sha256,cloud.version,
            photo?.thumbnailPath?.takeIf(String::isNotBlank) ?: cloud.thumbnailPath,cloud.capturedAt,
            desired=true,updatedAt=System.currentTimeMillis())
        TrashCandidate(record,source.email,source.folderName)
    }

    suspend fun queue(candidates: List<TrashCandidate>,restore: Boolean=false)=withContext(Dispatchers.IO) { lock.withLock {
        require(candidates.isNotEmpty() && candidates.size<=500)
        require(candidates.map { it.record.key }.distinct().size==candidates.size)
        db.withTransaction {
            for(candidate in candidates) {
                val row=candidate.record
                val source=db.drive().source(row.bindingKey) ?: throw TrashUnavailable("Reconnect ${candidate.account} before continuing.")
                if(source.accountId!=row.accountId) throw TrashUnavailable("This source account changed. Refresh and confirm again.")
                val old=db.trash().find(row.key)
                if(db.trash().records(row.accountId,row.remoteId).any { it.desired!=null }) throw TrashUnavailable("An operation for ${row.name} is already waiting. Open Trash for its status.")
                if(restore && old?.confirmed!=true) throw TrashUnavailable("This item is no longer in MPA's Drive Trash index. Refresh Trash.")
                if(old!=null && !DriveTrashVersion.notOlder(old.observedVersion,row.observedVersion))
                    throw TrashUnavailable("This Drive item changed while confirmation was open. Refresh and confirm again.")
                if(restore && old!=null && (old.expectedVersion!=row.expectedVersion || old.bytes!=row.bytes ||
                    old.sha256!=row.sha256 || old.md5!=row.md5 || old.photoId!=row.photoId))
                    throw TrashUnavailable("This Drive item's identity changed. Refresh Trash and confirm the current item.")
                db.trash().save((if(restore) requireNotNull(old) else row).copy(desired=!restore,error=null,updatedAt=System.currentTimeMillis()))
            }
        }
    }

    }

    suspend fun cancel(key: String)=withContext(Dispatchers.IO) { lock.withLock {
        db.withTransaction { db.trash().find(key)?.takeIf { it.desired!=null }?.let { row ->
            db.trash().save(row.copy(desired=null,error="Pending operation stopped. Refresh to verify the current Drive state.",updatedAt=System.currentTimeMillis()))
        } }
    } }

    private fun sameContent(row: DriveTrashRecord,file: DriveFile): Boolean = file.id==row.remoteId &&
        file.mimeType==row.mimeType && file.bytes==row.bytes && when {
            row.sha256!=null && file.sha256!=null -> row.sha256==file.sha256
            row.md5!=null && file.md5!=null -> row.md5==file.md5
            else -> false
        }

    /** A fresh state read reconciles lost replies before any retry; changed content is never targeted. */
    suspend fun process(key: String)=withContext(Dispatchers.IO) { lock.withLock {
        val row=db.trash().find(key) ?: return@withLock
        val desired=row.desired ?: return@withLock
        val source=db.drive().source(row.bindingKey) ?: throw TrashUnavailable("Reconnect this photo's source to continue.")
        if(source.accountId!=row.accountId) throw TrashUnavailable("This source account changed. Confirm again.")
        if(desired && db.trash().unfinishedTransfer(row.accountId,row.remoteId))
            throw TrashUnavailable("This original has an unfinished transfer. Its checkpoint is preserved; finish or cancel the import first.")
        val api=gateway(source)
        val user=api.user()
        if(user.id!=source.accountId || !user.email.equals(source.email,true)) throw DriveAuthorizationNeeded()
        val (before,etag)=api.mediaRevision(row.remoteId)
        if(!sameContent(row,before)) throw TrashUnavailable("The Drive original changed. Nothing was moved; refresh and confirm the current photo.")
        if(!inside(api,before,source.folderId))
            throw TrashUnavailable("This file or its parent folder moved or is trashed. Restore its folder in Google Drive, then refresh MPA.")
        if(before.trashed!=desired) {
            if(before.version!=row.expectedVersion) throw TrashUnavailable("The Drive revision changed. Refresh and confirm again.")
            if(desired && !before.canTrash || !desired && !before.canUntrash)
                throw TrashUnavailable(if(desired) "This account cannot move this file to Drive Trash." else "This account cannot restore this Drive file.")
            currentCoroutineContext().ensureActive()
            // Recheck the source immediately before crossing the mutation boundary.
            if(db.drive().source(row.bindingKey)?.accountId!=row.accountId) throw TrashUnavailable("This source disconnected. Nothing was moved.")
            api.setMediaTrashed(before,etag,desired)
        }
        val after=api.file(row.remoteId)
        if(!sameContent(row,after) || after.trashed!=desired)
            throw TrashUnavailable("Drive did not confirm the requested state. Refresh Trash before trying again.")
        if(!inside(api,after,requireNotNull(source.folderId))) throw TrashUnavailable("The file moved outside this source. Check it in Google Drive.")
        app.cache.lock.withLock { db.withTransaction {
            val current=db.trash().find(key) ?: error("Confirmed trash intent disappeared")
            if(!DriveTrashVersion.notOlder(current.observedVersion,after.version))
                throw TrashUnavailable("Drive changed again while verifying this operation. Refresh Trash before confirming another action.")
            check(current.desired==desired || current.desired==null && current.confirmed==desired) { "Trash intent changed during mutation" }
            val now=System.currentTimeMillis()
            db.trash().save(current.copy(confirmed=desired,desired=null,error=null,expectedVersion=after.version,updatedAt=now,observedVersion=after.version))
            db.trash().verifiedState(row.accountId,row.remoteId,desired,now,after.version)
            if(desired) db.cloud().accountCopies(row.accountId,row.remoteId).forEach { cloud ->
                db.cloud().savePhoto(cloud.copy(removed=true,version=after.version,modifiedTime=after.modifiedTime))
                cloud.photoId?.let { id -> db.photos().find(id)?.let { photo ->
                    if(photo.sourceBindingKey==cloud.bindingKey && photo.sourceRemoteId==row.remoteId && photo.cachedVersion==cloud.version)
                        db.photos().update(photo.copy(cachedVersion=after.version))
                } }
            } else {
                // Other source aliases await their own ancestry verification during source sync.
                val cloud=db.cloud().photo(row.key) ?: error("Confirmed original lost its index")
                CloudSync(app,db).save(row.bindingKey,after.parents.first(),after,cloud.seenGeneration)
            }
        } }
    } }

    private suspend fun inside(api: DriveGateway,file: DriveFile,root: String): Boolean {
        val queue=ArrayDeque(file.parents);val seen=mutableSetOf<String>()
        while(queue.isNotEmpty()) {
            currentCoroutineContext().ensureActive()
            val id=queue.removeFirst()
            if(!seen.add(id)) continue
            if(seen.size>64) throw TrashUnavailable("Source ancestry is too complex to verify safely.")
            val parent=api.file(id)
            if(parent.mimeType!=DriveGateway.FOLDER_MIME || parent.trashed) return false
            if(id==root) return true
            queue.addAll(parent.parents)
        }
        return false
    }

    suspend fun failure(key: String,error: IOException,retry: Boolean)=withContext(Dispatchers.IO) {
        db.withTransaction { db.trash().find(key)?.let { row ->
            db.trash().save(row.copy(desired=if(retry) row.desired else null,error=error.message ?: "Drive operation failed. Refresh Trash.",updatedAt=System.currentTimeMillis()))
        } }
    }

    /** Explicit refresh inspects only already indexed source media, not unrelated account trash. */
    suspend fun refresh(limit: Int=60)=withContext(Dispatchers.IO) {
        require(limit in 1..300)
        val rows=db.trash().page(null,"",limit)
        for(snapshot in rows) {
            currentCoroutineContext().ensureActive()
            lock.withLock {
            val row=db.trash().find(snapshot.key) ?: return@withLock
            if(row.desired!=null) return@withLock
            val source=db.drive().source(row.bindingKey) ?: return@withLock
            try {
                val api=gateway(source);val user=api.user()
                if(user.id!=row.accountId || !user.email.equals(source.email,true)) throw DriveAuthorizationNeeded()
                val file=api.file(row.remoteId)
                if(!sameContent(row,file)) throw TrashUnavailable("This original changed. Review it in Google Drive.")
                val within=!file.trashed && inside(api,file,requireNotNull(source.folderId))
                app.cache.lock.withLock { db.withTransaction {
                    db.trash().verifiedState(row.accountId,row.remoteId,file.trashed,System.currentTimeMillis(),file.version)
                    if(within) {
                        val cloud=db.cloud().photo(row.key) ?: throw TrashUnavailable("Refresh this photo's source before restoring its index.")
                        CloudSync(app,db).save(row.bindingKey,file.parents.first(),file,cloud.seenGeneration)
                    }
                    db.trash().find(row.key)?.let { current -> db.trash().save(current.copy(expectedVersion=if(current.desired==null) file.version else current.expectedVersion,error=null)) }
                } }
            } catch(cancelled: CancellationException) { throw cancelled }
            catch(error: IOException) { failure(row.key,error,false) }
            }
        }
    }
}

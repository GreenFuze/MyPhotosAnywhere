package com.myphotosanywhere.mpa.trash

import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.drive.DriveFile

/** Called inside the existing source/index transaction. No network or file mutation. */
internal class DriveTrashIndex(private val db: PhotoDatabase) {
    /** Drive versions increase for every server change; stale pages cannot undo a confirmed action. */
    suspend fun accepts(binding: String,file: DriveFile): Boolean {
        val account=db.drive().source(binding)?.accountId
            ?: db.trash().find("$binding:${file.id}")?.accountId ?: return true
        val records=db.trash().records(account,file.id)
        return records.all { DriveTrashVersion.notOlder(it.observedVersion,file.version) }
    }
    suspend fun verified(binding: String,file: DriveFile) {
        val source=db.drive().source(binding) ?: return
        val cloud=db.cloud().photo("$binding:${file.id}") ?: return
        val old=db.trash().find(cloud.key)
        val now=System.currentTimeMillis()
        val complete=file.version!=null && (file.md5!=null || file.sha256!=null)
        if(!complete) return // Missing content proof never hides a known local identity.
        fun matches(bytes: Long,md5: String?,sha: String?)=bytes==file.bytes && when {
            sha!=null && file.sha256!=null -> sha==file.sha256
            md5!=null && file.md5!=null -> md5==file.md5
            else -> false
        }
        // One physical file can have several indexed source aliases. Never carry an old local
        // association or captured intent onto replacement bytes through a different alias.
        db.trash().records(source.accountId,file.id).filterNot { matches(it.bytes,it.md5,it.sha256) }.forEach { previous ->
            db.trash().save(previous.copy(photoId=null,name=file.name.takeIf(String::isNotBlank) ?: previous.name,
                mimeType=file.mimeType,bytes=file.bytes,md5=file.md5,sha256=file.sha256,
                expectedVersion=file.version,observedVersion=file.version,thumbnailPath="",capturedAt=file.capturedAt,
                desired=null,confirmed=file.trashed,error="The Drive file's content changed. Its previous local original is preserved.",updatedAt=now))
        }
        val linked=matches(cloud.bytes,cloud.md5,cloud.sha256)
        val replaced=old!=null && !matches(old.bytes,old.md5,old.sha256)
        if(file.trashed && old==null || replaced) {
            db.trash().save(DriveTrashRecord(cloud.key,binding,source.accountId,cloud.remoteId,
                if(linked) cloud.photoId else null,file.name.takeIf(String::isNotBlank) ?: cloud.name,
                file.mimeType,file.bytes,file.md5,file.sha256,file.version,
                if(linked) cloud.thumbnailPath else "",if(linked) cloud.capturedAt else file.capturedAt,
                confirmed=file.trashed,error=if(replaced) "The Drive file's content changed. Its previous local original is preserved." else null,updatedAt=now))
        }
        db.trash().verifiedState(source.accountId,file.id,file.trashed,now,file.version)
    }
}

package com.myphotosanywhere.mpa.data

import android.content.Context
import java.io.File
import java.io.IOException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

internal data class OfflineStorageSummary(val cutoff: Long,val removable: Int,val removableBytes: Long,
    val downloadedBytes: Long,val protected: Int)
internal data class OfflineCleanupResult(val removed: Int=0,val freedBytes: Long=0,val retained: Int=0,
    val failures: List<String> = emptyList()) {
    fun message()="Freed %.1f MiB from %d downloads. %d protected or unavailable items kept.".format(freedBytes/(1024.0*1024),removed,retained)+
        failures.firstOrNull()?.let { " $it" }.orEmpty()
}

/** Bounded discovery; each deletion delegates to the existing exact-copy/lease transaction. */
internal class OfflineStorageRepository(context: Context,private val database: PhotoDatabase,private val cache: MediaCache) {
    private val originals=File(context.filesDir,"originals").canonicalFile
    private fun owned(photo: Photo,file: File)=photo.sourceBindingKey!=null && photo.sourceRemoteId!=null &&
        file.canonicalPath.startsWith(originals.path+File.separator)
    suspend fun summary(): OfflineStorageSummary=withContext(Dispatchers.IO) {
        val cutoff=System.currentTimeMillis()
        var after="";var count=0;var bytes=0L;var total=0L;var protected=0
        while(true) {
            currentCoroutineContext().ensureActive()
            val batch=database.photos().offlineOriginals(after,120)
            if(batch.isEmpty()) break
            for(photo in batch) {
                val file=File(photo.originalPath)
                if(!file.isFile) continue
                val size=file.length()
                if(owned(photo,file)) total=Math.addExact(total,size)
                if(owned(photo,file) && cache.disposable(photo)) { count++;bytes=Math.addExact(bytes,size) }
                else protected++
            }
            after=batch.last().id
        }
        OfflineStorageSummary(cutoff,count,bytes,total,protected)
    }
    suspend fun clear(cutoff: Long,budget: Long=0,onProgress: (OfflineCleanupResult)->Unit={}): OfflineCleanupResult=withContext(Dispatchers.IO) {
        require(cutoff>=0 && budget>=0)
        var remaining=summary().downloadedBytes
        var result=OfflineCleanupResult();var access=Long.MIN_VALUE;var after=""
        while(remaining>budget) {
            currentCoroutineContext().ensureActive()
            val batch=database.photos().offlineCandidates(cutoff,access,after,120)
            if(batch.isEmpty()) break
            for(photo in batch) {
                currentCoroutineContext().ensureActive()
                if(remaining<=budget) break
                val file=File(photo.originalPath)
                val identity=DeviceCopyIdentity.from(photo)
                if(identity==null || !owned(photo,file) || !cache.disposable(photo)) result=result.copy(retained=result.retained+1)
                else try {
                    val bytes=file.length()
                    cache.removeDownloadedCopy(identity,beforeAccess=cutoff)
                    remaining=(remaining-bytes).coerceAtLeast(0)
                    result=result.copy(removed=result.removed+1,freedBytes=Math.addExact(result.freedBytes,bytes))
                } catch(error: IOException) {
                    result=result.copy(retained=result.retained+1,failures=(result.failures+"${photo.displayName}: ${error.message ?: "Drive could not be verified; download kept."}").take(12))
                } catch(_: SecurityException) {
                    result=result.copy(retained=result.retained+1,failures=(result.failures+"Android denied access to ${photo.displayName}; download kept.").take(12))
                }
                onProgress(result)
            }
            access=batch.last().lastAccess;after=batch.last().id
        }
        result
    }
}

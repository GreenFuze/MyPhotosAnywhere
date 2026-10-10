package com.myphotosanywhere.mpa.drive

import androidx.room.*
import kotlinx.coroutines.flow.Flow

@Entity(tableName = "cloud_photos", indices = [Index("bindingKey"), Index("photoId")])
data class CloudPhoto(@PrimaryKey val key: String, val bindingKey: String, val remoteId: String,
    val parentId: String, val name: String, val mimeType: String, val bytes: Long,
    val md5: String?, val version: String?, val modifiedTime: String?, val capturedAt: Long?,
    val latitude: Double?, val longitude: Double?, val thumbnailPath: String = "",
    val thumbnailVersion: String? = null, val photoId: String? = null, val removed: Boolean = false,
    val seenGeneration: String = "", val assetId: String? = null, val sourceAssetId: String? = null)
@Entity(tableName = "cloud_folders", primaryKeys = ["bindingKey", "remoteId"])
data class CloudFolder(val bindingKey: String, val remoteId: String, val parentId: String?, val name: String,
    val seenGeneration: String)
@Entity(tableName = "sync_states")
data class SyncState(@PrimaryKey val bindingKey: String, val changeToken: String? = null,
    val lastScan: Long = 0, val error: String? = null, val running: Boolean = false)
@Entity(tableName = "downloads", indices = [Index("bindingKey")])
data class DownloadTask(@PrimaryKey val key: String, val bindingKey: String, val remoteId: String,
    val pinned: Boolean = false, val state: String = "PENDING", val error: String? = null)

@Dao
interface CloudDao {
    @Query("SELECT * FROM cloud_photos ORDER BY capturedAt DESC, modifiedTime DESC, name") fun observePhotos(): Flow<List<CloudPhoto>>
    @Query("SELECT * FROM sync_states") fun observeSync(): Flow<List<SyncState>>
    @Query("SELECT * FROM downloads") fun observeDownloads(): Flow<List<DownloadTask>>
    @Query("SELECT * FROM cloud_photos WHERE bindingKey = :binding") suspend fun photos(binding: String): List<CloudPhoto>
    @Query("SELECT * FROM cloud_photos WHERE key = :key") suspend fun photo(key: String): CloudPhoto?
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun savePhoto(photo: CloudPhoto)
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun saveFolder(folder: CloudFolder)
    @Query("SELECT * FROM cloud_folders WHERE bindingKey = :binding") suspend fun folders(binding: String): List<CloudFolder>
    @Query("DELETE FROM cloud_folders WHERE bindingKey = :binding AND seenGeneration != :generation") suspend fun pruneFolders(binding: String, generation: String)
    @Query("UPDATE cloud_photos SET removed = 1 WHERE bindingKey = :binding AND seenGeneration != :generation") suspend fun markAbsent(binding: String, generation: String)
    @Query("UPDATE cloud_photos SET removed = 1 WHERE bindingKey = :binding AND remoteId = :remote") suspend fun markRemoved(binding: String, remote: String)
    @Query("SELECT * FROM sync_states WHERE bindingKey = :binding") suspend fun sync(binding: String): SyncState?
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun saveSync(sync: SyncState)
    @Query("SELECT * FROM downloads WHERE key = :key") suspend fun download(key: String): DownloadTask?
    @Query("SELECT * FROM downloads WHERE bindingKey = :binding AND state NOT IN ('COMPLETE','FAILED','AUTH_REQUIRED')") suspend fun pendingDownloads(binding: String): List<DownloadTask>
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun saveDownload(task: DownloadTask)
}

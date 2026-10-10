package com.myphotosanywhere.mpa.drive

import androidx.room.*
import kotlinx.coroutines.flow.Flow

@Entity(tableName = "cloud_photos", indices = [Index("bindingKey"), Index("photoId")])
data class CloudPhoto(@PrimaryKey val key: String, val bindingKey: String, val remoteId: String,
    val parentId: String, val name: String, val mimeType: String, val bytes: Long,
    val md5: String?, val version: String?, val modifiedTime: String?, val capturedAt: Long?,
    val latitude: Double?, val longitude: Double?, val thumbnailPath: String = "",
    val thumbnailVersion: String? = null, val photoId: String? = null, val removed: Boolean = false,
    val seenGeneration: String = "", val assetId: String? = null, val sourceAssetId: String? = null, val thumbnailUpdatedAt: Long = 0)
@Entity(tableName = "cloud_folders", primaryKeys = ["bindingKey", "remoteId"])
data class CloudFolder(val bindingKey: String, val remoteId: String, val parentId: String?, val name: String,
    val seenGeneration: String)
@Entity(tableName = "sync_states")
data class SyncState(@PrimaryKey val bindingKey: String, val changeToken: String? = null,
    val lastScan: Long = 0, val error: String? = null, val running: Boolean = false,
    val scanGeneration: String? = null, val scanToken: String? = null,
    @ColumnInfo(defaultValue = "0") val scanPages: Int = 0,
    @ColumnInfo(defaultValue = "0") val lastSuccess: Long = 0,
    @ColumnInfo(defaultValue = "'WAITING'") val status: String = "WAITING")

/** Completed entries remain until the generation finishes, preventing duplicate traversal/cycles. */
@Entity(tableName = "scan_folders", primaryKeys = ["bindingKey", "remoteId"])
data class ScanFolder(val bindingKey: String, val remoteId: String,
    val pageToken: String? = null, val complete: Boolean = false)
@Entity(tableName = "downloads", indices = [Index("bindingKey")])
data class DownloadTask(@PrimaryKey val key: String, val bindingKey: String, val remoteId: String,
    val pinned: Boolean = false, val state: String = "PENDING", val error: String? = null)

@Dao
interface CloudDao {
    @Query("""SELECT cloud_photos.* FROM cloud_photos JOIN drive_sources ON cloud_photos.bindingKey = drive_sources.key
        WHERE drive_sources.accountId = :account AND cloud_photos.remoteId = :remote
        AND cloud_photos.md5 = :md5 AND cloud_photos.bytes = :bytes
        AND cloud_photos.photoId IS NOT NULL AND cloud_photos.removed = 0 LIMIT 1""")
    suspend fun sameAccountCopy(account: String, remote: String, md5: String, bytes: Long): CloudPhoto?
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
    @Query("SELECT * FROM scan_folders WHERE bindingKey = :binding AND complete = 0 ORDER BY remoteId LIMIT 1")
    suspend fun nextScanFolder(binding: String): ScanFolder?
    @Query("SELECT COUNT(*) FROM scan_folders WHERE bindingKey = :binding") suspend fun scanFolderCount(binding: String): Int
    @Insert(onConflict = OnConflictStrategy.IGNORE) suspend fun enqueueScanFolder(folder: ScanFolder)
    @Update suspend fun updateScanFolder(folder: ScanFolder)
    @Query("DELETE FROM scan_folders WHERE bindingKey = :binding") suspend fun clearScanFolders(binding: String)
    @Query("SELECT * FROM downloads WHERE key = :key") suspend fun download(key: String): DownloadTask?
    @Query("SELECT * FROM downloads WHERE bindingKey = :binding AND state NOT IN ('COMPLETE','FAILED','AUTH_REQUIRED')") suspend fun pendingDownloads(binding: String): List<DownloadTask>
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun saveDownload(task: DownloadTask)
}

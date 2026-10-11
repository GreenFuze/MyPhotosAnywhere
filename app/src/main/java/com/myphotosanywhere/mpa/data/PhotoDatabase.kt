package com.myphotosanywhere.mpa.data

import androidx.room.ColumnInfo
import androidx.room.Update
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Index
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.RoomDatabase
import kotlinx.coroutines.flow.Flow
import com.myphotosanywhere.mpa.drive.DriveConnection
import com.myphotosanywhere.mpa.drive.DriveBackup
import com.myphotosanywhere.mpa.drive.DriveDao
import com.myphotosanywhere.mpa.drive.CloudPhoto
import com.myphotosanywhere.mpa.drive.CloudFolder
import com.myphotosanywhere.mpa.drive.SyncState
import com.myphotosanywhere.mpa.drive.DownloadTask
import com.myphotosanywhere.mpa.drive.CloudDao
import com.myphotosanywhere.mpa.drive.DriveSource

@Entity(tableName = "photos", indices = [Index("sha256"), Index("parentId")])
data class Photo(
    @PrimaryKey val id: String,
    val displayName: String,
    val originalPath: String,
    val thumbnailPath: String,
    val mimeType: String,
    val bytes: Long,
    val importedAt: Long,
    val sha256: String,
    val latitude: Double? = null,
    val longitude: Double? = null,
    val parentId: String? = null,
    val editProvider: String? = null,
    val editInstructions: String? = null,
    val capturedAt: Long? = null,
    @ColumnInfo(defaultValue = "0") val favorite: Boolean = false,
    @ColumnInfo(defaultValue = "0") val archived: Boolean = false,
    @ColumnInfo(defaultValue = "''") val placeLabel: String = "",
    val sourceBindingKey: String? = null,
    val sourceRemoteId: String? = null,
    val cachedVersion: String? = null,
    @ColumnInfo(defaultValue = "0") val pinned: Boolean = false,
    @ColumnInfo(defaultValue = "0") val lastAccess: Long = 0,
    @ColumnInfo(defaultValue = "''") val labels: String = "",
    val metadataAssetId: String? = null,
    @ColumnInfo(defaultValue = "''") val importedMetadata: String = "",
)

@Entity(tableName = "handoffs")
data class Handoff(
    @PrimaryKey val id: String,
    val photoId: String,
    val provider: String,
    val instructions: String,
    val createdAt: Long,
)

@Dao
interface PhotoDao {
    @Query("SELECT * FROM photos ORDER BY importedAt DESC, id DESC")
    fun observeAll(): Flow<List<Photo>>

    @Update
    suspend fun update(photo: Photo)

    @Query("SELECT * FROM photos WHERE id = :id")
    suspend fun find(id: String): Photo?

    @Query("SELECT * FROM photos ORDER BY importedAt")
    suspend fun all(): List<Photo>

    @Insert(onConflict = OnConflictStrategy.ABORT)
    suspend fun insert(photo: Photo)

    @Query("SELECT * FROM handoffs ORDER BY createdAt DESC, id DESC")
    fun observeHandoffs(): Flow<List<Handoff>>

    @Query("SELECT * FROM handoffs WHERE photoId = :id")
    suspend fun findHandoffsForPhoto(id: String): List<Handoff>

    @Query("SELECT * FROM handoffs WHERE id = :id")
    suspend fun findHandoff(id: String): Handoff?

    @Insert(onConflict = OnConflictStrategy.ABORT)
    suspend fun insertHandoff(handoff: Handoff)

    @Query("DELETE FROM handoffs WHERE id = :id")
    suspend fun deleteHandoff(id: String)
}

@Database(entities = [Photo::class, Handoff::class, DriveConnection::class, DriveBackup::class,
    Album::class, AlbumPhoto::class, EditDraft::class, Person::class, FaceRecord::class, FaceScan::class,
    CloudPhoto::class, CloudFolder::class, SyncState::class, DownloadTask::class, DriveSource::class,
    com.myphotosanywhere.mpa.drive.ScanFolder::class, com.myphotosanywhere.mpa.metadata.MetadataState::class,
    com.myphotosanywhere.mpa.importing.ImportJob::class, com.myphotosanywhere.mpa.importing.ImportItem::class,
    com.myphotosanywhere.mpa.importing.TakeoutSidecar::class,
    com.myphotosanywhere.mpa.importing.TakeoutDownload::class], version = 9, exportSchema = true)
abstract class PhotoDatabase : RoomDatabase() {
    abstract fun photos(): PhotoDao
    abstract fun drive(): DriveDao
    abstract fun library(): LibraryDao
    abstract fun cloud(): CloudDao
    abstract fun metadata(): com.myphotosanywhere.mpa.metadata.MetadataDao
    abstract fun imports(): com.myphotosanywhere.mpa.importing.ImportDao
}

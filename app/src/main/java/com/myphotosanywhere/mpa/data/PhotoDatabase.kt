package com.myphotosanywhere.mpa.data

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

    @Query("SELECT * FROM photos WHERE id = :id")
    suspend fun find(id: String): Photo?

    @Query("SELECT * FROM photos ORDER BY importedAt")
    suspend fun all(): List<Photo>

    @Insert(onConflict = OnConflictStrategy.ABORT)
    suspend fun insert(photo: Photo)

    @Query("SELECT * FROM handoffs ORDER BY createdAt DESC, id DESC")
    fun observeHandoffs(): Flow<List<Handoff>>

    @Query("SELECT * FROM handoffs WHERE id = :id")
    suspend fun findHandoff(id: String): Handoff?

    @Insert(onConflict = OnConflictStrategy.ABORT)
    suspend fun insertHandoff(handoff: Handoff)

    @Query("DELETE FROM handoffs WHERE id = :id")
    suspend fun deleteHandoff(id: String)
}

@Database(entities = [Photo::class, Handoff::class, DriveConnection::class, DriveBackup::class], version = 2, exportSchema = true)
abstract class PhotoDatabase : RoomDatabase() {
    abstract fun photos(): PhotoDao
    abstract fun drive(): DriveDao
}

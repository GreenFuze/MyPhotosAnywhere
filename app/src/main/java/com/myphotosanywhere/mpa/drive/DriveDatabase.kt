package com.myphotosanywhere.mpa.drive

import androidx.room.Dao
import androidx.room.Entity
import androidx.room.Index
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Update
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase
import kotlinx.coroutines.flow.Flow

@Entity(tableName = "drive_connection")
data class DriveConnection(
    @PrimaryKey val singleton: Int = 1,
    val accountId: String,
    val email: String,
    val mode: String,
    val folderId: String? = null,
    val folderName: String? = null,
    val backupEnabled: Boolean = false,
    val unmeteredOnly: Boolean = true,
)

fun DriveConnection.binding(): DriveBinding? = folderId?.let { DriveBinding(accountId, it) }

@Entity(tableName = "drive_backups", indices = [Index("bindingKey"), Index("photoId"), Index(value = ["bindingKey", "remoteId"])])
data class DriveBackup(
    @PrimaryKey val id: String,
    val bindingKey: String,
    val photoId: String,
    val accountId: String,
    val email: String,
    val folderId: String,
    val mode: String,
    val remoteId: String? = null,
    val sessionUri: String? = null,
    val state: String = "PENDING",
    val uploadedBytes: Long = 0,
    val errorMessage: String? = null,
    val remoteMd5: String? = null,
    val remoteVersion: String? = null,
)

@Dao
interface DriveDao {
    @Query("SELECT * FROM drive_connection WHERE singleton = 1")
    fun observeConnection(): Flow<DriveConnection?>
    @Query("SELECT * FROM drive_connection WHERE singleton = 1")
    suspend fun connection(): DriveConnection?
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun saveConnection(connection: DriveConnection)
    @Query("DELETE FROM drive_connection")
    suspend fun disconnect()
    @Query("SELECT * FROM drive_backups WHERE bindingKey = :bindingKey ORDER BY photoId")
    suspend fun backups(bindingKey: String): List<DriveBackup>
    @Query("SELECT * FROM drive_backups WHERE bindingKey = :binding AND remoteId = :remote AND state = 'COMPLETE' LIMIT 1")
    suspend fun backupForRemote(binding: String, remote: String): DriveBackup?
    @Query("SELECT * FROM drive_backups ORDER BY photoId")
    fun observeBackups(): Flow<List<DriveBackup>>
    @Query("SELECT * FROM drive_backups WHERE id = :id")
    suspend fun backup(id: String): DriveBackup?
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertBackup(backup: DriveBackup)
    @Update
    suspend fun updateBackup(backup: DriveBackup)
}

val DRIVE_MIGRATION_1_2 = object : Migration(1, 2) {
    override fun migrate(db: SupportSQLiteDatabase) {
        db.execSQL("""CREATE TABLE IF NOT EXISTS drive_connection (
            singleton INTEGER NOT NULL, accountId TEXT NOT NULL, email TEXT NOT NULL,
            mode TEXT NOT NULL, folderId TEXT, folderName TEXT, backupEnabled INTEGER NOT NULL,
            unmeteredOnly INTEGER NOT NULL, PRIMARY KEY(singleton))""".trimIndent())
        db.execSQL("""CREATE TABLE IF NOT EXISTS drive_backups (
            id TEXT NOT NULL, bindingKey TEXT NOT NULL, photoId TEXT NOT NULL,
            accountId TEXT NOT NULL, email TEXT NOT NULL, folderId TEXT NOT NULL, mode TEXT NOT NULL,
            remoteId TEXT, sessionUri TEXT, state TEXT NOT NULL, uploadedBytes INTEGER NOT NULL,
            errorMessage TEXT, remoteMd5 TEXT, remoteVersion TEXT, PRIMARY KEY(id))""".trimIndent())
        db.execSQL("CREATE INDEX IF NOT EXISTS index_drive_backups_bindingKey ON drive_backups(bindingKey)")
        db.execSQL("CREATE INDEX IF NOT EXISTS index_drive_backups_photoId ON drive_backups(photoId)")
    }
}

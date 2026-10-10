package com.myphotosanywhere.mpa.drive

import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

/** A persistent read source. The singleton connection is only the folder-browser selection. */
@Entity(tableName = "drive_sources", indices = [Index("accountId")])
data class DriveSource(
    @PrimaryKey val key: String,
    val accountId: String,
    val email: String,
    val mode: String,
    val folderId: String,
    val folderName: String,
    val backupEnabled: Boolean = false,
    val unmeteredOnly: Boolean = true,
    val enabled: Boolean = true,
) {
    fun connection() = DriveConnection(accountId=accountId,email=email,mode=mode,
        folderId=folderId,folderName=folderName,backupEnabled=backupEnabled,unmeteredOnly=unmeteredOnly)
    companion object {
        fun from(connection: DriveConnection) = DriveSource(
            requireNotNull(connection.binding()).key,connection.accountId,connection.email,connection.mode,
            requireNotNull(connection.folderId),connection.folderName ?: "Photo folder",
            connection.backupEnabled,connection.unmeteredOnly)
    }
}

val SOURCES_MIGRATION_3_4 = object : Migration(3,4) {
    override fun migrate(db: SupportSQLiteDatabase) {
        db.execSQL("""CREATE TABLE drive_sources (key TEXT NOT NULL PRIMARY KEY, accountId TEXT NOT NULL,
            email TEXT NOT NULL, mode TEXT NOT NULL, folderId TEXT NOT NULL, folderName TEXT NOT NULL,
            backupEnabled INTEGER NOT NULL, unmeteredOnly INTEGER NOT NULL, enabled INTEGER NOT NULL)""".trimIndent())
        db.execSQL("CREATE INDEX index_drive_sources_accountId ON drive_sources(accountId)")
        db.query("SELECT accountId,email,mode,folderId,folderName,backupEnabled,unmeteredOnly FROM drive_connection WHERE folderId IS NOT NULL").use { cursor ->
            while(cursor.moveToNext()) {
                val account=cursor.getString(0); val folder=cursor.getString(3)
                db.execSQL("INSERT INTO drive_sources VALUES (?,?,?,?,?,?,?,?,1)",arrayOf(
                    DriveBinding(account,folder).key,account,cursor.getString(1),cursor.getString(2),folder,
                    cursor.getString(4) ?: "Photo folder",cursor.getInt(5),cursor.getInt(6)))
            }
        }
    }
}

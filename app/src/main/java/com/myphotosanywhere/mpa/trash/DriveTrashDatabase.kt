package com.myphotosanywhere.mpa.trash

import androidx.room.*
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

/** Durable intent plus last verified Drive state. Never stores tokens or deletes original bytes. */
@Entity(tableName="drive_trash", indices=[Index("photoId"),Index(value=["accountId","remoteId"]),Index("bindingKey"),Index(value=["updatedAt","key"],orders=[Index.Order.DESC,Index.Order.ASC]),Index(value=["desired","updatedAt","key"])])
internal data class DriveTrashRecord(@PrimaryKey val key: String,val bindingKey: String,val accountId: String,
    val remoteId: String,val photoId: String?,val name: String,val mimeType: String,val bytes: Long,
    val md5: String?,val sha256: String?,val expectedVersion: String?,val thumbnailPath: String,
    val capturedAt: Long?,val confirmed: Boolean=false,val desired: Boolean?=null,
    val error: String?=null,val updatedAt: Long=0,val observedVersion: String?=expectedVersion) {
    init { require(key=="$bindingKey:$remoteId" && bytes>=0) }
}

@Dao
internal interface DriveTrashDao {
    @RawQuery suspend fun entries(query: androidx.sqlite.db.SupportSQLiteQuery): List<DriveTrashRecord>
    @Query("SELECT * FROM drive_trash WHERE key=:key") suspend fun find(key: String): DriveTrashRecord?
    @Query("SELECT * FROM drive_trash WHERE accountId=:account AND remoteId=:remote")
    suspend fun records(account: String,remote: String): List<DriveTrashRecord>
    @Insert(onConflict=OnConflictStrategy.REPLACE) suspend fun save(row: DriveTrashRecord)
    @Query("SELECT * FROM drive_trash WHERE desired IS NOT NULL ORDER BY updatedAt,key LIMIT :limit")
    suspend fun pending(limit: Int=20): List<DriveTrashRecord>
    @Query("""SELECT EXISTS(SELECT 1 FROM import_items i JOIN import_jobs j ON j.id=i.job
        JOIN drive_sources s ON s.key=j.binding WHERE s.accountId=:account AND i.remoteId=:remote
        AND i.state<>'COMPLETE' AND j.state<>'CANCELLED') OR EXISTS(SELECT 1 FROM drive_backups b
        WHERE b.accountId=:account AND b.remoteId=:remote AND b.state<>'COMPLETE')""")
    suspend fun unfinishedTransfer(account: String,remote: String): Boolean
    @Query("SELECT COUNT(*) FROM drive_trash WHERE desired IS NOT NULL") suspend fun pendingCount(): Int
    @Query("""SELECT t.* FROM drive_trash t
        WHERE (t.confirmed=1 OR t.desired IS NOT NULL OR t.error IS NOT NULL)
        AND (:before IS NULL OR t.updatedAt<:before OR (t.updatedAt=:before AND t.key>:after))
        ORDER BY t.updatedAt DESC,t.key LIMIT :limit""")
    suspend fun page(before: Long?,after: String,limit: Int): List<DriveTrashRecord>
    @Query("""SELECT EXISTS(SELECT 1 FROM drive_trash t WHERE t.photoId=:photo AND t.confirmed=1
        AND NOT EXISTS(SELECT 1 FROM cloud_photos c JOIN drive_sources s ON s.key=c.bindingKey
        WHERE c.photoId=:photo AND c.removed=0 AND s.enabled=1 AND NOT EXISTS(
          SELECT 1 FROM drive_trash x WHERE x.accountId=s.accountId AND x.remoteId=c.remoteId AND x.confirmed=1)))""")
    suspend fun hidesPhoto(photo: String): Boolean
    @Query("""UPDATE drive_trash SET confirmed=:trashed,updatedAt=:now,observedVersion=COALESCE(:version,observedVersion),
        expectedVersion=CASE WHEN desired IS NULL OR desired=:trashed THEN COALESCE(:version,expectedVersion) ELSE expectedVersion END,
        desired=CASE WHEN desired=:trashed THEN NULL ELSE desired END,
        error=CASE WHEN desired=:trashed THEN NULL ELSE error END
        WHERE accountId=:account AND remoteId=:remote""")
    suspend fun verifiedState(account: String,remote: String,trashed: Boolean,now: Long,version: String?)
}

internal val TRASH_MIGRATION_9_10=object: Migration(9,10) {
    override fun migrate(db: SupportSQLiteDatabase) {
        db.execSQL("""CREATE TABLE IF NOT EXISTS drive_trash (
            `key` TEXT NOT NULL, bindingKey TEXT NOT NULL, accountId TEXT NOT NULL, remoteId TEXT NOT NULL,
            photoId TEXT, name TEXT NOT NULL, mimeType TEXT NOT NULL, bytes INTEGER NOT NULL,
            md5 TEXT, sha256 TEXT, expectedVersion TEXT, thumbnailPath TEXT NOT NULL, capturedAt INTEGER,
            confirmed INTEGER NOT NULL, desired INTEGER, error TEXT, updatedAt INTEGER NOT NULL, observedVersion TEXT, PRIMARY KEY(`key`))""")
        db.execSQL("CREATE INDEX index_drive_trash_photoId ON drive_trash(photoId)")
        db.execSQL("CREATE INDEX index_drive_trash_accountId_remoteId ON drive_trash(accountId,remoteId)")
        db.execSQL("CREATE INDEX index_drive_trash_bindingKey ON drive_trash(bindingKey)")
        db.execSQL("CREATE INDEX index_drive_trash_updatedAt_key ON drive_trash(updatedAt DESC,key ASC)")
        db.execSQL("CREATE INDEX index_drive_trash_desired_updatedAt_key ON drive_trash(desired,updatedAt,key)")
    }
}

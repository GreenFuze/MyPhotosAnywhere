package com.myphotosanywhere.mpa.importing

import androidx.room.*
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase
import kotlinx.coroutines.flow.Flow

/** Destination is captured once. Workers never consult the current default to retarget a job. */
@Entity(tableName = "import_jobs")
data class ImportJob(@PrimaryKey val id: String, val binding: String, val kind: String,
    val email: String = "", val session: String? = null, val pickerUri: String? = null,
    val state: String = "PREVIEW", val error: String? = null, val created: Long = System.currentTimeMillis())

@Entity(tableName = "import_items", indices = [Index("job")])
data class ImportItem(@PrimaryKey val id: String, val job: String, val uri: String, val path: String,
    val name: String, val mime: String, val size: Long, val crc: Long = -1,
    val metadata: String? = null, val album: String = "", val selected: Boolean = true,
    val state: String = "PENDING", val error: String? = null, val sha256: String? = null,
    val md5: String? = null, val staged: String? = null, val remoteId: String? = null,
    val sessionUri: String? = null, val offset: Long = 0, val photoId: String? = null)

/** JSON is never a gallery item. A revision-specific row survives process death and cleanup retries. */
@Entity(tableName = "takeout_sidecars", indices = [Index("binding")])
data class TakeoutSidecar(@PrimaryKey val key: String, val binding: String, val remoteId: String,
    val parent: String, val name: String, val version: String?, val md5: String?, val bytes: Long,
    val state: String = "PENDING", val json: String? = null, val photoId: String? = null,
    val error: String? = null)

@Entity(tableName = "takeout_downloads")
data class TakeoutDownload(@PrimaryKey val job: String, val sealedLink: String,
    val bytes: Long = 0, val total: Long = -1, val etag: String? = null,
    val state: String = "DOWNLOADING", val autoStart: Boolean = true, val error: String? = null)

@Dao
interface ImportDao {
    @Query("""UPDATE import_jobs SET state='RUNNING',error=NULL WHERE id=:id
        AND (state='WAITING_FOR_APP' OR (state='RUNNING' AND error=:legacyMessage))""")
    suspend fun claimForegroundResume(id: String, legacyMessage: String): Int
    @Query("UPDATE import_jobs SET error=NULL WHERE id=:id AND state='RUNNING'")
    suspend fun clearRunningError(id: String)
    @Query("UPDATE import_jobs SET error=NULL WHERE id=:id") suspend fun clearJobError(id: String)
    @Query("UPDATE import_items SET error=NULL WHERE job=:id") suspend fun clearItemErrors(id: String)
    @Query("UPDATE takeout_downloads SET error=NULL WHERE job=:id") suspend fun clearDownloadError(id: String)
    @Query("UPDATE import_jobs SET state=:state,error=:message WHERE id=:id AND state='RUNNING'")
    suspend fun connectionState(id: String, state: String, message: String?): Int
    @Query("SELECT * FROM import_jobs WHERE id=:id") fun observeJob(id: String): Flow<ImportJob?>
    @Query("SELECT * FROM import_items WHERE job=:id ORDER BY path,id") fun observeJobItems(id: String): Flow<List<ImportItem>>
    @Query("SELECT * FROM takeout_downloads") fun observeDownloads(): Flow<List<TakeoutDownload>>
    @Query("SELECT * FROM takeout_downloads WHERE job=:id") suspend fun download(id: String): TakeoutDownload?
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun saveDownload(download: TakeoutDownload)
    @Query("SELECT * FROM import_jobs ORDER BY created DESC") fun observeJobs(): Flow<List<ImportJob>>
    @Query("SELECT * FROM import_jobs ORDER BY created") suspend fun jobs(): List<ImportJob>
    @Query("SELECT * FROM import_jobs WHERE id=:id") suspend fun job(id: String): ImportJob?
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun saveJob(job: ImportJob)
    @Query("SELECT * FROM import_items ORDER BY job,path") fun observeItems(): Flow<List<ImportItem>>
    @Query("SELECT * FROM import_items WHERE job=:id ORDER BY path,id") suspend fun items(id: String): List<ImportItem>
    @Query("SELECT * FROM import_items WHERE id=:id") suspend fun item(id: String): ImportItem?
    @Insert(onConflict = OnConflictStrategy.IGNORE) suspend fun insertItem(item: ImportItem)
    @Update suspend fun updateItem(item: ImportItem)
    @Query("SELECT * FROM takeout_sidecars ORDER BY binding,name") fun observeSidecars(): Flow<List<TakeoutSidecar>>
    @Query("SELECT * FROM takeout_sidecars WHERE binding=:binding") suspend fun sidecars(binding: String): List<TakeoutSidecar>
    @Query("SELECT * FROM takeout_sidecars WHERE key=:key") suspend fun sidecar(key: String): TakeoutSidecar?
    @Insert(onConflict = OnConflictStrategy.IGNORE) suspend fun insertSidecar(item: TakeoutSidecar)
    @Update suspend fun updateSidecar(item: TakeoutSidecar)
}

val TAKEOUT_DOWNLOAD_MIGRATION_7_8 = object : Migration(7, 8) {
    override fun migrate(db: SupportSQLiteDatabase) {
        db.execSQL("""CREATE TABLE IF NOT EXISTS takeout_downloads (job TEXT NOT NULL PRIMARY KEY,
            sealedLink TEXT NOT NULL, bytes INTEGER NOT NULL, total INTEGER NOT NULL, etag TEXT,
            state TEXT NOT NULL, autoStart INTEGER NOT NULL, error TEXT)""")
    }
}

val IMPORT_MIGRATION_6_7 = object : Migration(6, 7) {
    override fun migrate(db: SupportSQLiteDatabase) {
        db.execSQL("ALTER TABLE photos ADD COLUMN importedMetadata TEXT NOT NULL DEFAULT ''")
        db.execSQL("""CREATE TABLE IF NOT EXISTS import_jobs (id TEXT NOT NULL PRIMARY KEY, binding TEXT NOT NULL,
            kind TEXT NOT NULL, email TEXT NOT NULL, session TEXT, pickerUri TEXT, state TEXT NOT NULL,
            error TEXT, created INTEGER NOT NULL)""")
        db.execSQL("""CREATE TABLE IF NOT EXISTS import_items (id TEXT NOT NULL PRIMARY KEY, job TEXT NOT NULL,
            uri TEXT NOT NULL, path TEXT NOT NULL, name TEXT NOT NULL, mime TEXT NOT NULL, size INTEGER NOT NULL,
            crc INTEGER NOT NULL, metadata TEXT, album TEXT NOT NULL, selected INTEGER NOT NULL, state TEXT NOT NULL,
            error TEXT, sha256 TEXT, md5 TEXT, staged TEXT, remoteId TEXT, sessionUri TEXT, offset INTEGER NOT NULL, photoId TEXT)""")
        db.execSQL("CREATE INDEX IF NOT EXISTS index_import_items_job ON import_items(job)")
        db.execSQL("""CREATE TABLE IF NOT EXISTS takeout_sidecars (key TEXT NOT NULL PRIMARY KEY, binding TEXT NOT NULL,
            remoteId TEXT NOT NULL, parent TEXT NOT NULL, name TEXT NOT NULL, version TEXT, md5 TEXT,
            bytes INTEGER NOT NULL, state TEXT NOT NULL, json TEXT, photoId TEXT, error TEXT)""")
        db.execSQL("CREATE INDEX IF NOT EXISTS index_takeout_sidecars_binding ON takeout_sidecars(binding)")
        db.execSQL("DROP TRIGGER IF EXISTS mpa_xmp_photos_update")
        com.myphotosanywhere.mpa.metadata.installMetadataTriggers(db)
    }
}

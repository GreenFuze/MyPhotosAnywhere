package com.myphotosanywhere.mpa.data

import androidx.room.*
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

/** References belong to MediaStore; their source files are never disposable MPA cache. */
@Entity(tableName="device_media",indices=[Index("uri"),Index("available")])
internal data class DeviceMediaRecord(@PrimaryKey val photoId: String,val uri: String,val volume: String,
    val providerVersion: String,val revision: String,val width: Int,val height: Int,val orientation: Int,
    val available: Boolean)
@Entity(tableName="device_scan_seen")
internal data class DeviceScanSeen(@PrimaryKey val photoId: String,val scan: String)

/** Explicit authorization is scoped to an immutable task and captured destination. */
@Entity(tableName="manual_device_backups",indices=[Index("photoId")])
internal data class ManualDeviceBackup(@PrimaryKey val taskId: String,val photoId: String,
    val bindingKey: String,val sha256: String,val bytes: Long)

/** A canonical person can have several provider names, including names before a rename. */
@Entity(tableName="person_aliases",indices=[Index("personId")])
internal data class PersonAlias(@PrimaryKey val key: String,val name: String,val binding: String?,
    val personId: String?,val ignored: Boolean=false)
@Entity(tableName="photo_people",primaryKeys=["photoId","aliasKey"],indices=[Index("personId"),Index("aliasKey")])
internal data class PhotoPersonLabel(val photoId: String,val aliasKey: String,val personId: String,@ColumnInfo(defaultValue="0") val excluded: Boolean=false)
@Entity(tableName="photo_people_origins")
internal data class PhotoPeopleOrigin(@PrimaryKey val photoId: String,val binding: String?)
@Entity(tableName="people_index_queue")
internal data class PeopleIndexItem(@PrimaryKey val photoId: String,val error: String?=null)

@Dao
internal interface DeviceMediaDao {
    @Query("SELECT * FROM device_media WHERE photoId=:id") suspend fun find(id: String): DeviceMediaRecord?
    @Query("SELECT * FROM device_media WHERE photoId=:id") fun observe(id: String): kotlinx.coroutines.flow.Flow<DeviceMediaRecord?>
    @Query("SELECT * FROM device_media WHERE uri=:uri ORDER BY available DESC,photoId LIMIT 20") suspend fun byUri(uri: String): List<DeviceMediaRecord>
    @Insert(onConflict=OnConflictStrategy.REPLACE) suspend fun save(record: DeviceMediaRecord)
    @Insert(onConflict=OnConflictStrategy.REPLACE) suspend fun seen(record: DeviceScanSeen)
    @Query("UPDATE device_media SET available=0 WHERE uri=:uri AND photoId<>:id AND available=1") suspend fun supersede(uri: String,id: String)
    @Query("UPDATE device_media SET available=0 WHERE available=1 AND NOT EXISTS(SELECT 1 FROM device_scan_seen s WHERE s.photoId=device_media.photoId AND s.scan=:seen)") suspend fun finish(seen: String)
    @Query("UPDATE device_media SET available=0 WHERE available=1") suspend fun revoke()
    @Query("SELECT COUNT(*) FROM device_media WHERE available=1") suspend fun count(): Int
    @Insert(onConflict=OnConflictStrategy.ABORT) suspend fun authorize(task: ManualDeviceBackup)
    @Query("SELECT * FROM manual_device_backups WHERE taskId=:id") suspend fun authorization(id: String): ManualDeviceBackup?
    @Query("SELECT m.* FROM manual_device_backups m JOIN drive_backups b ON b.id=m.taskId WHERE b.state<>'COMPLETE' AND m.taskId>:after ORDER BY m.taskId LIMIT :limit")
    suspend fun pendingManual(after: String,limit: Int): List<ManualDeviceBackup>
    @Query("SELECT * FROM manual_device_backups WHERE photoId=:id") suspend fun authorizations(id: String): List<ManualDeviceBackup>
}

@Dao
internal interface PersonLabelsDao {
    @Query("SELECT * FROM photo_people_origins WHERE photoId=:id") suspend fun origin(id: String): PhotoPeopleOrigin?
    @Insert(onConflict=OnConflictStrategy.IGNORE) suspend fun saveOrigin(origin: PhotoPeopleOrigin)
    @Insert(onConflict=OnConflictStrategy.REPLACE) suspend fun restoreOrigin(origin: PhotoPeopleOrigin)
    @Query("UPDATE photo_people SET personId=:person WHERE aliasKey=:alias") suspend fun redirectAlias(alias: String,person: String)
    @Query("DELETE FROM photo_people WHERE aliasKey=:alias") suspend fun clearAlias(alias: String)
    @Query("UPDATE photo_people SET excluded=1 WHERE photoId=:photo AND personId=:person") suspend fun exclude(photo: String,person: String)
    @Query("SELECT * FROM person_aliases WHERE ignored=1 AND binding IS :binding") suspend fun ignored(binding: String?): List<PersonAlias>
    @Query("SELECT * FROM people WHERE id=:id") suspend fun person(id: String): Person?
    @Query("""SELECT EXISTS(SELECT 1 FROM faces WHERE personId=:id) OR EXISTS(SELECT 1 FROM person_aliases WHERE personId=:id)
        OR EXISTS(SELECT 1 FROM photo_people WHERE personId=:id)""") suspend fun references(id: String): Boolean
    @Query("SELECT * FROM person_aliases WHERE `key`=:key") suspend fun alias(key: String): PersonAlias?
    @Query("SELECT * FROM person_aliases WHERE personId=:id") suspend fun aliases(id: String): List<PersonAlias>
    @Insert(onConflict=OnConflictStrategy.REPLACE) suspend fun saveAlias(alias: PersonAlias)
    @Insert(onConflict=OnConflictStrategy.REPLACE) suspend fun saveMember(member: PhotoPersonLabel)
    @Query("SELECT * FROM photo_people WHERE photoId=:id ORDER BY aliasKey") suspend fun members(id: String): List<PhotoPersonLabel>
    @Query("SELECT * FROM photo_people WHERE photoId=:id ORDER BY aliasKey") fun observeMembers(id: String): kotlinx.coroutines.flow.Flow<List<PhotoPersonLabel>>
    @Query("SELECT * FROM photo_people WHERE photoId IN (:ids)") suspend fun membersForPhotos(ids: List<String>): List<PhotoPersonLabel>
    @Query("SELECT * FROM photo_people") suspend fun allMembers(): List<PhotoPersonLabel>
    @Query("DELETE FROM photo_people WHERE photoId=:id") suspend fun clearPhoto(id: String)
    @Query("DELETE FROM photo_people WHERE personId=:id") suspend fun clearPerson(id: String)
    @Query("UPDATE person_aliases SET personId=:target WHERE personId=:source") suspend fun mergeAliases(source: String,target: String)
    @Query("UPDATE photo_people SET personId=:target WHERE personId=:source") suspend fun mergeMembers(source: String,target: String)
    @Query("UPDATE person_aliases SET personId=NULL,ignored=1 WHERE personId=:id") suspend fun ignorePerson(id: String)
    @Query("SELECT photoId FROM people_index_queue WHERE error IS NULL ORDER BY photoId LIMIT :limit") suspend fun pending(limit: Int): List<String>
    @Query("DELETE FROM people_index_queue WHERE photoId=:id") suspend fun indexed(id: String)
    @Query("UPDATE people_index_queue SET error=:message WHERE photoId=:id") suspend fun failed(id: String,message: String)
    @Query("SELECT COUNT(*) FROM people_index_queue WHERE error IS NOT NULL") suspend fun errors(): Int
    @Query("UPDATE people_index_queue SET error=NULL") suspend fun retry()
    @Insert(onConflict=OnConflictStrategy.IGNORE) suspend fun queue(item: PeopleIndexItem)
}

/** Additive only: every existing table, identity, original and checkpoint is untouched. */
internal val LIBRARY_FOLLOWUP_MIGRATION_10_11=object: Migration(10,11) {
    override fun migrate(db: SupportSQLiteDatabase) {
        db.execSQL("""CREATE TABLE IF NOT EXISTS device_media (photoId TEXT NOT NULL PRIMARY KEY,uri TEXT NOT NULL,volume TEXT NOT NULL,
            providerVersion TEXT NOT NULL,revision TEXT NOT NULL,width INTEGER NOT NULL,height INTEGER NOT NULL,orientation INTEGER NOT NULL,
            available INTEGER NOT NULL)""")
        db.execSQL("CREATE TABLE IF NOT EXISTS device_scan_seen (photoId TEXT NOT NULL PRIMARY KEY,scan TEXT NOT NULL)")
        db.execSQL("CREATE INDEX IF NOT EXISTS index_device_media_uri ON device_media(uri)")
        db.execSQL("CREATE INDEX IF NOT EXISTS index_device_media_available ON device_media(available)")
        db.execSQL("""CREATE TABLE IF NOT EXISTS manual_device_backups (taskId TEXT NOT NULL PRIMARY KEY,photoId TEXT NOT NULL,
            bindingKey TEXT NOT NULL,sha256 TEXT NOT NULL,bytes INTEGER NOT NULL)""")
        db.execSQL("CREATE INDEX IF NOT EXISTS index_manual_device_backups_photoId ON manual_device_backups(photoId)")
        db.execSQL("""CREATE TABLE IF NOT EXISTS person_aliases (`key` TEXT NOT NULL PRIMARY KEY,name TEXT NOT NULL,binding TEXT,
            personId TEXT,ignored INTEGER NOT NULL)""")
        db.execSQL("CREATE INDEX IF NOT EXISTS index_person_aliases_personId ON person_aliases(personId)")
        db.execSQL("""CREATE TABLE IF NOT EXISTS photo_people (photoId TEXT NOT NULL,aliasKey TEXT NOT NULL,personId TEXT NOT NULL,
            excluded INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(photoId,aliasKey))""")
        db.execSQL("CREATE INDEX IF NOT EXISTS index_photo_people_personId ON photo_people(personId)")
        db.execSQL("CREATE INDEX IF NOT EXISTS index_photo_people_aliasKey ON photo_people(aliasKey)")
        db.execSQL("CREATE TABLE IF NOT EXISTS photo_people_origins (photoId TEXT NOT NULL PRIMARY KEY,binding TEXT)")
        db.execSQL("CREATE TABLE IF NOT EXISTS people_index_queue (photoId TEXT NOT NULL PRIMARY KEY,error TEXT)")
        installPersonLabelTriggers(db)
        com.myphotosanywhere.mpa.metadata.installMetadataTriggers(db)
        db.execSQL("INSERT OR IGNORE INTO people_index_queue(photoId) SELECT id FROM photos WHERE importedMetadata<>''")
    }
}

internal fun installPersonLabelTriggers(db: SupportSQLiteDatabase) {
    db.execSQL("""CREATE TRIGGER IF NOT EXISTS mpa_people_photo_insert AFTER INSERT ON photos WHEN NEW.importedMetadata<>''
        BEGIN DELETE FROM people_index_queue WHERE photoId=NEW.id; INSERT INTO people_index_queue(photoId) VALUES(NEW.id); END""")
    db.execSQL("""CREATE TRIGGER IF NOT EXISTS mpa_people_photo_update AFTER UPDATE OF importedMetadata,sourceBindingKey ON photos
        WHEN NEW.importedMetadata<>OLD.importedMetadata OR NEW.sourceBindingKey IS NOT OLD.sourceBindingKey
        BEGIN DELETE FROM people_index_queue WHERE photoId=NEW.id; INSERT INTO people_index_queue(photoId) VALUES(NEW.id); END""")
    db.execSQL("""CREATE TRIGGER IF NOT EXISTS mpa_people_photo_delete AFTER DELETE ON photos BEGIN
        DELETE FROM photo_people WHERE photoId=OLD.id; DELETE FROM photo_people_origins WHERE photoId=OLD.id; DELETE FROM people_index_queue WHERE photoId=OLD.id;
        DELETE FROM device_media WHERE photoId=OLD.id; DELETE FROM device_scan_seen WHERE photoId=OLD.id; END""")
}

/** Visible tiles subscribe only to a boolean; upload progress never invalidates timeline paging. */
@Dao
internal interface LibraryPresenceDao {
    @Query("""SELECT EXISTS(SELECT 1 FROM photos p JOIN cloud_photos c ON c.photoId=p.id
        JOIN drive_sources s ON s.key=c.bindingKey AND s.enabled=1
        WHERE p.id=:id AND c.removed=0 AND c.bytes=p.bytes AND c.version=p.cachedVersion
        AND (c.sha256 IS NULL OR c.sha256=p.sha256)
        AND NOT EXISTS(SELECT 1 FROM drive_trash t WHERE t.accountId=s.accountId AND t.remoteId=c.remoteId AND t.confirmed=1))
        OR EXISTS(SELECT 1 FROM manual_device_backups m JOIN drive_backups b ON b.id=m.taskId
        JOIN photos p ON p.id=m.photoId JOIN drive_sources s ON s.key=b.bindingKey AND s.enabled=1 AND s.accountId=b.accountId AND s.folderId=b.folderId
        WHERE p.id=:id AND b.state='COMPLETE' AND m.bindingKey=b.bindingKey AND m.photoId=b.photoId AND m.sha256=p.sha256 AND m.bytes=p.bytes AND b.uploadedBytes=p.bytes
        AND b.remoteId IS NOT NULL AND b.remoteVersion IS NOT NULL AND b.remoteMd5 IS NOT NULL
        AND NOT EXISTS(SELECT 1 FROM cloud_photos c WHERE c.bindingKey=b.bindingKey AND c.remoteId=b.remoteId
          AND (c.removed=1 OR c.version<>b.remoteVersion OR c.bytes<>p.bytes))
        AND NOT EXISTS(SELECT 1 FROM drive_trash t WHERE t.accountId=b.accountId AND t.remoteId=b.remoteId AND t.confirmed=1))""")
    fun inDrive(id: String): kotlinx.coroutines.flow.Flow<Boolean>
}

package com.myphotosanywhere.mpa.metadata

import androidx.room.RoomDatabase
import androidx.sqlite.db.SupportSQLiteDatabase

/** Annotation writes and their outbox invalidation commit together, even if the process dies immediately. */
fun installMetadataTriggers(db: SupportSQLiteDatabase) {
    db.execSQL("""INSERT OR IGNORE INTO metadata_states(bindingKey,generation,uploadedGeneration,remoteId,etag,error)
        SELECT key,1,0,NULL,NULL,NULL FROM drive_sources WHERE enabled=1""")
    val tables=mapOf(
        "photos" to "favorite,archived,placeLabel,latitude,longitude,capturedAt,parentId,editProvider,editInstructions,labels,metadataAssetId,importedMetadata",
        "faces" to "personId,corrected,left,top,right,bottom,imageWidth,imageHeight,modelVersion",
        "people" to "name", "albums" to "name", "album_photos" to "albumId,photoId", "edit_drafts" to "recipe",
        "drive_backups" to "state,remoteId,remoteMd5,remoteVersion",
        "person_aliases" to "name,binding,personId,ignored", "photo_people" to "aliasKey,personId,excluded",
        "photo_people_origins" to "binding")
    // Earlier migrations also install this outbox. New tables are installed only
    // after the additive migration has created them; never reference future tables.
    val available=db.query("SELECT name FROM sqlite_master WHERE type='table'").use { cursor ->
        buildSet { while(cursor.moveToNext()) add(cursor.getString(0)) }
    }
    for((table,columns) in tables.filterKeys { it in available }) for(event in listOf("INSERT","DELETE","UPDATE OF $columns")) {
        val suffix=event.substringBefore(' ').lowercase()
        val changed=if(suffix=="update") "WHEN "+columns.split(',').joinToString(" OR ") { "OLD.`$it` IS NOT NEW.`$it`" } else ""
        db.execSQL("""CREATE TRIGGER IF NOT EXISTS mpa_xmp_${table}_$suffix AFTER $event ON `$table` $changed BEGIN
            UPDATE metadata_states SET generation=generation+1
            WHERE bindingKey IN (SELECT key FROM drive_sources WHERE enabled=1); END""")
    }
    db.execSQL("""CREATE TRIGGER IF NOT EXISTS mpa_xmp_source_insert AFTER INSERT ON drive_sources BEGIN
        INSERT OR IGNORE INTO metadata_states(bindingKey,generation,uploadedGeneration,remoteId,etag,error)
        VALUES (NEW.key,1,0,NULL,NULL,NULL); END""")
}
val METADATA_CALLBACK=object: RoomDatabase.Callback() {
    override fun onCreate(db: SupportSQLiteDatabase) { installMetadataTriggers(db) }
}

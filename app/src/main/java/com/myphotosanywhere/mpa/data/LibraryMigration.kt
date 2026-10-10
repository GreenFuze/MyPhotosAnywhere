package com.myphotosanywhere.mpa.data

import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

val LIBRARY_MIGRATION_2_3 = object : Migration(2, 3) {
    override fun migrate(db: SupportSQLiteDatabase) {
        db.execSQL("CREATE INDEX index_drive_backups_bindingKey_remoteId ON drive_backups(bindingKey, remoteId)")
        listOf("capturedAt INTEGER", "favorite INTEGER NOT NULL DEFAULT 0", "archived INTEGER NOT NULL DEFAULT 0",
            "placeLabel TEXT NOT NULL DEFAULT ''", "sourceBindingKey TEXT", "sourceRemoteId TEXT", "cachedVersion TEXT",
            "pinned INTEGER NOT NULL DEFAULT 0", "lastAccess INTEGER NOT NULL DEFAULT 0", "labels TEXT NOT NULL DEFAULT ''")
            .forEach { db.execSQL("ALTER TABLE photos ADD COLUMN $it") }
        db.execSQL("CREATE TABLE albums (id TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL, createdAt INTEGER NOT NULL, pinned INTEGER NOT NULL)")
        db.execSQL("CREATE TABLE album_photos (albumId TEXT NOT NULL, photoId TEXT NOT NULL, PRIMARY KEY(albumId, photoId))")
        db.execSQL("CREATE INDEX index_album_photos_photoId ON album_photos(photoId)")
        db.execSQL("CREATE TABLE edit_drafts (photoId TEXT NOT NULL PRIMARY KEY, recipe TEXT NOT NULL, updatedAt INTEGER NOT NULL)")
        db.execSQL("CREATE TABLE people (id TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL)")
        db.execSQL("""CREATE TABLE faces (id TEXT NOT NULL PRIMARY KEY, photoId TEXT NOT NULL, cropPath TEXT NOT NULL,
            embedding BLOB, personId TEXT, suggestedPersonId TEXT, similarity REAL, corrected INTEGER NOT NULL)""")
        db.execSQL("CREATE INDEX index_faces_photoId ON faces(photoId)")
        db.execSQL("CREATE INDEX index_faces_personId ON faces(personId)")
        db.execSQL("CREATE TABLE face_scans (photoId TEXT NOT NULL PRIMARY KEY, sha256 TEXT NOT NULL, error TEXT)")
        db.execSQL("""CREATE TABLE cloud_photos (key TEXT NOT NULL PRIMARY KEY, bindingKey TEXT NOT NULL, remoteId TEXT NOT NULL,
            parentId TEXT NOT NULL, name TEXT NOT NULL, mimeType TEXT NOT NULL, bytes INTEGER NOT NULL, md5 TEXT, version TEXT,
            modifiedTime TEXT, capturedAt INTEGER, latitude REAL, longitude REAL, thumbnailPath TEXT NOT NULL,
            thumbnailVersion TEXT, photoId TEXT, removed INTEGER NOT NULL, seenGeneration TEXT NOT NULL, assetId TEXT, sourceAssetId TEXT, thumbnailUpdatedAt INTEGER NOT NULL)""")
        db.execSQL("CREATE INDEX index_cloud_photos_bindingKey ON cloud_photos(bindingKey)")
        db.execSQL("CREATE INDEX index_cloud_photos_photoId ON cloud_photos(photoId)")
        db.execSQL("""CREATE TABLE cloud_folders (bindingKey TEXT NOT NULL, remoteId TEXT NOT NULL, parentId TEXT,
            name TEXT NOT NULL, seenGeneration TEXT NOT NULL, PRIMARY KEY(bindingKey, remoteId))""")
        db.execSQL("""CREATE TABLE sync_states (bindingKey TEXT NOT NULL PRIMARY KEY, changeToken TEXT,
            lastScan INTEGER NOT NULL, error TEXT, running INTEGER NOT NULL)""")
        db.execSQL("""CREATE TABLE downloads (key TEXT NOT NULL PRIMARY KEY, bindingKey TEXT NOT NULL,
            remoteId TEXT NOT NULL, pinned INTEGER NOT NULL, state TEXT NOT NULL, error TEXT)""")
        db.execSQL("CREATE INDEX index_downloads_bindingKey ON downloads(bindingKey)")
    }
}

/** Preserve reviewed identities; old geometry is unknown and must never be guessed from face order. */
val METADATA_MIGRATION_4_5 = object : Migration(4, 5) {
    override fun migrate(db: SupportSQLiteDatabase) {
        db.execSQL("ALTER TABLE photos ADD COLUMN metadataAssetId TEXT")
        listOf("left REAL", "top REAL", "right REAL", "bottom REAL", "imageWidth INTEGER",
            "imageHeight INTEGER", "modelVersion TEXT").forEach { db.execSQL("ALTER TABLE faces ADD COLUMN $it") }
        db.execSQL("ALTER TABLE face_scans ADD COLUMN modelVersion TEXT NOT NULL DEFAULT ''")
        db.execSQL("""CREATE TABLE metadata_states (bindingKey TEXT NOT NULL PRIMARY KEY, generation INTEGER NOT NULL,
            uploadedGeneration INTEGER NOT NULL, remoteId TEXT, etag TEXT, error TEXT)""")
    }
}

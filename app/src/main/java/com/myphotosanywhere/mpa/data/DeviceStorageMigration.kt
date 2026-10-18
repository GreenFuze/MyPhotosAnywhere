package com.myphotosanywhere.mpa.data

import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

/** Adds directory references and explicit metadata receipts; existing identities/jobs are unchanged. */
internal val DEVICE_STORAGE_MIGRATION_11_12=object: Migration(11,12) {
    override fun migrate(db: SupportSQLiteDatabase) {
        db.execSQL("ALTER TABLE device_media ADD COLUMN folderPath TEXT NOT NULL DEFAULT ''")
        db.execSQL("ALTER TABLE device_media ADD COLUMN folderName TEXT NOT NULL DEFAULT ''")
        db.execSQL("CREATE INDEX IF NOT EXISTS index_device_media_volume_folderPath_available ON device_media(volume,folderPath,available)")
        db.execSQL("ALTER TABLE manual_device_backups ADD COLUMN metadataRequested INTEGER NOT NULL DEFAULT 0")
        db.execSQL("ALTER TABLE manual_device_backups ADD COLUMN metadataId TEXT")
        db.execSQL("ALTER TABLE manual_device_backups ADD COLUMN metadataSha TEXT")
        db.execSQL("ALTER TABLE manual_device_backups ADD COLUMN metadataEtag TEXT")
    }
}

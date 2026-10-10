package com.myphotosanywhere.mpa.drive

import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

val SCAN_MIGRATION_5_6 = object : Migration(5, 6) {
    override fun migrate(db: SupportSQLiteDatabase) {
        db.execSQL("ALTER TABLE sync_states ADD COLUMN scanGeneration TEXT")
        db.execSQL("ALTER TABLE sync_states ADD COLUMN scanToken TEXT")
        db.execSQL("ALTER TABLE sync_states ADD COLUMN scanPages INTEGER NOT NULL DEFAULT 0")
        db.execSQL("ALTER TABLE sync_states ADD COLUMN lastSuccess INTEGER NOT NULL DEFAULT 0")
        db.execSQL("ALTER TABLE sync_states ADD COLUMN status TEXT NOT NULL DEFAULT 'WAITING'")
        // A killed old worker must not look active after upgrading.
        db.execSQL("UPDATE sync_states SET running = 0")
        db.execSQL("""CREATE TABLE scan_folders (bindingKey TEXT NOT NULL, remoteId TEXT NOT NULL,
            pageToken TEXT, complete INTEGER NOT NULL, PRIMARY KEY(bindingKey, remoteId))""")
    }
}
